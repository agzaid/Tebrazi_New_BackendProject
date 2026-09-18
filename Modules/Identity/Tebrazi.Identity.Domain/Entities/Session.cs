using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Identity.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Session</c> model (table <c>sessions</c>). Backs the "active sessions"
/// screen and lets a user revoke a device. The JWT itself remains stateless; a session row is a
/// record of issuance, not the thing that authenticates the request.
/// </summary>
public sealed class Session : ImmutableEntity<string>
{
    private Session() { }

    public string UserId { get; private set; } = null!;
    public string Token { get; private set; } = null!;
    public DateTime ExpiresAt { get; private set; }
    public string? DeviceInfo { get; private set; }
    public string? IpAddress { get; private set; }

    public User User { get; private set; } = null!;

    public static Session Issue(
        string userId,
        string token,
        DateTime expiresAt,
        string? deviceInfo = null,
        string? ipAddress = null)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            Token = token,
            ExpiresAt = expiresAt,
            DeviceInfo = deviceInfo,
            IpAddress = ipAddress
        };

    public bool IsExpired(DateTime utcNow) => ExpiresAt <= utcNow;
}
