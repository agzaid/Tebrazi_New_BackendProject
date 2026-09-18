namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The published read port onto <c>prescriptions</c>, supplied by Prescriptions.
///
/// Visits needs it twice: the <c>_count.prescriptions</c> figure on the visit list, and the full
/// prescription rows embedded in <c>GET /api/visits/{id}</c>.
/// </summary>
public interface IPrescriptionDirectory
{
    /// <summary>
    /// Per-visit counts for a whole page in one query. A visit with none is absent from the
    /// dictionary — callers should read it as 0.
    ///
    /// Counts EVERY row including soft-deleted ones, because the Node <c>_count</c> applies no
    /// filter.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> CountByVisitAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default);

    /// <summary>
    /// Every prescription on a visit, soft-deleted ones INCLUDED — the Node include is a bare
    /// <c>prescriptions: true</c> with no <c>where</c>, so rows with a non-null
    /// <c>deletedAt</c> appear in the array and the client sees that field set.
    /// </summary>
    Task<IReadOnlyList<PrescriptionRow>> ListByVisitAsync(
        string visitId, CancellationToken ct = default);

    // ── Patient-facing reads (added for GET /api/patients/dashboard, /medications/reconciled) ──

    /// <summary>
    /// Prescriptions across a set of visits, filtered to specific statuses, ordered
    /// <c>createdAt DESC</c> and capped at <paramref name="limit"/>.
    ///
    /// This is the .NET shape of Node's <c>where: { visit: { patientUserId }, status: { in: … } }</c>.
    /// Prescriptions has no patient column and no visit navigation, so the relation filter
    /// becomes a two-step: the caller resolves the patient's visit ids through
    /// <see cref="IVisitDirectory.ListVisitIdsForPatientAsync"/> and passes them here. Three
    /// callers, three status sets and three caps:
    /// <list type="bullet">
    /// <item><c>/medications/reconciled</c> (patients.js:561-576) — CONFIRMED, SENT, DISPENSED,
    /// NO <c>take</c>: pass <see cref="int.MaxValue"/>.</item>
    /// <item><c>/dashboard</c> (patients.js:739-748) — CONFIRMED, SENT, DISPENSED, take 10.</item>
    /// <item><c>/health-summary</c> (patients.js:1008-1013) — SIGNED, CONFIRMED, SENT,
    /// DISPENSED, take 20.</item>
    /// </list>
    ///
    /// <b>An EMPTY <paramref name="visitIds"/> matches NOTHING.</b> It is a real filter standing
    /// in for a relation, not an absent one — treating it as "no filter" would hand a patient
    /// with no visits every prescription in the table. Same for
    /// <paramref name="statuses"/>: names that do not parse to a <c>PrescriptionStatus</c> are
    /// ignored, and a collection with no usable name matches nothing.
    ///
    /// No soft-delete predicate: not one of the 17 routes in prescriptions.js carries a
    /// <c>deletedAt</c> clause, and neither do these three patients.js queries, so a
    /// soft-deleted prescription's drug lines DO appear in the reconciled list.
    ///
    /// <c>VisitDate</c> and <c>VisitChiefComplaint</c> come back NULL — see the remarks on
    /// <see cref="PatientPrescriptionRow"/>.
    /// </summary>
    Task<IReadOnlyList<PatientPrescriptionRow>> ListByVisitIdsAsync(
        IReadOnlyCollection<string> visitIds,
        IReadOnlyCollection<string> statuses,
        int limit,
        CancellationToken ct = default);
}

/// <summary>
/// All sixteen Prescription scalars, because <c>GET /api/visits/{id}</c> embeds the whole row.
/// </summary>
/// <param name="Medications">Opaque JSON, passed through verbatim.</param>
public sealed record PrescriptionRow(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    string Status,
    string Medications,
    string? Notes,
    DateTime? SignedAt,
    DateTime? SentToPatientAt,
    string? PdfUrl,
    DateTime? RefillRequestedAt,
    string? RefillStatus,
    string? RefillNotes,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? DeletedAt);

/// <summary>
/// A prescription projected for the patient dashboard / reconciled-medications endpoint.
///
/// <c>Medications</c> is the opaque JSON array of drug lines, passed through verbatim — the
/// caller parses it to flatten one prescription into several medication entries, and must not
/// re-serialize it into a different shape.
///
/// <b><c>VisitDate</c> and <c>VisitChiefComplaint</c> are ALWAYS NULL as returned by
/// <see cref="IPrescriptionDirectory.ListByVisitIdsAsync"/>.</b> They are on the record because
/// <c>/medications/reconciled</c> emits them as <c>visitDate</c> and <c>visitComplaint</c>
/// (patients.js:610-611), but <c>Prescription.VisitId</c> is a plain column with no navigation
/// and the Prescriptions context does not own <c>visits</c> — so no join here can reach them.
/// The caller resolves them from <see cref="IVisitDirectory.GetManyAsync"/> keyed on
/// <c>VisitId</c> and fills the row in place:
/// <c>row with { VisitDate = v.VisitDate, VisitChiefComplaint = v.ChiefComplaint }</c>.
/// Leaving them null when the visit does not resolve is correct: Node's <c>rx.visit?.visitDate</c>
/// yields <c>undefined</c>, which JSON.stringify drops the same way a null does not survive as a
/// value the client reads.
///
/// <c>PhysicianId</c> likewise resolves to the doctor's display name through
/// <see cref="IIdentityDirectory"/>, where Node's <c>physician.user.displayName</c> falls back
/// to "Doctor" on the dashboard and reconciled routes and to "Unknown" on health-summary.
/// </summary>
public sealed record PatientPrescriptionRow(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    string Status,
    string Medications,
    DateTime? VisitDate,
    string? VisitChiefComplaint,
    DateTime CreatedAt);
