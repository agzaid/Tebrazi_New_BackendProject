using Tebrazi.Identity.Application.Abstractions;

namespace Tebrazi.Identity.Infrastructure.Security;

/// <summary>
/// bcrypt at cost 10, matching Node's <c>bcrypt.hash(password, 10)</c>.
///
/// Do not "upgrade" this to ASP.NET Identity's PBKDF2 hasher or raise the cost in place: every
/// existing user's stored hash is bcrypt-10, and a different scheme cannot verify them. Raising
/// the work factor is possible, but only as a rehash-on-successful-login migration.
/// </summary>
public sealed class BcryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 10;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public bool Verify(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // A stored value that is not a bcrypt hash at all — a legacy row, or corruption.
            // Treated as a failed verification rather than a 500, so it cannot be used to probe
            // which accounts have malformed hashes.
            return false;
        }
    }
}
