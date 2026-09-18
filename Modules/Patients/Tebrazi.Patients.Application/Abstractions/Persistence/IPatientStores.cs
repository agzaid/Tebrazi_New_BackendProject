using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Persistence;

namespace Tebrazi.Patients.Application.Abstractions.Persistence;

public interface IPatientsDbContext : IDbContext;

public interface IPatientProfileStore
{
    Task<PatientProfile?> GetByUserIdAsync(string userId, CancellationToken ct = default);
    Task<PatientProfile?> GetByIdAsync(string id, CancellationToken ct = default);
    void Add(PatientProfile profile);
}

public interface IFamilySubprofileStore
{
    Task<FamilySubprofile?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<FamilySubprofile>> ListActiveAsync(string patientProfileId, CancellationToken ct = default);

    /// <summary>
    /// EVERY dependant on the account, deactivated ones included.
    ///
    /// <para><c>GET /api/patients/health-summary</c> needs it: its include is a bare
    /// <c>subprofiles: { include: … }</c> with NO <c>where</c> (patients.js:945-947), so a
    /// deactivated dependant still appears under "## Family Members" and still counts towards
    /// <c>stats.familyMembers</c>. <see cref="ListActiveAsync"/> would silently drop them.</para>
    ///
    /// <para><c>/dashboard</c> and <c>/medications/reconciled</c> need it for a different
    /// reason: a visit or a prescription may reference a dependant who has since been
    /// deactivated, and <c>v.subprofile?.name</c> resolves through the relation, which no
    /// <c>isActive</c> filter touches. Resolving <c>forMember</c> from the ACTIVE list only
    /// would print "Self" for that row.</para>
    /// </summary>
    Task<IReadOnlyList<FamilySubprofile>> ListAllAsync(string patientProfileId, CancellationToken ct = default);

    /// <summary>Confirms a subprofile belongs to a profile before its records are touched.</summary>
    Task<bool> BelongsToAsync(string subprofileId, string patientProfileId, CancellationToken ct = default);

    void Add(FamilySubprofile subprofile);
}

/// <summary>
/// The three health-record types share an interface because their endpoints are identical apart
/// from the payload. Every read here excludes soft-deleted rows.
/// </summary>
public interface IHealthRecordStore
{
    Task<IReadOnlyList<Allergy>> ListAllergiesAsync(string patientProfileId, string? subprofileId, CancellationToken ct = default);
    Task<IReadOnlyList<ChronicCondition>> ListConditionsAsync(string patientProfileId, string? subprofileId, CancellationToken ct = default);
    Task<IReadOnlyList<CurrentMedication>> ListMedicationsAsync(string patientProfileId, string? subprofileId, CancellationToken ct = default);

    Task<Allergy?> GetAllergyAsync(string id, CancellationToken ct = default);
    Task<ChronicCondition?> GetConditionAsync(string id, CancellationToken ct = default);
    Task<CurrentMedication?> GetMedicationAsync(string id, CancellationToken ct = default);

    // ── Whole-account reads, for the three patient-facing aggregate endpoints ────────────────
    //
    // The three methods above select ONE owner: the account holder, or one dependant. That is
    // what the per-record routes need and it is what the Prisma `include` on a single subprofile
    // does. The aggregate endpoints need the UNION — the account holder's own rows plus every
    // row belonging to a named set of dependants — because Node gets both sides in a single
    // `findMany` with an `OR` (patients.js:549-559, :730-738) or as two branches of one
    // `include` tree (patients.js:669-696, :939-949). Calling the single-owner reads once per
    // dependant would be the N+1 those endpoints cannot afford: the dashboard would issue
    // 3 + 3n queries for n family members.
    //
    // Each returns the union in ONE query, ordered createdAt DESC — the order
    // `/medications/reconciled` requires (patients.js:557) — and the caller partitions it by
    // `PatientProfileId` / `SubprofileId` to rebuild Node's two collections. A row belongs to
    // exactly one owner (the Node writers set `patientProfileId: subprofileId ? null : id`,
    // patients.js:324-325), so the partition is a clean split.

    /// <summary>
    /// The account holder's allergies plus those of the named dependants, in one query.
    /// </summary>
    /// <param name="patientProfileId">The account holder's profile id.</param>
    /// <param name="subprofileIds">
    /// The dependants to include. Empty means "the account holder's own rows only" — it is a
    /// filter that matches no dependant, never one that matches every dependant.
    /// </param>
    /// <param name="newestFirst">
    /// <c>true</c> — the default — orders by <c>createdAt</c> DESC, which is what the Node reads
    /// that PIN an order ask for (<c>/medications/reconciled</c>, patients.js:558, and the
    /// dashboard's medication strip, patients.js:730-741, both of which then cap the result).
    ///
    /// <para><c>false</c> orders by <c>createdAt</c> ASC, for the callers whose Node counterpart
    /// pins NO order: <c>/health-summary</c> reads these three collections off a bare nested
    /// include (patients.js:942-948), so Prisma emits no ORDER BY and Postgres hands back the
    /// rows of these append-only tables in insertion order — oldest first. Ascending is the
    /// faithful reproduction; leaving the default DESC in place printed every summary bullet
    /// backwards.</para>
    /// </param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The union, newest first unless <paramref name="newestFirst"/> says otherwise.</returns>
    Task<IReadOnlyList<Allergy>> ListAllergiesForAccountAsync(
        string patientProfileId, IReadOnlyCollection<string> subprofileIds,
        bool newestFirst = true, CancellationToken ct = default);

    /// <inheritdoc cref="ListAllergiesForAccountAsync"/>
    Task<IReadOnlyList<ChronicCondition>> ListConditionsForAccountAsync(
        string patientProfileId, IReadOnlyCollection<string> subprofileIds,
        bool newestFirst = true, CancellationToken ct = default);

    /// <inheritdoc cref="ListAllergiesForAccountAsync"/>
    Task<IReadOnlyList<CurrentMedication>> ListMedicationsForAccountAsync(
        string patientProfileId, IReadOnlyCollection<string> subprofileIds,
        bool newestFirst = true, CancellationToken ct = default);

    void Add(Allergy allergy);
    void Add(ChronicCondition condition);
    void Add(CurrentMedication medication);
}

public interface IExternalCareStore
{
    Task<IReadOnlyList<ExternalVisit>> ListVisitsAsync(string patientUserId, string? subprofileId, CancellationToken ct = default);

    /// <summary>
    /// The patient's external visits across ALL owners — their own and every dependant's —
    /// newest first, capped at <paramref name="limit"/>.
    ///
    /// <para><see cref="ListVisitsAsync"/> cannot express this: passing a null subprofile there
    /// means "the account holder's OWN visits" and adds <c>SubprofileId == null</c>.
    /// <c>GET /api/patients/health-summary</c> queries
    /// <c>where: { patientUserId }</c> with no subprofile clause at all
    /// (patients.js:1033-1037), so a consultation logged for a child appears in the summary and
    /// its medications count towards <c>stats.medications</c>.</para>
    /// </summary>
    /// <param name="patientUserId">The account holder's USER id — external visits are keyed by user, not by profile.</param>
    /// <param name="limit">Node's <c>take</c>; the health summary passes 10.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The visits, <c>visitDate</c> descending.</returns>
    Task<IReadOnlyList<ExternalVisit>> ListAllVisitsForPatientAsync(
        string patientUserId, int limit, CancellationToken ct = default);

    Task<ExternalVisit?> GetVisitAsync(string id, CancellationToken ct = default);
    void Add(ExternalVisit visit);
    void Remove(ExternalVisit visit);

    Task<IReadOnlyList<ExternalDoctor>> ListDoctorsAsync(string patientUserId, string? subprofileId, CancellationToken ct = default);
    Task<ExternalDoctor?> GetDoctorAsync(string id, CancellationToken ct = default);
    void Add(ExternalDoctor doctor);
    void Remove(ExternalDoctor doctor);
}
