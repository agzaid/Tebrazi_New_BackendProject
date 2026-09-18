namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The published write port onto <c>patient_notes</c>, needed by exactly one endpoint:
/// <c>POST /api/appointments/{id}/intake-note</c> upserts the physician's intake note for a
/// patient and answers with THE STORED ROW plus a <c>message</c> key
/// (appointments.js:1155-1198). Appointments does not own the table — the PatientNotes module
/// will, and it does not exist yet.
///
/// <para><b>Unlike <see cref="INotificationPublisher"/>, <see cref="IWaitingQueueWriter"/> and
/// <see cref="IPaymentWriter"/>, this is NOT a best-effort side effect.</b> The note IS the
/// endpoint's product: its id, timestamps and content are the response body, so there is no
/// correct 200 without a stored row. A null result therefore means "not saved", and the caller
/// must answer with Node's own failure body for this route —
/// <c>500 {"error":"Failed to save intake note"}</c> — rather than inventing a row. See
/// docs/appointments-surface.md.</para>
/// </summary>
public interface IPatientNoteWriter
{
    /// <summary>
    /// Replaces the content of the physician's first note for this patient whose content starts
    /// with <see cref="PatientNoteUpsert.MatchContentPrefix"/>, or creates a new note when none
    /// matches.
    ///
    /// <para>The match prefix is a CALLER parameter rather than a constant in here because at
    /// the one live call site it never matches: the route searches for content starting
    /// <c>"[INTAKE]"</c> but writes content starting <c>"[INTAKE by &lt;userId&gt;]"</c>
    /// (appointments.js:1156 vs :1161), so the update branch is dead code and every call creates
    /// a row. Keeping the prefix at the call site keeps that visible instead of burying it.</para>
    ///
    /// <para>Note the SQL Server divergence this endpoint runs into.
    /// <c>patient_notes</c> carries <c>@@unique([physicianUserId, patientUserId, subprofileId])</c>
    /// and this path always writes <c>subprofileId = NULL</c>. PostgreSQL treats NULLs as
    /// distinct in a unique index, so Node's repeated creates all succeed and quietly pile up
    /// duplicate rows; SQL Server treats them as equal and rejects the second one. Whoever
    /// implements this MUST index around that (a filtered unique index, or none) or the endpoint
    /// becomes single-use per physician/patient pair.</para>
    /// </summary>
    /// <returns>
    /// The stored row, or null when it could not be stored. Null is not "nothing to do" — the
    /// calling endpoint has no response without a row.
    /// </returns>
    Task<PatientNoteRecord?> UpsertNoteAsync(
        PatientNoteUpsert request, CancellationToken ct = default);
}

/// <param name="PhysicianUserId">
/// A USER id (<c>patient_notes.physician_user_id</c>), not a physician-profile id. The intake
/// route resolves it as the caller's own id when the caller IS the physician, and the
/// appointment's physician user id when a staff member wrote the note
/// (appointments.js:1153).
/// </param>
/// <param name="PatientUserId">
/// The patient the note is about — the appointment's <c>patientUserId</c>, not the caller's.
/// </param>
/// <param name="Content">
/// Written verbatim. The intake route prefixes it <c>"[INTAKE by &lt;userId&gt;]\n"</c> and
/// trims the body first; that formatting stays at the call site.
/// </param>
/// <param name="MatchContentPrefix">
/// The <c>startsWith</c> the dedupe lookup applies. Null skips the lookup and always creates.
/// </param>
public sealed record PatientNoteUpsert(
    string PhysicianUserId,
    string PatientUserId,
    string Content,
    string? MatchContentPrefix = null);

/// <summary>
/// Every <c>PatientNote</c> scalar, because the intake-note response spreads the whole saved row
/// before appending its <c>message</c> key.
/// </summary>
/// <param name="Id">The note's own id.</param>
/// <param name="PhysicianUserId">The note's author-of-record, a USER id.</param>
/// <param name="PatientUserId">The patient the note is about.</param>
/// <param name="SubprofileId">
/// Always null on the intake path, which is what makes the SQL Server unique-index collision
/// described on <see cref="IPatientNoteWriter.UpsertNoteAsync"/> reachable.
/// </param>
/// <param name="ClinicPatientId">Always null on the intake path.</param>
/// <param name="Content">The stored text, including whatever prefix the caller applied.</param>
/// <param name="IsPinned">Column default false; the intake path never sets it.</param>
/// <param name="CreatedAt">Row creation timestamp.</param>
/// <param name="UpdatedAt">
/// Prisma declares <c>@updatedAt</c>, which is non-null there. Nullable here to match the rest
/// of this kernel's records, whose <c>MutableEntity</c> leaves it null until the first
/// modification — a freshly created note serializes it as null in the port and as a timestamp in
/// Node. Recorded in docs/PORT-STATUS.md rather than papered over.
/// </param>
public sealed record PatientNoteRecord(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Content,
    bool IsPinned,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
