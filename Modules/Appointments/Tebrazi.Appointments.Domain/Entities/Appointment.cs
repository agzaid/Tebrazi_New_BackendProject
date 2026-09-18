using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Appointments.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Appointment</c> model (table <c>appointments</c>).
///
/// <c>StartTime</c> and <c>EndTime</c> are STRINGS ("09:00", "09:30"), not times — that is the
/// Node schema, and the client compares and sorts them as strings. Storing them as
/// <c>TimeOnly</c> would serialize as "09:00:00" and break every such comparison.
///
/// The date lives separately in <c>AppointmentDate</c>, so a slot is identified by the pair.
/// </summary>
public sealed class Appointment : MutableEntity<string>
{
    private Appointment() { }

    public string ClinicId { get; private set; } = null!;
    public string PhysicianId { get; private set; } = null!;
    public string PatientUserId { get; private set; } = null!;
    public string? SubprofileId { get; private set; }
    public string? ClinicPatientId { get; private set; }

    public AppointmentStatus Status { get; private set; } = AppointmentStatus.PENDING;

    public DateTime AppointmentDate { get; private set; }

    /// <summary>"HH:mm", inclusive.</summary>
    public string StartTime { get; private set; } = null!;

    /// <summary>"HH:mm", exclusive — an 09:00-09:30 slot does not overlap a 09:30-10:00 one.</summary>
    public string EndTime { get; private set; } = null!;

    public string? Reason { get; private set; }
    public string? Notes { get; private set; }

    public AppointmentType AppointmentType { get; private set; } = AppointmentType.IN_PERSON;

    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }

    /// <summary>"WEEKLY", "BIWEEKLY" or "MONTHLY". Null means a one-off.</summary>
    public string? RecurringRule { get; private set; }

    /// <summary>Ties the instances of one recurring series together so they can be read as a set.</summary>
    public string? RecurringGroupId { get; private set; }

    /// <summary>
    /// Soft delete, for schema parity only. There is deliberately NO query filter on
    /// <c>AppointmentsDbContext</c> and NOTHING in <c>appointments.js</c> reads or writes this
    /// column — the string <c>deletedAt</c> does not appear in its 1,559 lines. See
    /// <see cref="SoftDelete"/>.
    /// </summary>
    public DateTime? DeletedAt { get; private set; }

    public static Appointment Create(
        string clinicId,
        string physicianId,
        string patientUserId,
        DateTime appointmentDate,
        string startTime,
        string endTime,
        string? subprofileId = null,
        string? clinicPatientId = null,
        string? reason = null,
        string? notes = null,
        AppointmentType appointmentType = AppointmentType.IN_PERSON,
        AppointmentStatus status = AppointmentStatus.PENDING,
        string? recurringRule = null,
        string? recurringGroupId = null)
    {
        // DELIBERATELY UNVALIDATED BEYOND NULL, and DELIBERATELY UNTRIMMED. All three creating
        // routes — POST /, POST /recurring, POST /walk-in — write these columns exactly as they
        // arrived: appointments.js:463-480, :720-726 and :592-600 pass startTime and endTime
        // straight through with no trim and no format check. The contract's own quirk list says
        // so: "any startTime/endTime string is accepted, including out-of-hours values and
        // non-time garbage".
        //
        // The presence guards in the handlers are `!startTime`, i.e. JS falsiness, so a
        // whitespace-only "  " is TRUTHY, passes the 400, and is stored verbatim with a 201. A
        // ThrowIfNullOrWhiteSpace here would escalate that 201 into an uncaught ArgumentException
        // and the middleware's generic 500 body, and a Trim() would silently rewrite a padded
        // " 09:00" that Node keeps padded — and StartTime is compared and sorted AS A STRING all
        // the way out to the client, so the padding is observable.
        //
        // Null is the one genuine rejection: these columns are NOT NULL, so Prisma fails the
        // statement and the route answers its own 500.
        ArgumentNullException.ThrowIfNull(clinicId);
        ArgumentNullException.ThrowIfNull(physicianId);
        ArgumentNullException.ThrowIfNull(patientUserId);
        ArgumentNullException.ThrowIfNull(startTime);
        ArgumentNullException.ThrowIfNull(endTime);

        return new Appointment
        {
            Id = Guid.NewGuid().ToString(),
            ClinicId = clinicId,
            PhysicianId = physicianId,
            PatientUserId = patientUserId,
            SubprofileId = subprofileId,
            ClinicPatientId = clinicPatientId,
            Status = status,
            AppointmentDate = appointmentDate,
            StartTime = startTime,
            EndTime = endTime,
            Reason = reason,
            Notes = notes,
            AppointmentType = appointmentType,
            RecurringRule = recurringRule,
            RecurringGroupId = recurringGroupId
        };
    }

    /// <summary>Partial update: null leaves a field alone, matching the Node PUT semantics.</summary>
    public void Update(
        DateTime? appointmentDate = null,
        string? startTime = null,
        string? endTime = null,
        string? reason = null,
        string? notes = null,
        AppointmentType? appointmentType = null)
    {
        if (appointmentDate.HasValue) AppointmentDate = appointmentDate.Value;
        if (!string.IsNullOrWhiteSpace(startTime)) StartTime = startTime.Trim();
        if (!string.IsNullOrWhiteSpace(endTime)) EndTime = endTime.Trim();
        if (reason is not null) Reason = reason;
        if (notes is not null) Notes = notes;
        if (appointmentType.HasValue) AppointmentType = appointmentType.Value;
    }

    /// <summary>
    /// <c>PUT /api/appointments/{id}/confirm</c>. <see cref="ConfirmedAt"/> is OVERWRITTEN, not
    /// preserved — appointments.js:762 writes <c>confirmedAt: new Date()</c> unconditionally, so
    /// re-confirming an already-confirmed appointment moves the timestamp and the response body
    /// shows the new one. <see cref="CheckIn"/> is the opposite and deliberately so.
    ///
    /// Also used by <c>POST /api/appointments/walk-in</c>, which creates the row CONFIRMED with
    /// <c>confirmedAt: now</c> (appointments.js:597-598): call <see cref="Create"/> and then
    /// this.
    /// </summary>
    public void Confirm()
    {
        Status = AppointmentStatus.CONFIRMED;
        ConfirmedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// <c>PUT /api/appointments/{id}/complete</c>. <see cref="CompletedAt"/> is OVERWRITTEN:
    /// appointments.js:880 writes <c>completedAt: new Date()</c> unconditionally.
    /// </summary>
    public void Complete()
    {
        Status = AppointmentStatus.COMPLETED;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// <c>PUT /api/appointments/{id}/cancel</c>. <see cref="CancelledAt"/> is OVERWRITTEN
    /// (appointments.js:814), and <paramref name="reason"/> is stored verbatim with no length
    /// limit — pass null for an absent OR EMPTY body reason, because
    /// <c>cancelReason: reason || null</c> (appointments.js:815) turns "" into null and the
    /// response then reports null for a reason the client believes it sent.
    /// </summary>
    public void Cancel(string? reason)
    {
        Status = AppointmentStatus.CANCELLED;
        CancelledAt = DateTime.UtcNow;
        CancelReason = reason;
    }

    /// <summary>
    /// <c>PUT /api/appointments/{id}/no-show</c>. Status ONLY — appointments.js:914 writes no
    /// timestamp, so <see cref="CancelledAt"/> and <see cref="CompletedAt"/> stay as they were.
    /// </summary>
    public void MarkNoShow() => Status = AppointmentStatus.NO_SHOW;

    /// <summary>
    /// The literal that marks a check-in inside <see cref="Notes"/>.
    ///
    /// It is a PARSING CONTRACT, not an implementation detail:
    /// <c>GET /api/appointments/queue</c> recovers <c>checkedIn</c>, <c>checkedInAt</c> and
    /// <c>room</c> by testing <c>notes.includes('CHECKIN:')</c> and JSON-parsing everything after
    /// the LAST occurrence (appointments.js:1260-1268). Both sides must use this constant so they
    /// cannot drift apart.
    /// </summary>
    public const string CheckInMarker = "CHECKIN:";

    /// <summary>
    /// <c>PUT /api/appointments/{id}/check-in</c> (appointments.js:1006-1013). Three writes in
    /// one, none of which the other transitions perform:
    ///
    /// <list type="bullet">
    /// <item>status becomes CONFIRMED — a check-in is also a confirmation;</item>
    /// <item><see cref="ConfirmedAt"/> is PRESERVED when already set
    /// (<c>appt.confirmedAt || new Date()</c>), the opposite of <see cref="Confirm"/>;</item>
    /// <item><paramref name="checkInPayloadJson"/> is APPENDED to <see cref="Notes"/> behind
    /// <see cref="CheckInMarker"/>, separated by a SINGLE newline. Existing notes are kept, so a
    /// walk-in reads "WALK_IN\nCHECKIN:{...}" and a third check-in stacks a third marker.
    /// <see cref="AppendNote"/> cannot be used here — it separates with a blank line, which
    /// would change the bytes the queue endpoint parses.</item>
    /// </list>
    /// </summary>
    /// <param name="checkInPayloadJson">
    /// The serialized <c>{ checkedInAt, checkedInBy, room }</c> object, WITHOUT the marker
    /// prefix. Built by the handler because it carries the caller's user id and the request's
    /// room number.
    /// </param>
    public void CheckIn(string checkInPayloadJson)
    {
        ArgumentNullException.ThrowIfNull(checkInPayloadJson);

        Status = AppointmentStatus.CONFIRMED;
        ConfirmedAt ??= DateTime.UtcNow;

        var marked = $"{CheckInMarker}{checkInPayloadJson}";

        // `appt.notes ? ... : ...` — JavaScript falsiness, so an empty string counts as no notes
        // and must NOT produce a leading newline.
        Notes = string.IsNullOrEmpty(Notes) ? marked : $"{Notes}\n{marked}";
    }

    /// <summary>
    /// Appends to the appointment's notes rather than replacing them — the intake note is
    /// written by reception before the physician has seen the patient, and must not erase
    /// whatever the booking already carried.
    /// </summary>
    public void AppendNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;

        Notes = string.IsNullOrWhiteSpace(Notes) ? note.Trim() : $"{Notes}\n\n{note.Trim()}";
    }

    public void SetNotes(string? notes) => Notes = notes;

    /// <summary>
    /// Writes <see cref="DeletedAt"/>. Nothing in <c>appointments.js</c> calls the equivalent —
    /// <c>DELETE /api/appointments/{id}</c> is a HARD delete
    /// (<c>prisma.appointment.delete</c>, appointments.js:948), so use
    /// <c>IAppointmentStore.Remove</c> for that endpoint. The column exists for schema parity
    /// and for an administrative path outside this module.
    /// </summary>
    public void SoftDelete() => DeletedAt ??= DateTime.UtcNow;
}

public enum AppointmentStatus { PENDING, CONFIRMED, COMPLETED, CANCELLED, NO_SHOW }

public enum AppointmentType { IN_PERSON, VIDEO, PHONE, WALK_IN }
