using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Domain.Entities;

namespace Tebrazi.Patients.Persistence.Stores;

public sealed class PatientProfileStore(PatientsDbContext context) : IPatientProfileStore
{
    // Tracked: the profile is nearly always read in order to be updated.
    public Task<PatientProfile?> GetByUserIdAsync(string userId, CancellationToken ct = default)
        => context.PatientProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public Task<PatientProfile?> GetByIdAsync(string id, CancellationToken ct = default)
        => context.PatientProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);

    public void Add(PatientProfile profile) => context.PatientProfiles.Add(profile);
}

public sealed class FamilySubprofileStore(PatientsDbContext context) : IFamilySubprofileStore
{
    public Task<FamilySubprofile?> GetByIdAsync(string id, CancellationToken ct = default)
        => context.FamilySubprofiles.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<FamilySubprofile>> ListActiveAsync(
        string patientProfileId, CancellationToken ct = default)
        => await context.FamilySubprofiles
            .AsNoTracking()
            .Where(s => s.PatientProfileId == patientProfileId && s.IsActive)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Every dependant, deactivated ones included — the aggregate endpoints resolve a visit's or
    /// a prescription's <c>forMember</c> through this, and the health summary lists all of them.
    /// Ordered like <see cref="ListActiveAsync"/> so the two agree on family order.
    /// </summary>
    public async Task<IReadOnlyList<FamilySubprofile>> ListAllAsync(
        string patientProfileId, CancellationToken ct = default)
        => await context.FamilySubprofiles
            .AsNoTracking()
            .Where(s => s.PatientProfileId == patientProfileId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Ownership check. Note it does NOT filter on IsActive: records attached to a deactivated
    /// dependant still belong to this account and must remain deletable.
    /// </summary>
    public Task<bool> BelongsToAsync(string subprofileId, string patientProfileId, CancellationToken ct = default)
        => context.FamilySubprofiles
            .AsNoTracking()
            .AnyAsync(s => s.Id == subprofileId && s.PatientProfileId == patientProfileId, ct);

    public void Add(FamilySubprofile subprofile) => context.FamilySubprofiles.Add(subprofile);
}

/// <summary>
/// Reads exclude soft-deleted rows automatically: the query filters live on the context, so a
/// query here cannot forget to apply them.
/// </summary>
public sealed class HealthRecordStore(PatientsDbContext context) : IHealthRecordStore
{
    public async Task<IReadOnlyList<Allergy>> ListAllergiesAsync(
        string patientProfileId, string? subprofileId, CancellationToken ct = default)
        => await context.Allergies
            .AsNoTracking()
            .Where(Owner<Allergy>(patientProfileId, subprofileId))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ChronicCondition>> ListConditionsAsync(
        string patientProfileId, string? subprofileId, CancellationToken ct = default)
        => await context.ChronicConditions
            .AsNoTracking()
            .Where(Owner<ChronicCondition>(patientProfileId, subprofileId))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CurrentMedication>> ListMedicationsAsync(
        string patientProfileId, string? subprofileId, CancellationToken ct = default)
        => await context.CurrentMedications
            .AsNoTracking()
            .Where(Owner<CurrentMedication>(patientProfileId, subprofileId))
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Allergy>> ListAllergiesForAccountAsync(
        string patientProfileId, IReadOnlyCollection<string> subprofileIds,
        bool newestFirst = true, CancellationToken ct = default)
    {
        var ids = Distinct(subprofileIds);

        var query = context.Allergies
            .AsNoTracking()
            .Where(a => a.PatientProfileId == patientProfileId
                     || (a.SubprofileId != null && ids.Contains(a.SubprofileId)));

        return await ByCreatedAt(query, a => a.CreatedAt, newestFirst).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChronicCondition>> ListConditionsForAccountAsync(
        string patientProfileId, IReadOnlyCollection<string> subprofileIds,
        bool newestFirst = true, CancellationToken ct = default)
    {
        var ids = Distinct(subprofileIds);

        var query = context.ChronicConditions
            .AsNoTracking()
            .Where(c => c.PatientProfileId == patientProfileId
                     || (c.SubprofileId != null && ids.Contains(c.SubprofileId)));

        return await ByCreatedAt(query, c => c.CreatedAt, newestFirst).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CurrentMedication>> ListMedicationsForAccountAsync(
        string patientProfileId, IReadOnlyCollection<string> subprofileIds,
        bool newestFirst = true, CancellationToken ct = default)
    {
        var ids = Distinct(subprofileIds);

        var query = context.CurrentMedications
            .AsNoTracking()
            .Where(m => m.PatientProfileId == patientProfileId
                     || (m.SubprofileId != null && ids.Contains(m.SubprofileId)));

        return await ByCreatedAt(query, m => m.CreatedAt, newestFirst).ToListAsync(ct);
    }

    /// <summary>
    /// The account-scoped reads' single ordering rule. DESC is the default because the Node
    /// queries that pin an order pin <c>createdAt: 'desc'</c>; ASC exists for
    /// <c>/health-summary</c>, whose Node counterpart pins none and therefore reads these
    /// append-only tables back in insertion order.
    /// </summary>
    private static IQueryable<T> ByCreatedAt<T>(
        IQueryable<T> query, Expression<Func<T, DateTime>> createdAt, bool newestFirst)
        => newestFirst ? query.OrderByDescending(createdAt) : query.OrderBy(createdAt);

    public Task<Allergy?> GetAllergyAsync(string id, CancellationToken ct = default)
        => context.Allergies.FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<ChronicCondition?> GetConditionAsync(string id, CancellationToken ct = default)
        => context.ChronicConditions.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<CurrentMedication?> GetMedicationAsync(string id, CancellationToken ct = default)
        => context.CurrentMedications.FirstOrDefaultAsync(m => m.Id == id, ct);

    public void Add(Allergy allergy) => context.Allergies.Add(allergy);
    public void Add(ChronicCondition condition) => context.ChronicConditions.Add(condition);
    public void Add(CurrentMedication medication) => context.CurrentMedications.Add(medication);

    /// <summary>
    /// Selects rows for one owner: the dependant when a subprofile is named, otherwise the
    /// account holder's own rows.
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<T, bool>> Owner<T>(
        string patientProfileId, string? subprofileId) where T : HealthRecord
        => subprofileId is null
            ? r => r.PatientProfileId == patientProfileId
            : r => r.SubprofileId == subprofileId;

    /// <summary>
    /// The dependant ids as an array EF can inline. Distinct because the callers assemble the
    /// list from a subprofile collection and a duplicate would widen the <c>IN</c> for nothing.
    /// </summary>
    private static string[] Distinct(IReadOnlyCollection<string> subprofileIds)
        => subprofileIds.Count == 0 ? [] : [.. subprofileIds.Distinct()];
}

public sealed class ExternalCareStore(PatientsDbContext context) : IExternalCareStore
{
    public async Task<IReadOnlyList<ExternalVisit>> ListVisitsAsync(
        string patientUserId, string? subprofileId, CancellationToken ct = default)
    {
        var query = context.ExternalVisits.AsNoTracking().Where(v => v.PatientUserId == patientUserId);

        // No subprofile named means the account holder's OWN visits, not everyone's.
        query = subprofileId is null
            ? query.Where(v => v.SubprofileId == null)
            : query.Where(v => v.SubprofileId == subprofileId);

        return await query.OrderByDescending(v => v.VisitDate).ToListAsync(ct);
    }

    /// <summary>
    /// No subprofile clause at all — deliberately unlike <see cref="ListVisitsAsync"/>, whose
    /// null-subprofile branch narrows to the account holder's own rows. The health summary wants
    /// the whole household.
    /// </summary>
    public async Task<IReadOnlyList<ExternalVisit>> ListAllVisitsForPatientAsync(
        string patientUserId, int limit, CancellationToken ct = default)
        => await context.ExternalVisits
            .AsNoTracking()
            .Where(v => v.PatientUserId == patientUserId)
            .OrderByDescending(v => v.VisitDate)
            .Take(limit)
            .ToListAsync(ct);

    public Task<ExternalVisit?> GetVisitAsync(string id, CancellationToken ct = default)
        => context.ExternalVisits.FirstOrDefaultAsync(v => v.Id == id, ct);

    public void Add(ExternalVisit visit) => context.ExternalVisits.Add(visit);
    public void Remove(ExternalVisit visit) => context.ExternalVisits.Remove(visit);

    public async Task<IReadOnlyList<ExternalDoctor>> ListDoctorsAsync(
        string patientUserId, string? subprofileId, CancellationToken ct = default)
    {
        var query = context.ExternalDoctors.AsNoTracking().Where(d => d.PatientUserId == patientUserId);

        query = subprofileId is null
            ? query.Where(d => d.SubprofileId == null)
            : query.Where(d => d.SubprofileId == subprofileId);

        return await query.OrderBy(d => d.Name).ToListAsync(ct);
    }

    public Task<ExternalDoctor?> GetDoctorAsync(string id, CancellationToken ct = default)
        => context.ExternalDoctors.FirstOrDefaultAsync(d => d.Id == id, ct);

    public void Add(ExternalDoctor doctor) => context.ExternalDoctors.Add(doctor);
    public void Remove(ExternalDoctor doctor) => context.ExternalDoctors.Remove(doctor);
}
