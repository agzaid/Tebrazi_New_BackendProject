using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Queries.ListSessions;

/// <summary>
/// <c>GET /api/auth/sessions</c> (auth.js:1295-1323) — the active-sessions screen.
///
/// Live sessions only (<c>expiresAt &gt; now</c>), newest first, each marked <c>isCurrent</c>
/// when its row holds the token that authorized THIS request — matched by token value, exactly
/// as Node's second <c>findFirst</c> does.
/// </summary>
public sealed record ListSessionsQuery(string UserId, string CurrentToken)
    : IRequest<IReadOnlyList<SessionResponse>>;

public sealed record SessionResponse(
    string Id,
    string? DeviceInfo,
    string? IpAddress,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    bool IsCurrent);

public sealed class ListSessionsHandler(ISessionStore sessions)
    : IRequestHandler<ListSessionsQuery, IReadOnlyList<SessionResponse>>
{
    public async Task<IReadOnlyList<SessionResponse>> Handle(ListSessionsQuery request, CancellationToken ct = default)
    {
        var all = await sessions.ListForUserIncludingExpiredAsync(request.UserId, ct);
        var currentId = (await sessions.GetByTokenAsync(request.UserId, request.CurrentToken, ct))?.Id;
        var now = DateTime.UtcNow;

        return all
            .Where(s => s.ExpiresAt > now)
            .Select(s => new SessionResponse(
                Id: s.Id,
                DeviceInfo: s.DeviceInfo,
                IpAddress: s.IpAddress,
                CreatedAt: s.CreatedAt,
                ExpiresAt: s.ExpiresAt,
                IsCurrent: s.Id == currentId))
            .ToList();
    }
}
