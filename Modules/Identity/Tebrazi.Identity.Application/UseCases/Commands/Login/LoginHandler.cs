using System.Text.RegularExpressions;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.Login;

/// <summary>
/// Port of <c>POST /api/auth/login</c> in <c>server/src/routes/auth.js</c>, step for step,
/// including which failures are 400 and which are 401.
///
/// Note this deliberately does NOT create a Session row. Neither does the Node route — sessions
/// are only written by the phone claim-account flow, so the active-sessions screen shows only
/// devices claimed that way. Reproducing the gap keeps the two backends consistent; closing it
/// is a product change, not a port.
/// </summary>
public sealed partial class LoginHandler(
    IUserReadStore users,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator tokens,
    IAppLogger<LoginHandler> logger) : IRequestHandler<LoginCommand, LoginResponse>
{
    // Which roles may sign in through which portal. A null entry means "any role".
    private static readonly Dictionary<string, string[]?> RolePortalMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = ["admin", "manager", "user", "viewer"],
        ["student"] = ["student"],
        ["default"] = null
    };

    public async Task<LoginResponse> Handle(LoginCommand request, CancellationToken cancellationToken = default)
    {
        var identifier = (request.Email ?? request.Username ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(request.Password))
            throw new ValidationException("Phone/email and password are required");

        var user = await FindUserAsync(identifier, request.LoginAs, cancellationToken);

        // One message for "no such user" and for "wrong password", so the response cannot be
        // used to enumerate which accounts exist.
        if (user is null)
            throw new UnauthorizedException("Invalid credentials");

        EnsurePortalAllowsRole(user, request.LoginContext);

        if (!user.Active)
            throw new UnauthorizedException("Account is deactivated");

        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            logger.Warning("Failed login attempt", new { UserId = user.Id });
            throw new UnauthorizedException("Invalid credentials");
        }

        var memberships = await users.GetMembershipsAsync(user.Id, cancellationToken);
        var primaryOrganization = memberships.Count > 0 ? memberships[0].Organization : null;

        var (token, _) = tokens.Generate(user, primaryOrganization?.Id);

        logger.Information("Login succeeded", new { UserId = user.Id, user.UserType });

        return new LoginResponse(
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
                OrganizationId: primaryOrganization?.Id,
                OrganizationName: primaryOrganization?.Name));
    }

    /// <summary>
    /// Phone first when the identifier looks like a phone number, then email. An identifier that
    /// looks like a phone but matches no phone row still falls through to the email lookup,
    /// exactly as the Node route does.
    /// </summary>
    private async Task<User?> FindUserAsync(string identifier, string? loginAs, CancellationToken ct)
    {
        var isPhone = PhonePattern().IsMatch(identifier);

        User? user = null;

        if (isPhone)
        {
            var normalized = NormalizePhone(identifier);
            user = await users.GetByPhoneAsync(normalized, ct);
        }

        if (user is null && !isPhone)
        {
            var email = identifier.ToLowerInvariant();
            var userType = ParseUserType(loginAs);
            user = await users.GetByEmailAsync(email, userType, ct);
        }

        return user;
    }

    private static void EnsurePortalAllowsRole(User user, string? loginContext)
    {
        var context = string.IsNullOrWhiteSpace(loginContext) ? "default" : loginContext;

        // An unrecognised portal name allows every role, matching the JS object lookup
        // returning undefined and the guard treating that as "no restriction".
        if (!RolePortalMap.TryGetValue(context, out var allowedRoles) || allowedRoles is null)
            return;

        var role = user.Role.ToString().ToLowerInvariant();
        if (allowedRoles.Contains(role, StringComparer.Ordinal))
            return;

        throw new WrongPortalException(user.Role.ToString());
    }

    private static UserType? ParseUserType(string? value)
        => Enum.TryParse<UserType>(value, ignoreCase: false, out var parsed) ? parsed : null;

    /// <summary>Strips spaces and dashes; the stored phone is kept in this normalized form.</summary>
    private static string NormalizePhone(string value)
        => value.Replace(" ", string.Empty).Replace("-", string.Empty);

    [GeneratedRegex(@"^\+?\d[\d\s\-]{6,}$")]
    private static partial Regex PhonePattern();
}
