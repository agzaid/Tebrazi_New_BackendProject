namespace Tebrazi.Identity.Infrastructure.Configuration;

/// <summary>
/// Bound from the <c>Jwt</c> configuration section. <see cref="Secret"/> must be the SAME value
/// as the Node backend's <c>JWT_SECRET</c> for tokens to remain interchangeable across the
/// migration.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HMAC signing key. Never commit a real value — supply it per environment.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Token lifetime, mirroring Node's <c>JWT_EXPIRES_IN</c> (default 7d). Expressed as a
    /// TimeSpan string, e.g. "7.00:00:00".
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Optional. The Node tokens carry no issuer or audience, so validation of both is OFF by
    /// default; setting either here turns the corresponding check on, which will reject
    /// Node-issued tokens.
    /// </summary>
    public string? Issuer { get; set; }

    public string? Audience { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Secret))
            throw new InvalidOperationException(
                "Jwt:Secret is not configured. It must match the Node backend's JWT_SECRET.");

        // HS256 keys shorter than 32 bytes are below the hash output size and are rejected by
        // the token handler at signing time rather than here, which is a confusing place to
        // discover it.
        if (System.Text.Encoding.UTF8.GetByteCount(Secret) < 32)
            throw new InvalidOperationException(
                "Jwt:Secret must be at least 32 bytes for HS256 signing.");
    }
}
