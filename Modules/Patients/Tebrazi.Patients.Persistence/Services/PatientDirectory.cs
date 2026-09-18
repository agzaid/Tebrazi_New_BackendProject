using Microsoft.EntityFrameworkCore;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Patients.Persistence.Services;

/// <summary>
/// Patients' implementation of the published <see cref="IPatientDirectory"/> port.
///
/// The health-record reads here go through the context's soft-delete query filters, so
/// deleted allergies, conditions and medications never leave the module.
/// </summary>
public sealed class PatientDirectory(PatientsDbContext context) : IPatientDirectory
{
    public Task<SubprofileSummary?> GetSubprofileAsync(
        string subprofileId, CancellationToken ct = default)
        => context.FamilySubprofiles
            .AsNoTracking()
            .Where(s => s.Id == subprofileId)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, SubprofileSummary>> GetSubprofilesAsync(
        IReadOnlyCollection<string> subprofileIds, CancellationToken ct = default)
    {
        if (subprofileIds.Count == 0) return new Dictionary<string, SubprofileSummary>(0);

        var ids = subprofileIds.Distinct().ToArray();

        var rows = await context.FamilySubprofiles
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(Projection)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id);
    }

    public async Task<SubprofileClinicalContext?> GetSubprofileClinicalContextAsync(
        string subprofileId, CancellationToken ct = default)
    {
        var subprofile = await context.FamilySubprofiles
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == subprofileId, ct);

        if (subprofile is null) return null;

        // Three separate round trips rather than one Include: the context promotes the
        // cartesian-explosion warning to an exception, and these are three independent
        // collections off the same parent.
        var allergies = await context.Allergies
            .AsNoTracking()
            .Where(a => a.SubprofileId == subprofileId)
            .Select(a => new AllergyItem(a.Allergen, a.Severity, a.Reaction))
            .ToListAsync(ct);

        var conditions = await context.ChronicConditions
            .AsNoTracking()
            .Where(c => c.SubprofileId == subprofileId && c.IsActive)
            .Select(c => c.Condition)
            .ToListAsync(ct);

        var medications = await context.CurrentMedications
            .AsNoTracking()
            .Where(m => m.SubprofileId == subprofileId && m.IsActive)
            .Select(m => new MedicationItem(m.DrugName, m.Dosage, m.Frequency))
            .ToListAsync(ct);

        return new SubprofileClinicalContext(
            subprofile.Id,
            subprofile.Name,
            subprofile.Relation.ToString(),
            allergies,
            conditions,
            medications);
    }

    public async Task<IReadOnlyList<string>> ListSubprofileIdsByUserAsync(
        string patientUserId, CancellationToken ct = default)
        => await context.FamilySubprofiles
            .AsNoTracking()
            .Where(s => context.PatientProfiles
                .Where(p => p.UserId == patientUserId)
                .Select(p => p.Id)
                .Contains(s.PatientProfileId))
            // Deterministic, unlike the Node findFirst it replaces.
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> ListActiveMedicationNamesAsync(
        IReadOnlyCollection<string> subprofileIds, CancellationToken ct = default)
    {
        if (subprofileIds.Count == 0) return [];

        var ids = subprofileIds.Distinct().ToArray();

        return await context.CurrentMedications
            .AsNoTracking()
            // IgnoreQueryFilters is deliberate and is part of the contract this port exists to
            // satisfy. Node's reads (prescriptions.js:742-745, :792-795) filter on `isActive` ONLY
            // and never on `deletedAt`, so a soft-deleted-but-active row is on the wire in
            // `drugsChecked`. PatientsDbContext's blanket `m.DeletedAt == null` filter would hide
            // it and diverge. See IPatientDirectory.ListActiveMedicationNamesAsync.
            .IgnoreQueryFilters()
            .Where(m => m.SubprofileId != null && ids.Contains(m.SubprofileId) && m.IsActive)
            .OrderBy(m => m.SubprofileId)
            .ThenBy(m => m.CreatedAt)
            .Select(m => m.DrugName)
            .ToListAsync(ct);
    }

    // ── Added for the Connections module ─────────────────────────────────────

    public Task<string?> GetPatientProfileIdAsync(
        string patientUserId, CancellationToken ct = default)
        => context.PatientProfiles
            .AsNoTracking()
            .Where(p => p.UserId == patientUserId)
            .Select(p => (string?)p.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, SubprofileClinicalCounts>>
        CountSubprofileClinicalItemsAsync(
            IReadOnlyCollection<string> patientUserIds, CancellationToken ct = default)
    {
        if (patientUserIds.Count == 0)
            return new Dictionary<string, SubprofileClinicalCounts>(0);

        var ids = patientUserIds.Distinct().ToArray();

        // The whole chain is subprofile -> patientProfile -> userId, matching Node's
        // `{ subprofile: { patientProfile: { userId: pid } } }` (connections.js:1070, :1073).
        // A record attached DIRECTLY to the PatientProfile has a null SubprofileId and is
        // deliberately NOT counted. The context's soft-delete filters apply, which is what
        // reproduces Node's hard deletes — see IPatientDirectory.
        var conditions = await context.ChronicConditions
            .AsNoTracking()
            .Where(c => c.SubprofileId != null)
            .Join(
                context.FamilySubprofiles.AsNoTracking(),
                c => c.SubprofileId, s => s.Id, (c, s) => s.PatientProfileId)
            .Join(
                context.PatientProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)),
                profileId => profileId, p => p.Id, (_, p) => p.UserId)
            .GroupBy(userId => userId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var medications = await context.CurrentMedications
            .AsNoTracking()
            .Where(m => m.SubprofileId != null)
            .Join(
                context.FamilySubprofiles.AsNoTracking(),
                m => m.SubprofileId, s => s.Id, (m, s) => s.PatientProfileId)
            .Join(
                context.PatientProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)),
                profileId => profileId, p => p.Id, (_, p) => p.UserId)
            .GroupBy(userId => userId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var conditionCounts = conditions.ToDictionary(r => r.UserId, r => r.Count);
        var medicationCounts = medications.ToDictionary(r => r.UserId, r => r.Count);

        return conditionCounts.Keys
            .Union(medicationCounts.Keys)
            .ToDictionary(
                userId => userId,
                userId => new SubprofileClinicalCounts(
                    conditionCounts.GetValueOrDefault(userId),
                    medicationCounts.GetValueOrDefault(userId)));
    }

    private static readonly System.Linq.Expressions.Expression<
        Func<Domain.Entities.FamilySubprofile, SubprofileSummary>> Projection =
        s => new SubprofileSummary(
            s.Id,
            s.PatientProfileId,
            s.Name,
            s.Relation.ToString(),
            s.DateOfBirth,
            s.Gender == null ? null : s.Gender.ToString(),
            s.BloodType,
            s.IsActive);
}
