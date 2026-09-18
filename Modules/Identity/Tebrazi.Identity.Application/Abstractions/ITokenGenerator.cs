using Tebrazi.Identity.Domain.Entities;

namespace Tebrazi.Identity.Application.Abstractions;

/// <summary>
/// Issues the access token. The output must be an HS256 JWT whose payload carries exactly
/// <c>{ id, email, role, userType, displayName, organizationId }</c> — the Node backend signs
/// the same shape with the same secret, so tokens stay valid across both during the migration.
/// </summary>
public interface IJwtTokenGenerator
{
    /// <param name="organizationId">The user's primary organization, or null when they have none.</param>
    /// <returns>The signed token and the instant it expires.</returns>
    (string Token, DateTime ExpiresAtUtc) Generate(User user, string? organizationId);
}

/// <summary>Cryptographically random opaque tokens for password reset, invitations and OTPs.</summary>
public interface ISecureTokenGenerator
{
    /// <summary>A hex token of <paramref name="byteLength"/> random bytes (Node used 32).</summary>
    string CreateToken(int byteLength = 32);

    /// <summary>A numeric OTP of the given length, drawn from a cryptographic RNG.</summary>
    string CreateNumericCode(int digits = 6);
}
