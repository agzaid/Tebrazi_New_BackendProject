using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Prescriptions.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Prescription</c> model (table <c>prescriptions</c>).
///
/// A prescription always belongs to a visit. <c>VisitId</c> is therefore required, but it is a
/// plain column with no navigation: the visit lives in the Visits module, and this module reads
/// it through a published port rather than by joining another module's context.
///
/// The enum declares DRAFT → SIGNED → CONFIRMED → SENT → DISPENSED, but the ladder the API
/// actually walks starts one rung up: <c>POST /api/prescriptions</c> creates the row as
/// <b>SIGNED</b> with <c>signedAt</c> already set, because a physician's authenticated account
/// implicitly signs at creation. DRAFT is unreachable through any ported endpoint.
///
/// From there <c>/sign</c> writes CONFIRMED, <c>/send</c> writes SENT, and <c>/dispense</c>
/// writes DISPENSED. See the note on <see cref="Sign"/> — the method named after signing does
/// not write the status named after it.
/// </summary>
public sealed class Prescription : MutableEntity<string>
{
    private Prescription() { }

    public string VisitId { get; private set; } = null!;
    public string PhysicianId { get; private set; } = null!;
    public string? SubprofileId { get; private set; }

    public PrescriptionStatus Status { get; private set; } = PrescriptionStatus.DRAFT;

    /// <summary>
    /// Opaque JSON array of drug lines: <c>[{ drugName, dosage, frequency, duration,
    /// instructions }]</c>. Required — a prescription with no medications is not one. Stored
    /// verbatim because the shape is the client's, not the server's.
    /// </summary>
    public string Medications { get; private set; } = null!;

    public string? Notes { get; private set; }

    public DateTime? SignedAt { get; private set; }
    public DateTime? SentToPatientAt { get; private set; }
    public string? PdfUrl { get; private set; }

    // ── Refills ──────────────────────────────────────────────────────────────
    public DateTime? RefillRequestedAt { get; private set; }

    /// <summary>"PENDING", "APPROVED" or "DENIED". Free text in the Node schema, not an enum.</summary>
    public string? RefillStatus { get; private set; }

    public string? RefillNotes { get; private set; }

    /// <summary>
    /// Soft delete. Null means active.
    ///
    /// <b>Nothing filters on it.</b> There is deliberately no query filter on
    /// <c>PrescriptionsDbContext</c> and not one of the 17 routes in <c>prescriptions.js</c>
    /// carries a <c>deletedAt</c> clause, so a soft-deleted prescription is listed, counted,
    /// signed, sent, dispensed, refilled and printed exactly like a live one — and this column's
    /// value is visible in the response.
    /// </summary>
    public DateTime? DeletedAt { get; private set; }

    public static Prescription Create(
        string visitId,
        string physicianId,
        string medications,
        string? subprofileId = null,
        string? notes = null,
        PrescriptionStatus status = PrescriptionStatus.DRAFT)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicianId);
        ArgumentException.ThrowIfNullOrWhiteSpace(medications);

        return new Prescription
        {
            Id = Guid.NewGuid().ToString(),
            VisitId = visitId,
            PhysicianId = physicianId,
            SubprofileId = subprofileId,
            Medications = medications,
            Notes = notes,
            Status = status
        };
    }

    /// <summary>
    /// What <c>POST /api/prescriptions</c> actually creates (prescriptions.js:118-131): a row
    /// that is already <b>SIGNED</b> with <c>signedAt</c> stamped, because the physician's
    /// authenticated account implicitly signs at creation.
    ///
    /// <para>Use this, not <see cref="Create"/>, for the create endpoint. <see cref="Create"/>
    /// defaults to DRAFT and never stamps <c>signedAt</c>, and a row created that way is
    /// unreachable through any ported endpoint — it would also be invisible to the patient list,
    /// whose filter is CONFIRMED/SENT/DISPENSED.</para>
    ///
    /// <para><c>subprofileId</c> is inherited from the VISIT, never from the request body
    /// (prescriptions.js:122); the caller resolves it through the Visits port and passes it
    /// here.</para>
    /// </summary>
    /// <param name="visitId">The visit the prescription is written against. Required.</param>
    /// <param name="physicianId">
    /// The authoring physician's PROFILE id — <c>physicianProfile.id</c>, not their user id.
    /// </param>
    /// <param name="medications">
    /// The request's medications array, already serialized to JSON text. Stored with zero
    /// normalization: no trimming, no per-element <c>drugName</c> requirement, no dedup, so
    /// <c>[null]</c>, <c>[{}]</c> and <c>[1,2,3]</c> all pass the route's non-empty-array test
    /// and reach here intact.
    /// </param>
    /// <param name="subprofileId">The visit's dependant, or null for a chart-only visit.</param>
    /// <param name="notes">
    /// Already through the route's <c>notes || null</c>, so the empty string arrives as null and
    /// is stored as SQL NULL rather than "".
    /// </param>
    public static Prescription CreateAutoSigned(
        string visitId,
        string physicianId,
        string medications,
        string? subprofileId = null,
        string? notes = null)
    {
        var prescription = Create(
            visitId, physicianId, medications, subprofileId, notes, PrescriptionStatus.SIGNED);

        prescription.SignedAt = DateTime.UtcNow;

        return prescription;
    }

    /// <summary>
    /// Replaces the drug lines with an already-serialized JSON payload.
    ///
    /// <para><b>There is no status gate on this, in either backend.</b> The earlier doc comment
    /// claiming "only legal while the prescription is still a draft" was wrong:
    /// <c>PUT /{id}/stop-medication</c> and <c>PUT /{id}/resume-medication</c> rewrite the whole
    /// column for a patient at ANY status (prescriptions.js:676, :719), and <c>PUT /{id}</c>
    /// rewrites it for the physician at any status except SENT — which is the file's ONLY status
    /// precondition. Enforce the SENT test in the handler, not here.</para>
    ///
    /// <para>The value is written verbatim: <c>PUT /{id}</c> performs no array validation at all,
    /// so <c>{"medications": 5}</c> really is persisted as the JSON scalar <c>5</c> and later
    /// crashes stop/resume. Pass the raw JSON text.</para>
    ///
    /// <para>The guard rejects null, empty and whitespace. It cannot reject the four-character
    /// text <c>"null"</c>, so a handler receiving an EXPLICIT <c>medications: null</c> must raise
    /// its own 500 instead of calling this — Prisma refuses a bare null on a required Json column
    /// and Node answers <c>500 {"error":"Failed to update prescription"}</c>. Persisting the
    /// string "null" would be a silent divergence.</para>
    /// </summary>
    public void ReplaceMedications(string medications)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(medications);
        Medications = medications;
    }

    /// <summary>
    /// Sets or clears the free-text notes. Null clears the column, which
    /// <c>PUT /api/prescriptions/{id}</c> relies on: it guards with <c>!== undefined</c>, so an
    /// explicit <c>notes: null</c> wipes the field while an ABSENT key must leave it alone
    /// (prescriptions.js:265) — the handler decides which by distinguishing
    /// <c>JsonValueKind.Undefined</c> from <c>JsonValueKind.Null</c>, and only calls this for the
    /// latter.
    /// </summary>
    public void SetNotes(string? notes) => Notes = notes;

    /// <summary>
    /// What <c>PUT /api/prescriptions/{id}/sign</c> does — and note it sets
    /// <b>CONFIRMED</b>, not SIGNED (prescriptions.js:294). Rows are already created SIGNED, so
    /// "signing" is the rung ABOVE that, and the enum member named SIGNED is only ever the
    /// creation state.
    ///
    /// <c>SignedAt</c> is overwritten unconditionally, not stamped once: re-signing moves the
    /// timestamp, which is what the Node update does.
    /// </summary>
    public void Sign()
    {
        Status = PrescriptionStatus.CONFIRMED;
        SignedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Sends the prescription to the patient. It does NOT touch <c>SignedAt</c> — the Node
    /// update writes only status and <c>sentToPatientAt</c> (prescriptions.js:324), because
    /// creation already auto-signed. There is no DRAFT guard for the same reason.
    /// </summary>
    public void SendToPatient()
    {
        Status = PrescriptionStatus.SENT;
        SentToPatientAt = DateTime.UtcNow;
    }

    public void Dispense() => Status = PrescriptionStatus.DISPENSED;

    public void SetPdfUrl(string? pdfUrl) => PdfUrl = pdfUrl;

    /// <summary>
    /// The patient asks for a refill (<c>POST /api/prescriptions/{id}/refill-request</c>,
    /// prescriptions.js:547-553). Re-requesting after an APPROVED or DENIED decision is legal and
    /// moves the timestamp forward.
    ///
    /// <para><b><c>RefillNotes</c> is overwritten UNCONDITIONALLY</b>, including to null. Node
    /// writes <c>refillNotes: notes || null</c>, so a request with no notes silently WIPES the
    /// physician's note from the previous decision. This is the asymmetric half of the pair —
    /// <see cref="RespondToRefill"/> preserves instead — and the difference is the contract, not
    /// a bug to smooth over.</para>
    /// </summary>
    /// <param name="notes">
    /// The patient's note, already through the caller's <c>notes || null</c> (pass
    /// <c>RxJs.Truthy(notes)</c>, so the empty string arrives as null and clears the column).
    /// </param>
    public void RequestRefill(string? notes = null)
    {
        RefillRequestedAt = DateTime.UtcNow;
        RefillStatus = "PENDING";
        RefillNotes = notes;
    }

    /// <summary>
    /// The physician answers a refill request (<c>PUT /api/prescriptions/{id}/refill-respond</c>,
    /// prescriptions.js:607-613). <c>RefillRequestedAt</c> is NOT cleared — it keeps the original
    /// request timestamp.
    ///
    /// <para><b><c>RefillNotes</c> is PRESERVED when <paramref name="notes"/> is null</b>, because
    /// Node writes <c>refillNotes: notes || prescription.refillNotes</c>. The opposite of
    /// <see cref="RequestRefill"/>. There is no way to clear the notes through this endpoint.</para>
    /// </summary>
    /// <param name="status">
    /// The raw uppercase action string, written verbatim into the free-text <c>refill_status</c>
    /// column: "APPROVED" or "DENIED". The route rejects anything else BEFORE loading the row, so
    /// no other value can reach here.
    /// </param>
    /// <param name="notes">
    /// The physician's note, already through the caller's truthiness test (pass
    /// <c>RxJs.Truthy(notes)</c>). Null preserves whatever is stored.
    /// </param>
    public void RespondToRefill(string status, string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        RefillStatus = status;
        if (notes is not null) RefillNotes = notes;
    }

    public void SoftDelete() => DeletedAt ??= DateTime.UtcNow;
}

public enum PrescriptionStatus { DRAFT, SIGNED, CONFIRMED, SENT, DISPENSED }
