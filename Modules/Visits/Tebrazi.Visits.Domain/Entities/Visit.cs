using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Visits.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Visit</c> model (table <c>visits</c>) — one clinical encounter and its
/// SOAP note. This is the root of the clinical record: prescriptions and investigations hang
/// off it, and the Prescriptions module reaches back by <c>visit_id</c>.
///
/// Three ids describe who the visit is for, and they are not interchangeable:
/// <list type="bullet">
/// <item><c>PatientUserId</c> — the Tebrazi account, always set.</item>
/// <item><c>SubprofileId</c> — a dependant on that account, when the visit is for one.</item>
/// <item><c>ClinicPatientId</c> — the physician-owned chart, for a walk-in with no account.</item>
/// </list>
/// </summary>
public sealed class Visit : MutableEntity<string>
{
    private Visit() { }

    public string OrganizationId { get; private set; } = null!;
    public string ClinicId { get; private set; } = null!;
    public string PhysicianId { get; private set; } = null!;
    public string PatientUserId { get; private set; } = null!;
    public string? SubprofileId { get; private set; }
    public string? ClinicPatientId { get; private set; }

    public VisitStatus Status { get; private set; } = VisitStatus.IN_PROGRESS;

    // ── Raw input ────────────────────────────────────────────────────────────
    public string? AudioUrl { get; private set; }
    public string? RawTranscript { get; private set; }
    public string? RawNotes { get; private set; }

    // ── Structured SOAP note ─────────────────────────────────────────────────
    public string? Subjective { get; private set; }
    public string? Objective { get; private set; }
    public string? Assessment { get; private set; }
    public string? Plan { get; private set; }

    // ── Metadata ─────────────────────────────────────────────────────────────
    public string? ChiefComplaint { get; private set; }

    /// <summary>
    /// Free-text diagnoses. A primitive collection, so it lands in one JSON column and still
    /// translates <c>Contains</c> to SQL — the analytics endpoints will need that.
    /// </summary>
    public List<string> Diagnosis { get; private set; } = [];

    /// <summary>Opaque JSON array: <c>[{ code, description }]</c>. Nothing server-side reads into it.</summary>
    public string? DiagnosisCodes { get; private set; }

    public DateTime? FollowUpDate { get; private set; }
    public string? FollowUpNotes { get; private set; }

    /// <summary>
    /// Opaque JSON array naming the SOAP sections the patient may see, e.g.
    /// <c>["subjective","assessment","plan"]</c>. NULL means every section is shared — absent
    /// and empty are therefore NOT the same, and the null must survive the round trip.
    /// </summary>
    public string? SharedSections { get; private set; }

    public string? PatientFeedback { get; private set; }
    public DateTime? PatientFeedbackAt { get; private set; }

    /// <summary>
    /// Set when the patient hides the visit from their own inbox. It is a patient-side soft
    /// delete only: the physician's copy of the record is untouched.
    /// </summary>
    public DateTime? PatientDismissedAt { get; private set; }

    public DateTime VisitDate { get; private set; }

    /// <summary>Opaque JSON of specialty-specific exam fields (ophthalmology IOP, cardiology EF%, ...).</summary>
    public string? SpecialtyData { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    /// <summary>Soft delete. Null means active; the query filter on the context enforces it.</summary>
    public DateTime? DeletedAt { get; private set; }

    /// <summary>
    /// Orders placed during this visit. A real navigation, because investigations live in this
    /// same module and context — the only collection on the aggregate, which keeps the
    /// cartesian-explosion guard in <c>ApplyGlobalSettings</c> satisfied when it is included.
    /// </summary>
    public ICollection<Investigation> Investigations { get; private set; } = [];

    public static Visit Create(
        string organizationId,
        string clinicId,
        string physicianId,
        string patientUserId,
        string? subprofileId = null,
        string? clinicPatientId = null,
        string? chiefComplaint = null,
        string? rawNotes = null,
        string? rawTranscript = null,
        string? audioUrl = null,
        string? subjective = null,
        string? objective = null,
        string? assessment = null,
        string? plan = null,
        IEnumerable<string>? diagnosis = null,
        string? diagnosisCodes = null,
        DateTime? followUpDate = null,
        string? followUpNotes = null,
        string? sharedSections = null,
        string? specialtyData = null,
        DateTime? visitDate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clinicId);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicianId);
        ArgumentException.ThrowIfNullOrWhiteSpace(patientUserId);

        return new Visit
        {
            Id = Guid.NewGuid().ToString(),
            OrganizationId = organizationId,
            ClinicId = clinicId,
            PhysicianId = physicianId,
            PatientUserId = patientUserId,
            SubprofileId = subprofileId,
            ClinicPatientId = clinicPatientId,
            Status = VisitStatus.IN_PROGRESS,
            ChiefComplaint = chiefComplaint,
            RawNotes = rawNotes,
            RawTranscript = rawTranscript,
            AudioUrl = audioUrl,
            Subjective = subjective,
            Objective = objective,
            Assessment = assessment,
            Plan = plan,
            Diagnosis = diagnosis is null ? [] : [.. diagnosis],
            DiagnosisCodes = diagnosisCodes,
            FollowUpDate = followUpDate,
            FollowUpNotes = followUpNotes,
            SharedSections = sharedSections,
            SpecialtyData = specialtyData,
            VisitDate = visitDate ?? DateTime.UtcNow
        };
    }

    /// <summary>
    /// Partial update: null leaves a field alone. That is the Node PUT semantics — it spreads
    /// only the keys present in the body — so passing null must NOT blank a stored value.
    /// </summary>
    public void Update(
        string? chiefComplaint = null,
        string? rawNotes = null,
        string? rawTranscript = null,
        string? audioUrl = null,
        string? subjective = null,
        string? objective = null,
        string? assessment = null,
        string? plan = null,
        IEnumerable<string>? diagnosis = null,
        string? diagnosisCodes = null,
        DateTime? followUpDate = null,
        string? followUpNotes = null,
        string? sharedSections = null,
        string? specialtyData = null,
        DateTime? visitDate = null,
        VisitStatus? status = null)
    {
        if (chiefComplaint is not null) ChiefComplaint = chiefComplaint;
        if (rawNotes is not null) RawNotes = rawNotes;
        if (rawTranscript is not null) RawTranscript = rawTranscript;
        if (audioUrl is not null) AudioUrl = audioUrl;
        if (subjective is not null) Subjective = subjective;
        if (objective is not null) Objective = objective;
        if (assessment is not null) Assessment = assessment;
        if (plan is not null) Plan = plan;
        if (diagnosis is not null) Diagnosis = [.. diagnosis];
        if (diagnosisCodes is not null) DiagnosisCodes = diagnosisCodes;
        if (followUpDate.HasValue) FollowUpDate = followUpDate;
        if (followUpNotes is not null) FollowUpNotes = followUpNotes;
        if (sharedSections is not null) SharedSections = sharedSections;
        if (specialtyData is not null) SpecialtyData = specialtyData;
        if (visitDate.HasValue) VisitDate = visitDate.Value;
        if (status.HasValue) Status = status.Value;
    }

    /// <summary>Stores an AI-generated SOAP note over the draft.</summary>
    public void ApplySoap(string? subjective, string? objective, string? assessment, string? plan)
    {
        Subjective = subjective;
        Objective = objective;
        Assessment = assessment;
        Plan = plan;
    }

    public void SetDiagnosisCodes(string? diagnosisCodes) => DiagnosisCodes = diagnosisCodes;

    /// <summary>
    /// Replaces the shared-section whitelist outright, NULL included.
    ///
    /// <see cref="Update"/> cannot do this: there, null means "not supplied" and leaves the
    /// stored value alone. <c>PUT /api/visits/{id}/complete</c> writes
    /// <c>sharedSections: sectionsToStore</c> unconditionally (visits.js:511-515), so when the
    /// filter collapses to null it CLEARS a previously stored whitelist — and null means share
    /// everything. Re-completing a visit with an empty selection therefore publishes the whole
    /// note, and going through <see cref="Update"/> would silently keep the old restriction
    /// instead.
    /// </summary>
    public void SetSharedSections(string? sharedSections) => SharedSections = sharedSections;

    public void SetTranscript(string? rawTranscript, string? audioUrl = null)
    {
        RawTranscript = rawTranscript;
        if (audioUrl is not null) AudioUrl = audioUrl;
    }

    /// <summary>
    /// Signs the visit off.
    ///
    /// <c>CompletedAt</c> is OVERWRITTEN, not stamped once: <c>PUT /api/visits/{id}/complete</c>
    /// has no idempotency guard and writes <c>completedAt: new Date()</c> on every call
    /// (visits.js:513), then echoes the fresh value back. A stamp-once <c>??=</c> would return
    /// the first completion's instant where Node returns the latest.
    /// </summary>
    public void Complete(DateTime? completedAt = null)
    {
        Status = VisitStatus.COMPLETED;
        CompletedAt = completedAt ?? DateTime.UtcNow;
    }

    public void SetFollowUp(DateTime? followUpDate, string? followUpNotes)
    {
        FollowUpDate = followUpDate;
        FollowUpNotes = followUpNotes;
    }

    /// <summary>
    /// The patient's post-visit note.
    ///
    /// Clearing it nulls the timestamp too: <c>PATCH /api/visits/{id}/patient-note</c> writes
    /// <c>patientFeedbackAt: note ? new Date() : null</c>, so an emptied note leaves no
    /// "written at" behind. Always stamping would keep a timestamp for feedback that no longer
    /// exists.
    /// </summary>
    public void SetPatientFeedback(string? feedback)
    {
        PatientFeedback = feedback;
        PatientFeedbackAt = string.IsNullOrEmpty(feedback) ? null : DateTime.UtcNow;
    }

    /// <summary>
    /// Hides the visit from the patient's inbox, leaving the physician's record intact.
    ///
    /// Overwrites the timestamp on a repeat dismiss, matching the Node update — a stamp-once
    /// <c>??=</c> would keep the first instant.
    /// </summary>
    public void DismissForPatient() => PatientDismissedAt = DateTime.UtcNow;

    /// <summary>
    /// What <c>DELETE /api/visits/{id}</c> actually does. It moves the status to ARCHIVED and
    /// does NOT write <see cref="DeletedAt"/> — the physician stops seeing the visit in their
    /// list, and the patient deliberately keeps access to the archived record.
    ///
    /// Use this, not <see cref="SoftDelete"/>, for the delete endpoint.
    /// </summary>
    public void Archive() => Status = VisitStatus.ARCHIVED;

    /// <summary>
    /// Writes <see cref="DeletedAt"/>. Nothing in <c>visits.js</c> calls the equivalent — the
    /// column exists and is read by the GET /api/visits list, but only an administrative path
    /// outside this module would ever set it. Kept for that, and for parity with the schema.
    /// </summary>
    public void SoftDelete() => DeletedAt ??= DateTime.UtcNow;
}

public enum VisitStatus { IN_PROGRESS, COMPLETED, CANCELLED, ARCHIVED }
