using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Queries.InviteByToken;

/// <summary>
/// <c>GET /api/auth/invite/{token}</c> — reads an invitation and reports who it is for.
/// Public: the invitee has nothing to authenticate with yet, the token IS the credential.
///
/// The Node handler builds the response from a separate organization lookup
/// (auth.js:521-526) and answers <c>Unknown Organization</c> when the row is gone — the
/// membership lookup is a raw <c>findUnique</c> with no cascade, so a dangling
/// <c>organizationId</c> is reachable. Reproduced, not "fixed".
/// </summary>
public sealed record InviteByTokenQuery(string Token) : IRequest<InviteByTokenResponse>;

/// <summary>Body of <c>GET /api/auth/invite/{token}</c>.</summary>
public sealed record InviteByTokenResponse(string OrganizationName, string Role, string Email);

public sealed class InviteByTokenHandler(ITokenStore tokens, IOrganizationStore organizations)
    : IRequestHandler<InviteByTokenQuery, InviteByTokenResponse>
{
    public async Task<InviteByTokenResponse> Handle(InviteByTokenQuery request, CancellationToken ct = default)
    {
        var invitation = await tokens.GetInvitationAsync(request.Token, ct);

        // Node's two 410s are shaped { error: "Gone", message: ... } while the 404 is
        // { error: "Not Found", message: ... } — AppException's body builder emits exactly that
        // when Error differs from Message, which is why the label is passed as the first arg.
        if (invitation is null)
            throw new BusinessException("Not Found", "Invitation not found", 404);
        if (invitation.AcceptedAt is not null)
            throw new GoneException("This invitation has already been accepted");
        if (invitation.IsExpired(DateTime.UtcNow))
            throw new GoneException("This invitation has expired");

        var organization = await organizations.GetByIdAsync(invitation.OrganizationId, ct);

        return new InviteByTokenResponse(
            OrganizationName: organization?.Name ?? "Unknown Organization",
            Role: invitation.Role.ToString(),
            Email: invitation.Email);
    }
}
