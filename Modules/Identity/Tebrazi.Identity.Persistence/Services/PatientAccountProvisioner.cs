using System.Security.Cryptography;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Persistence.Services;

/// <summary>
/// Identity's implementation of <see cref="IPatientAccountProvisioner"/> — the one write another
/// module may make to <c>users</c>, and only for
/// <c>POST /api/connections/create-patient</c> (connections.js:617-735).
///
/// <para>It sits beside <see cref="OrganizationProvisioner"/> for the same reason: cross-module
/// writes are few, narrow, and each commits on its own.</para>
/// </summary>
public sealed class PatientAccountProvisioner(
    IdentityDbContext context,
    IPasswordHasher passwordHasher) : IPatientAccountProvisioner
{
    /// <summary>
    /// <c>crypto.randomBytes(16).toString('hex')</c> (connections.js:659) — 16 bytes, so 32 hex
    /// characters.
    /// </summary>
    private const int TemporaryPasswordBytes = 16;

    public async Task<UserSummary> CreatePatientAccountAsync(
        string displayName,
        string email,
        string? phone,
        string? currentOrganizationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        // Generated, hashed and DISCARDED inside this method. The patient never learns it; they
        // claim the account through the password-reset flow. Keeping the plaintext out of the
        // port's signature is what makes that impossible to get wrong at a call site.
        var temporaryPassword = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(TemporaryPasswordBytes));

        var user = User.Create(
            displayName,
            passwordHasher.Hash(temporaryPassword),
            // Both hard-coded at the Node call site (connections.js:674-675).
            UserType.PATIENT,
            email,
            // `phone || null`: the empty string must become SQL NULL, not "".
            string.IsNullOrEmpty(phone) ? null : phone,
            UserRole.USER,
            currentOrganizationId);

        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        return new UserSummary(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Phone,
            user.ProfilePictureUrl,
            user.Role.ToString(),
            user.UserType.ToString(),
            user.Active);
    }
}
