using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Exceptions;

namespace Tebrazi.Patients.Application.Services;

/// <summary>
/// Resolves the caller's patient profile, creating one on first use.
///
/// Registration does not create a profile — the Node routes upsert it lazily — so every entry
/// point into this module has to be able to make one. Centralised here so the twenty-odd
/// handlers do not each repeat the get-or-create.
/// </summary>
public sealed class PatientProfileResolver(
    IPatientsDbContext dbContext,
    IPatientProfileStore profiles,
    IFamilySubprofileStore subprofiles)
{
    /// <summary>Gets the caller's profile, creating and committing one if absent.</summary>
    public async Task<PatientProfile> GetOrCreateAsync(string userId, CancellationToken ct = default)
    {
        var profile = await profiles.GetByUserIdAsync(userId, ct);
        if (profile is not null) return profile;

        profile = PatientProfile.Create(userId);
        profiles.Add(profile);
        await dbContext.SaveChangesAsync(ct);

        return profile;
    }

    /// <summary>The profile, or null. Use where an absent profile means "no data" not "make one".</summary>
    public Task<PatientProfile?> FindAsync(string userId, CancellationToken ct = default)
        => profiles.GetByUserIdAsync(userId, ct);

    /// <summary>
    /// Validates that a subprofile named in a request belongs to the caller.
    ///
    /// This is the module's main authorization check. Without it, passing another account's
    /// subprofile id would attach a record to — or read one from — someone else's dependant.
    /// </summary>
    public async Task EnsureSubprofileOwnedAsync(
        string? subprofileId, string patientProfileId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subprofileId)) return;

        if (!await subprofiles.BelongsToAsync(subprofileId, patientProfileId, ct))
            throw new NotFoundException("Family member not found");
    }
}
