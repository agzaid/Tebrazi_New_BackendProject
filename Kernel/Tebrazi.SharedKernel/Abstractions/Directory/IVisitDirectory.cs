namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The published read port onto <c>visits</c>, supplied by the Visits module.
///
/// Prescriptions needs it because a prescription reaches its patient only through its visit:
/// <c>prescriptions</c> carries a <c>visit_id</c> and no patient column at all. Listing a
/// patient's prescriptions therefore means resolving their visit ids first, which is what
/// <see cref="ListVisitIdsForPatientAsync"/> is for.
///
/// Read-only, matching the other directory ports — visits are written only by the Visits
/// module's own use cases.
/// </summary>
public interface IVisitDirectory
{
    Task<VisitSummary?> GetAsync(string visitId, CancellationToken ct = default);

    /// <summary>
    /// Resolved in ONE query. A page of prescriptions references many visits, and calling
    /// <see cref="GetAsync"/> per row is the N+1 this exists to prevent. Ids that do not resolve
    /// — a deleted visit, say — are simply absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<string, VisitSummary>> GetManyAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default);

    /// <summary>
    /// The patient's visit ids, for scoping a prescription list to them.
    ///
    /// An EMPTY result means "this patient has no visits", and a caller must treat it as a
    /// filter that matches nothing — not as "no filter". Getting that backwards would list every
    /// prescription in the table.
    /// </summary>
    Task<IReadOnlyList<string>> ListVisitIdsForPatientAsync(
        string patientUserId, string? subprofileId = null, CancellationToken ct = default);

    /// <summary>True when the visit exists and the physician owns it.</summary>
    Task<bool> BelongsToPhysicianAsync(
        string visitId, string physicianId, CancellationToken ct = default);

    /// <summary>
    /// How many COMPLETED visits a patient has had at a clinic. The check-in path uses it to
    /// decide between the consultation fee and the follow-up fee.
    /// </summary>
    Task<int> CountCompletedAsync(
        string clinicId, string patientUserId, CancellationToken ct = default);

    /// <summary>
    /// The clinic's visits within a day window, for the appointment queue and the schedule
    /// optimiser.
    ///
    /// Applies NO status filter, matching the Node query — so ARCHIVED and CANCELLED visits are
    /// included. The route's own inline comment claims otherwise and is wrong.
    /// </summary>
    Task<IReadOnlyList<VisitQueueRow>> ListForClinicDayAsync(
        string clinicId, DateTime dayStartUtc, DateTime dayEndUtc, CancellationToken ct = default);

    /// <summary>
    /// Start and completion timestamps for a physician's COMPLETED visits since a date, which
    /// the schedule optimiser turns into an average consultation length. Only visits with a
    /// <c>completed_at</c> are returned, matching the Node query.
    /// </summary>
    Task<IReadOnlyList<VisitDuration>> ListCompletedDurationsAsync(
        string physicianId, DateTime since, CancellationToken ct = default);

    // ── Patient-facing reads (added for GET /api/patients/dashboard and /health-summary) ──

    /// <summary>
    /// Recent visits for a patient, for the two patient-facing aggregate endpoints. Only visits
    /// whose status is in <paramref name="statuses"/> are returned, ordered by
    /// <c>visitDate DESC</c> and capped at <paramref name="limit"/>.
    ///
    /// Two callers with two different filters, which is why every knob is a parameter:
    /// <list type="bullet">
    /// <item><c>GET /api/patients/dashboard</c> (patients.js:794-812) — statuses
    /// COMPLETED + ARCHIVED, <c>patientDismissedAt: null</c>, take 5.</item>
    /// <item><c>GET /api/patients/health-summary</c> (patients.js:953-962) — statuses
    /// COMPLETED + IN_PROGRESS, NO dismissed filter, take 5.</item>
    /// </list>
    ///
    /// <paramref name="excludePatientDismissed"/> gates the <c>PatientDismissedAt == null</c>
    /// predicate, so the health-summary caller passes false and still sees visits the patient
    /// hid from their dashboard inbox — which is what Node does.
    ///
    /// NO soft-delete predicate is applied: neither Node query carries a <c>deletedAt</c>
    /// clause, and <c>VisitsDbContext</c> has no query filter to inherit one from.
    ///
    /// Status names that do not parse to a <c>VisitStatus</c> are ignored; an empty or
    /// entirely-unparseable collection therefore matches NOTHING rather than everything.
    ///
    /// <b><c>ClinicName</c> comes back null.</b> The Visits context owns <c>visits</c> and
    /// <c>investigations</c> only — there is no clinics table to join — so the caller fills it
    /// from <see cref="IClinicDirectory.GetClinicsAsync"/> keyed on the row's
    /// <c>ClinicId</c>. Likewise the physician display name and specialty, from
    /// <see cref="IIdentityDirectory.GetPhysiciansAsync"/> keyed on <c>PhysicianId</c>.
    /// </summary>
    Task<IReadOnlyList<PatientVisitRow>> ListRecentForPatientAsync(
        string patientUserId,
        IReadOnlyCollection<string> statuses,
        bool excludePatientDismissed,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Total visit count for a patient — all statuses, no limit, and SOFT-DELETED VISITS
    /// INCLUDED. <c>prisma.visit.count({ where: { patientUserId } })</c> (patients.js:838-840)
    /// is the whole filter: no status clause, no <c>deletedAt</c> clause, and no
    /// <c>patientDismissedAt</c> clause, so the dashboard's <c>stats.totalVisits</c> counts
    /// cancelled, archived and dismissed visits alike.
    /// </summary>
    Task<int> CountForPatientAsync(string patientUserId, CancellationToken ct = default);

    // ── Added for the Connections module ─────────────────────────────────────

    /// <summary>
    /// Visits per clinic-patient chart — the <c>include: { _count: { select: { visits: true } } }</c>
    /// of <c>GET /api/connections/clinic-patients</c> (connections.js:841), which the client renders
    /// as the visit badge on each walk-in chart.
    ///
    /// <para>Counts EVERY visit on the chart: no status filter, no <c>deletedAt</c> filter. Prisma's
    /// <c>_count</c> on a relation counts rows, and neither this route nor the relation carries a
    /// predicate.</para>
    ///
    /// <para>A chart with no visits is ABSENT from the dictionary rather than present as 0 — read
    /// it with <c>GetValueOrDefault</c>, whose default is already 0, which is also what the Node
    /// <c>_count</c> reports. An empty <paramref name="clinicPatientIds"/> returns an empty
    /// dictionary.</para>
    /// </summary>
    /// <param name="clinicPatientIds">The charts to count for. Empty returns empty.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, int>> CountByClinicPatientAsync(
        IReadOnlyCollection<string> clinicPatientIds, CancellationToken ct = default);

    /// <summary>
    /// Everything <c>GET /api/connections/patient-summaries</c> needs out of <c>visits</c>, for
    /// every connected patient at once (connections.js:1060-1099).
    ///
    /// <para>Node issues FOUR queries per patient inside a <c>Promise.allSettled</c> — last visit,
    /// visit count, distinct visited subprofiles, overdue follow-up. This is the batched
    /// equivalent: a physician with 200 patients makes a handful of round trips instead of 800.
    /// The <c>allSettled</c> semantics are worth knowing: in Node, a failure for ONE patient drops
    /// that patient's key from the response and still answers 200 (:1122-1127). A batched query
    /// cannot fail per patient, so the port either returns every patient or throws — and a throw
    /// becomes the route's own <c>500 {"error":"Failed to load summaries"}</c>. That is a
    /// deliberate, recorded difference; see docs/connections-surface.md §10.</para>
    ///
    /// <para>The filters, each exactly as Node writes them:</para>
    /// <list type="bullet">
    /// <item><b>Last visit</b> — <c>{ physicianId, patientUserId }</c>, ordered
    /// <c>visitDate DESC</c>, first row. No status filter, so an IN_PROGRESS or CANCELLED visit
    /// can be the "last" one. Supplies <c>lastVisitDate</c> and <c>lastComplaint</c>.</item>
    /// <item><b>Visit count</b> — the same two-key filter, no status clause.</item>
    /// <item><b>Visited dependants</b> — <c>{ patientUserId, physicianId, subprofileId: { not: null } }</c>,
    /// DISTINCT on <c>subprofileId</c>. No status filter. The caller unions this with
    /// <c>IDoctorPatientConnectionStore.ListAcceptedSubprofileIdsAsync</c> and takes the SIZE of
    /// the union — that is <c>familyCount</c> (connections.js:1089), and it already excludes SELF
    /// because a SELF visit carries a null <c>subprofileId</c>.</item>
    /// <item><b>Overdue follow-up</b> — <c>{ physicianId, patientUserId, status: 'COMPLETED',
    /// followUpDate: { lte: today } }</c>, first row. <b>Only here is status filtered</b>, and
    /// <c>today</c> is midnight LOCAL time on the server, from
    /// <c>new Date(); today.setHours(0,0,0,0)</c> (connections.js:1055-1056) — pass the same
    /// instant, not <c>DateTime.UtcNow.Date</c>, unless the two servers share a timezone.
    /// Supplies <c>hasOverdueFollowUp</c> (a boolean: the row existing) and
    /// <c>followUpDate</c>.</item>
    /// </list>
    ///
    /// <para>NO <c>deletedAt</c> predicate anywhere, matching all four Node queries and the
    /// context's deliberate lack of a query filter.</para>
    ///
    /// <para>A patient with no visits at all is ABSENT from the dictionary. The route still emits
    /// a summary object for them — zeros and nulls — so read with <c>GetValueOrDefault</c> and
    /// build the response from the connection list, not from this dictionary's keys.</para>
    /// </summary>
    /// <param name="physicianId">
    /// The physician's PROFILE id (<c>visits.physician_id</c>), not their user id. The route
    /// resolves it through <c>IIdentityDirectory.GetPhysicianByUserIdAsync</c> first and answers
    /// <c>200 {}</c> when there is none (connections.js:1045-1046).
    /// </param>
    /// <param name="patientUserIds">The connected patients' USER ids. Empty returns empty.</param>
    /// <param name="overdueFollowUpCutoff">
    /// The <c>followUpDate &lt;= x</c> bound — Node's local midnight. Inclusive.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, PhysicianPatientVisitSummary>> GetPatientVisitSummariesAsync(
        string physicianId,
        IReadOnlyCollection<string> patientUserIds,
        DateTime overdueFollowUpCutoff,
        CancellationToken ct = default);
}

/// <summary>
/// One patient's visit-side aggregates for the physician dashboard. Every member maps to a key of
/// the <c>patient-summaries</c> response object; the three keys that are NOT here —
/// <c>conditionsCount</c>, <c>medsCount</c> and <c>unreadMessages</c> — come from
/// <see cref="IPatientDirectory"/> and from the unported Messages module respectively.
/// </summary>
/// <param name="LastVisitDate">
/// The most recent visit's date, or null. Any status counts.
/// </param>
/// <param name="LastComplaint">
/// That visit's <c>chiefComplaint</c>, or null. Node emits <c>lastVisit?.chiefComplaint || null</c>,
/// so an empty string becomes null — apply that at the call site, not here.
/// </param>
/// <param name="VisitCount">Every visit this physician has recorded for this patient.</param>
/// <param name="VisitedSubprofileIds">
/// DISTINCT dependants this physician has seen. Union it with the ACCEPTED dependant edges and
/// take the size for <c>familyCount</c>. Empty list, never null.
/// </param>
/// <param name="OverdueFollowUpDate">
/// The follow-up date of the first COMPLETED visit whose follow-up is due, or null. Drives both
/// <c>followUpDate</c> and <c>hasOverdueFollowUp</c> — the latter is simply
/// <c>OverdueFollowUpDate is not null</c>, because Node's <c>!!overdueFollowUp</c> tests the ROW
/// and the query guarantees a non-null <c>followUpDate</c> on any row it returns.
/// </param>
public sealed record PhysicianPatientVisitSummary(
    DateTime? LastVisitDate,
    string? LastComplaint,
    int VisitCount,
    IReadOnlyList<string> VisitedSubprofileIds,
    DateTime? OverdueFollowUpDate);

/// <summary>
/// The narrow WRITE port onto <c>visits</c>, published by Visits because Visits owns the table.
/// It exists for one endpoint: <c>DELETE /api/connections/clinic-patients/{id}</c>
/// (connections.js:877), whose transaction removes a walk-in chart's visits before the chart
/// itself.
///
/// <para>Kept separate from <see cref="IVisitDirectory"/> so that port stays read-only, and
/// deliberately narrow — it can delete by chart id and nothing else. It commits through the VISITS
/// unit of work, so this is its own commit; see the atomicity note in
/// docs/connections-surface.md §10.</para>
/// </summary>
public interface IVisitClinicPatientWriter
{
    /// <summary>
    /// HARD-deletes every visit attached to a walk-in chart —
    /// <c>tx.visit.deleteMany({ where: { clinicPatientId: id } })</c>.
    ///
    /// <para>A hard delete, not the ARCHIVE that <c>DELETE /api/visits/{id}</c> performs: the
    /// chart is going away and its visits have nothing left to reference. No <c>deletedAt</c>
    /// predicate, so already-soft-deleted visits go too.</para>
    ///
    /// <para><b>Their prescriptions and investigations are NOT removed</b>, matching Node — the
    /// transaction deletes visits, appointments, notes and tags, and nothing else. Rows in
    /// <c>prescriptions</c> and <c>investigations</c> are left pointing at visit ids that no longer
    /// exist, in both backends.</para>
    /// </summary>
    /// <param name="clinicPatientId">The chart whose visits are to be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were deleted. Zero is normal and not an error.</returns>
    Task<int> DeleteForClinicPatientAsync(string clinicPatientId, CancellationToken ct = default);
}

/// <param name="CompletedAt">Never null — the query requires it.</param>
public sealed record VisitDuration(DateTime VisitDate, DateTime CompletedAt);

/// <summary>
/// The slice of a visit the Appointments module needs. Field names here are the MODEL's; the
/// queue endpoint renames them on the wire (<c>followUpDate</c> becomes <c>visitFollowUp</c>,
/// and so on), and that renaming belongs in the response DTO, not in this port.
/// </summary>
public sealed record VisitQueueRow(
    string Id,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    DateTime VisitDate,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    DateTime? CompletedAt);

/// <param name="Plan">
/// The SOAP plan text. Exposed because <c>POST /api/prescriptions/extract-from-plan</c> reads it
/// and asks the AI gateway to turn it into drug lines — the one endpoint that needs a visit's
/// clinical prose rather than just its identity.
/// </param>
public sealed record VisitSummary(
    string Id,
    string OrganizationId,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    DateTime VisitDate,
    string Status,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis,
    string? Plan);

/// <summary>
/// A visit projected for the patient dashboard / health-summary: the scalars both routes read,
/// plus the three foreign ids the caller needs to finish the row.
///
/// It carries IDS, not names. <c>PhysicianId</c> resolves to the doctor's display name and
/// specialty through <see cref="IIdentityDirectory.GetPhysiciansAsync"/>, <c>ClinicId</c> to
/// <c>ClinicName</c> through <see cref="IClinicDirectory.GetClinicsAsync"/>, and
/// <c>SubprofileId</c> to the family member's name inside the Patients module itself. None of
/// those tables is in the Visits context, so the Visits query cannot reach them.
///
/// <c>ClinicName</c> is therefore ALWAYS NULL as returned by
/// <see cref="IVisitDirectory.ListRecentForPatientAsync"/>. It is on the record so the caller
/// can fill it in place — <c>row with { ClinicName = clinics[row.ClinicId].Name }</c> — rather
/// than carrying a parallel dictionary through the rest of the handler.
///
/// It does NOT carry the visit's prescriptions. <c>GET /health-summary</c> embeds them
/// (patients.js:1082-1088) and gets them from <see cref="IPrescriptionDirectory"/>, which is a
/// different module's port; joining the two here would put Prescriptions inside a Visits query.
/// </summary>
public sealed record PatientVisitRow(
    string Id,
    DateTime VisitDate,
    string Status,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    string PhysicianId,
    string? SubprofileId,
    string? Subjective,
    string? Assessment,
    string? Plan,
    string ClinicId,
    string? ClinicName,
    DateTime? PatientDismissedAt);
