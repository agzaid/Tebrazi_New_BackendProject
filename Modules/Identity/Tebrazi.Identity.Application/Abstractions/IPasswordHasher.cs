namespace Tebrazi.Identity.Application.Abstractions;

/// <summary>
/// Password hashing. The implementation MUST be bcrypt at cost 10 — every existing Tebrazi
/// user's <c>passwordHash</c> was produced by Node's <c>bcrypt.hash(password, 10)</c>, and a
/// different algorithm would lock out the entire user base on cutover.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>False rather than throwing when the stored hash is malformed or not bcrypt.</summary>
    bool Verify(string password, string passwordHash);
}
