using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.RevokeOtherSessions;

/// <summary>
/// <c>DELETE /api/auth/sessions</c> (auth.js:1345-1368) — revoke everything EXCEPT the current
/// device. Node's deleteMany filters <c>token != currentToken</c>, which keeps the current
/// session alive even if it has no row (the token match is against the stored token, so a
/// session issued without a row is unaffected either way).
/// </summary>
public sealed record RevokeOtherSessionsCommand(string UserId, string CurrentToken)
    : IRequest<RevokeOtherSessionsResponse>;

public sealed record RevokeOtherSessionsResponse(bool Success, string Message);

public sealed class RevokeOtherSessionsHandler(ISessionStore sessions, IIdentityDbContext dbContext)
    : IRequestHandler<RevokeOtherSessionsCommand, RevokeOtherSessionsResponse>
{
    public async Task<RevokeOtherSessionsResponse> Handle(RevokeOtherSessionsCommand request, CancellationToken ct = default)
    {
        var all = await sessions.ListForUserIncludingExpiredAsync(request.UserId, ct);
        var victims = all.Where(s => s.Token != request.CurrentToken).ToList();

        sessions.RemoveRange(victims);
        await dbContext.SaveChangesAsync(ct);

        return new RevokeOtherSessionsResponse(Success: true, Message: "All other sessions revoked");
    }
}
