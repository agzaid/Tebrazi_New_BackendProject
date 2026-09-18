using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.RevokeSession;

/// <summary>
/// <c>DELETE /api/auth/sessions/{id}</c> (auth.js:1326-1344). 404 when the row is not THIS
/// user's — the where clause carries the userId, so a foreign id and a missing id are the same
/// answer.
/// </summary>
public sealed record RevokeSessionCommand(string UserId, string SessionId) : IRequest<RevokeSessionResponse>;

public sealed record RevokeSessionResponse(bool Success, string Message);

public sealed class RevokeSessionHandler(ISessionStore sessions, IIdentityDbContext dbContext)
    : IRequestHandler<RevokeSessionCommand, RevokeSessionResponse>
{
    public async Task<RevokeSessionResponse> Handle(RevokeSessionCommand request, CancellationToken ct = default)
    {
        var session = await sessions.GetForUpdateAsync(request.SessionId, ct);
        if (session is null || session.UserId != request.UserId)
            throw new BusinessException("Session not found", "Session not found", 404);

        sessions.Remove(session);
        await dbContext.SaveChangesAsync(ct);

        return new RevokeSessionResponse(Success: true, Message: "Session revoked");
    }
}
