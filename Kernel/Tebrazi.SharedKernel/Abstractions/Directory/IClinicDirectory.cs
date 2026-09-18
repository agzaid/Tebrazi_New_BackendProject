namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The published read port onto <c>clinics</c> and clinic staffing, supplied by Clinics.
///
/// Visits, Appointments and Prescriptions all embed clinic details in their responses and all
/// gate writes on clinic standing, and none of them may touch the Clinics context to do it.
///
/// <para><b>This is NOT <c>IClinicAccessEvaluator</c>, and the two are not interchangeable.</b>
/// That port mirrors <c>server/src/middleware/clinicPermission.js</c>, whose precedence is
/// "owning physician → ANY physician profile → staff row". These three route files never use
/// that middleware; they use the strictly narrower tests below. Substituting the evaluator would
/// grant any physician with a profile access to clinics they have nothing to do with.</para>
/// </summary>
public interface IClinicDirectory
{
    Task<ClinicSummary?> GetClinicAsync(string clinicId, CancellationToken ct = default);

    /// <summary>Resolved in one query, for a page of visits or appointments.</summary>
    Task<IReadOnlyDictionary<string, ClinicSummary>> GetClinicsAsync(
        IReadOnlyCollection<string> clinicIds, CancellationToken ct = default);

    /// <summary>
    /// True when the clinic's <c>physician_id</c> IS this physician profile — ownership, not
    /// membership. This is the test the slot-management and queue endpoints apply.
    /// </summary>
    Task<bool> IsOwningPhysicianAsync(
        string clinicId, string physicianProfileId, CancellationToken ct = default);

    /// <summary>True when the user holds an ACTIVE <c>clinic_staff</c> row at the clinic.</summary>
    Task<bool> IsActiveStaffAsync(string userId, string clinicId, CancellationToken ct = default);

    /// <summary>
    /// Every active staff user id at a clinic, for the visit-completed notification fan-out.
    /// Empty list, never null.
    /// </summary>
    Task<IReadOnlyList<string>> ListActiveStaffUserIdsAsync(
        string clinicId, CancellationToken ct = default);

    // ── Added for the Connections module ─────────────────────────────────────

    /// <summary>
    /// One clinic id belonging to a physician, or null when they have none — the
    /// <c>include: { clinics: { take: 1, select: { id: true } } }</c> then
    /// <c>physician?.clinics?.[0]?.id || null</c> that <c>POST /api/connections/clinic-patients</c>
    /// falls back to when a physician creates a walk-in chart without naming a clinic
    /// (connections.js:760-764).
    ///
    /// <para>The argument is a physician PROFILE id (<c>clinics.physician_id</c>), not a user id —
    /// resolve it with <see cref="IIdentityDirectory.GetPhysicianByUserIdAsync"/> first. Null is
    /// NOT an error at the call site: the chart is simply created with
    /// <c>clinic_id = NULL</c>, which the schema allows.</para>
    ///
    /// <para><b>No <c>isActive</c> filter</b>, matching the Node relation load — a physician whose
    /// only clinic is deactivated still gets its id. Ordered by <c>created_at</c> then <c>id</c>
    /// so "the first clinic" is deterministic; Node's <c>take: 1</c> has no <c>orderBy</c> and
    /// takes whatever Postgres returns first, so a physician with several clinics can get a
    /// different one from each backend. Recorded in docs/connections-surface.md §10.</para>
    /// </summary>
    /// <param name="physicianProfileId">The physician's PROFILE id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> GetFirstClinicIdForPhysicianAsync(
        string physicianProfileId, CancellationToken ct = default);
}

/// <summary>
/// The narrow write port onto a clinic's working hours, needed by
/// <c>POST /api/appointments/slots/sync-from-hours</c> — the only clinic write any of these
/// three modules performs. Kept separate from the read port so the read side stays read-only.
/// </summary>
public interface IClinicWorkingHoursWriter
{
    /// <summary>
    /// Replaces the stored working-hours JSON and commits. Returns false when no such clinic
    /// exists. The value is passed already serialized so the double encoding the Node route
    /// performs stays visible at the call site rather than hiding in here.
    /// </summary>
    Task<bool> SetWorkingHoursAsync(
        string clinicId, string? workingHoursJson, CancellationToken ct = default);
}

/// <param name="PhysicianId">
/// The OWNING physician's profile id — the clinic's <c>physician_id</c> column, not a user id.
/// </param>
/// <param name="WorkingHours">Opaque JSON, stored verbatim.</param>
/// <param name="ConsultationFee">
/// Nullable, and defaulted to 300 on the entity. The check-in path treats 0 as "unset" and falls
/// back to a literal, because the Node code uses <c>||</c> rather than <c>??</c> — reproduce
/// that with an explicit <c>is null or 0</c> test, not with <c>??</c>.
/// </param>
public sealed record ClinicSummary(
    string Id,
    string OrganizationId,
    string PhysicianId,
    string Name,
    string? Address,
    string? City,
    string? Country,
    string? Phone,
    string? Email,
    string? Specialty,
    string? Logo,
    string? WorkingHours,
    bool IsActive,
    bool AllowPatientBooking,
    double? ConsultationFee,
    double? FollowUpFee);
