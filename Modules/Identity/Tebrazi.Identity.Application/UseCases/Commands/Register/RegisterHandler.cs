using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.Identity.Application.Configuration;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.Register;

/// <summary>
/// Port of <c>POST /api/auth/register</c>. The five writes happen inside one transaction, as in
/// the Prisma <c>$transaction</c> the original used — a half-created tenant (an organization
/// with no owner, or a user with no subscription) is not a recoverable state.
/// </summary>
public sealed class RegisterHandler(
    IIdentityDbContext dbContext,
    IUserReadStore users,
    IUserWriteStore userWriter,
    IOrganizationStore organizations,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator tokens,
    IAppLogger<RegisterHandler> logger) : IRequestHandler<RegisterCommand, RegisterResponse>
{
    public async Task<RegisterResponse> Handle(RegisterCommand request, CancellationToken cancellationToken = default)
    {
        // An unrecognised userType silently becomes PATIENT, as in the original — the client
        // relies on that for its patient signup form, which posts no userType at all.
        var userType = Enum.TryParse<UserType>(request.UserType, ignoreCase: false, out var parsed)
            ? parsed
            : UserType.PATIENT;

        var displayName = request.DisplayName?.Trim();
        var email = request.Email?.Trim();
        var normalizedPhone = NormalizePhone(request.Phone);

        Validate(displayName, email, request.Password, request.OrganizationName, userType);

        // Uniqueness is per (email, userType): the same address may hold one physician and one
        // patient account, so this check must be narrowed by type or it rejects a legal signup.
        if (!string.IsNullOrEmpty(email) &&
            await users.EmailExistsAsync(email, userType, cancellationToken))
        {
            throw new ConflictException(
                $"A {userType.ToString().ToLowerInvariant()} account with this email already exists");
        }

        if (normalizedPhone is not null && await users.PhoneExistsAsync(normalizedPhone, cancellationToken))
            throw new ConflictException("An account with this phone number already exists");

        var organizationName = string.IsNullOrWhiteSpace(request.OrganizationName)
            ? $"{displayName}'s Health"
            : request.OrganizationName.Trim();

        var slug = await ReserveSlugAsync(organizationName, cancellationToken);
        var plan = PlanCatalog.Resolve(request.Plan);
        var selfService = PlanCatalog.IsSelfService(plan);
        var passwordHash = passwordHasher.Hash(request.Password!);

        var organization = Organization.Create(organizationName, slug);

        var user = User.Create(
            displayName: displayName!,
            passwordHash: passwordHash,
            userType: userType,
            email: email,
            phone: normalizedPhone,
            // Whoever signs up owns the tenant they just created.
            role: UserRole.ADMIN,
            currentOrganizationId: organization.Id);

        var now = DateTime.UtcNow;
        var periodEnd = selfService
            ? now.AddYears(100)   // self-service never expires
            : now.AddDays(14);    // paid plans open a 14-day trial

        // One retriable unit. Everything inside is database work only, so a transient-fault
        // retry re-runs it safely — the entities were built above and are not regenerated here.
        await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            organizations.Add(organization);
            userWriter.Add(user);

            if (userType == UserType.PHYSICIAN &&
                !string.IsNullOrWhiteSpace(request.LicenseNumber) &&
                !string.IsNullOrWhiteSpace(request.Specialty))
            {
                userWriter.Add(PhysicianProfile.Create(user.Id, request.LicenseNumber, request.Specialty));
            }

            userWriter.Add(OrganizationMember.Create(organization.Id, user.Id, OrgRole.OWNER));

            organizations.Add(Subscription.Create(
                organization.Id,
                plan,
                selfService ? SubscriptionStatus.ACTIVE : SubscriptionStatus.TRIALING,
                now,
                periodEnd));

            await dbContext.SaveChangesAsync(ct);
        }, cancellationToken);

        var (token, _) = tokens.Generate(user, organization.Id);

        logger.Information("Registration completed", new
        {
            UserId = user.Id,
            OrganizationId = organization.Id,
            Plan = plan.ToString()
        });

        return new RegisterResponse(
            token,
            new AuthUserResponse(
                Id: user.Id,
                Email: user.Email,
                Name: user.DisplayName,
                DisplayName: user.DisplayName,
                Role: user.Role.ToString(),
                UserType: user.UserType.ToString(),
                Phone: user.Phone,
                ProfilePictureUrl: user.ProfilePictureUrl,
                OrganizationId: organization.Id,
                OrganizationName: organization.Name),
            Plan: plan.ToString(),
            PlanStatus: selfService ? "ACTIVE" : "TRIALING");
    }

    /// <summary>
    /// Validation order matters: the client surfaces the first message it receives, and the
    /// existing signup screens were built against this sequence.
    /// </summary>
    private static void Validate(
        string? displayName,
        string? email,
        string? password,
        string? organizationName,
        UserType userType)
    {
        if (string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new ValidationException("Name, email, and password are required");
        }

        if (userType != UserType.PATIENT && string.IsNullOrWhiteSpace(organizationName))
            throw new ValidationException("Clinic name is required for physicians");

        if (password.Length < 8)
            throw new ValidationException("Password must be at least 8 characters");

        if (!password.Any(char.IsUpper))
            throw new ValidationException("Password must contain at least one uppercase letter");

        if (!password.Any(char.IsDigit))
            throw new ValidationException("Password must contain at least one number");
    }

    /// <summary>
    /// Finds a free slug by appending -1, -2, ... exactly as the Node loop does.
    ///
    /// This is check-then-insert, so two simultaneous signups of the same organization name can
    /// still collide. The unique index on <c>organizations.slug</c> is what actually guarantees
    /// uniqueness; the loser surfaces as a 500 rather than silently sharing a slug.
    /// </summary>
    private async Task<string> ReserveSlugAsync(string organizationName, CancellationToken ct)
    {
        var baseSlug = Organization.Slugify(organizationName);
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "org";

        var slug = baseSlug;
        var suffix = 0;

        while (await organizations.SlugExistsAsync(slug, ct))
        {
            suffix++;
            slug = $"{baseSlug}-{suffix}";
        }

        return slug;
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var normalized = phone.Replace(" ", string.Empty).Replace("-", string.Empty);
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
