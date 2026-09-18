using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.AcceptInvite;

public sealed class AcceptInviteHandler(
    IIdentityDbContext dbContext,
    ITokenStore tokens,
    IOrganizationStore organizations,
    IUserReadStore users,
    IUserWriteStore userWriter,
    IPasswordHasher hasher,
    IJwtTokenGenerator jwt)
    : IRequestHandler<AcceptInviteCommand, AcceptInviteResponse>
{
    public async Task<AcceptInviteResponse> Handle(AcceptInviteCommand request, CancellationToken ct = default)
    {
        var token = request.Token;
        var displayName = request.DisplayName;
        var password = request.Password;

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new ValidationException("All fields are required: token, displayName, password");
        }

        if (password.Length < 8)
            throw new ValidationException("Password must be at least 8 characters");

        // ONE combined message here, unlike register's two separate checks — the Node handlers
        // are deliberate about the difference (auth.js:566-573 vs register's :300-312).
        if (!password.Any(char.IsUpper) || !password.Any(char.IsDigit))
            throw new ValidationException("Password must contain at least one uppercase letter and one number");

        var invitation = await tokens.GetInvitationAsync(token, ct);

        // Node's 404 and the two 410s all emit { error, message } with the label as the error
        // key. GoneException already produces that; the 404 needs an explicit label.
        if (invitation is null)
            throw new BusinessException("Not Found", "Invitation not found", 404);
        if (invitation.AcceptedAt is not null)
            throw new GoneException("This invitation has already been accepted");
        if (invitation.IsExpired(DateTime.UtcNow))
            throw new GoneException("This invitation has expired");

        // auth.js:598-602 checks email with a bare lookup NOT narrowed by userType — any user
        // row with that address, patient or physician, blocks the invite. The read store's
        // EmailExistsAsync is per-type, so check both existent types; an address that exists as
        // patient OR physician fails this route exactly as the bare Prisma findUnique did.
        if (await users.EmailExistsAsync(invitation.Email, UserType.PATIENT, ct) ||
            await users.EmailExistsAsync(invitation.Email, UserType.PHYSICIAN, ct))
        {
            throw new ConflictException(
                "An account with this email already exists. Please log in and accept the invitation from your dashboard.");
        }

        var passwordHash = hasher.Hash(password);
        var user = User.Create(
            displayName: displayName!,
            passwordHash: passwordHash,
            // Node creates with no userType — the Prisma default is PATIENT.
            userType: UserType.PATIENT,
            email: invitation.Email,
            role: UserRole.USER,
            currentOrganizationId: invitation.OrganizationId);

        var membership = OrganizationMember.Create(invitation.OrganizationId, user.Id, invitation.Role);

        // Node runs user + membership + invitation-acceptance in one $transaction. A concurrent
        // double-accept is guarded only by the earlier check-then-act read; reproduce that,
        // do not invent an atomicity guard Node does not have.
        await dbContext.ExecuteInTransactionAsync(async transactionCt =>
        {
            userWriter.Add(user);
            userWriter.Add(membership);
            invitation.Accept(DateTime.UtcNow);
            await dbContext.SaveChangesAsync(transactionCt);
        }, ct);

        var organization = await organizations.GetByIdAsync(invitation.OrganizationId, ct);
        var (jwtToken, _) = jwt.Generate(user, invitation.OrganizationId);

        return new AcceptInviteResponse(
            Token: jwtToken,
            User: new AuthUserResponse(
                Id: user.Id,
                Email: user.Email,
                Name: user.DisplayName,
                DisplayName: user.DisplayName,
                Role: user.Role.ToString(),
                UserType: user.UserType.ToString(),
                Phone: user.Phone,
                ProfilePictureUrl: user.ProfilePictureUrl,
                OrganizationId: invitation.OrganizationId,
                OrganizationName: organization?.Name));
    }
}
