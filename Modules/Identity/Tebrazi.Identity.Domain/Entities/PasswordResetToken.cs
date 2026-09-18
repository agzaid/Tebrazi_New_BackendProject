using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Identity.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>PasswordResetToken</c> model (table <c>password_reset_tokens</c>).
/// Single-use: <see cref="UsedAt"/> is stamped on redemption, and a token that is expired OR
/// already used is refused.
/// </summary>
public sealed class PasswordResetToken : ImmutableEntity<string>
{
    private PasswordResetToken() { }

    public string UserId { get; private set; } = null!;
    public string Token { get; private set; } = null!;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? UsedAt { get; private set; }

    public User User { get; private set; } = null!;

    public static PasswordResetToken Issue(string userId, string token, DateTime expiresAt)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            Token = token,
            ExpiresAt = expiresAt
        };

    public bool IsRedeemable(DateTime utcNow) => UsedAt is null && ExpiresAt > utcNow;

    public void Redeem(DateTime utcNow) => UsedAt = utcNow;
}
