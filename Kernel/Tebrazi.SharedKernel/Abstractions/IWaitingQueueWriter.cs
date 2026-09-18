namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The published write port onto <c>waiting_queues</c> — the clinic's live waiting room.
///
/// Two Appointments endpoints push onto it as a SIDE EFFECT of their own write:
/// <c>PUT /api/appointments/{id}/check-in</c> (appointments.js:1029-1062) and
/// <c>POST /api/appointments/walk-in</c> (appointments.js:624-687). Neither owns the table — the
/// WaitingRoom module will, and it does not exist yet.
///
/// <para>The whole "is this patient already queued today, and if not what is the next number"
/// decision lives BEHIND this port rather than in the calling handler, deliberately. Node runs
/// it as three unsynchronised queries (a <c>findFirst</c> for an existing entry, a
/// <c>findFirst</c> ordered by <c>queueNumber desc</c>, then a <c>create</c>), so two concurrent
/// check-ins can be handed the same number. Whoever implements this owns that race; a caller
/// cannot fix it from outside.</para>
///
/// <para><b>This port never throws.</b> Both Node call sites wrap the enqueue in their own
/// try/catch that logs at warning level and continues — a waiting-room entry that cannot be
/// written must not turn a successful check-in into a 500, and NEITHER endpoint's response body
/// mentions the queue, so the caller has nothing to report either way. The guarantee lives here
/// so the two handlers do not repeat the try/catch. A handler MUST NOT treat a completed call as
/// proof a row was written.</para>
///
/// <para>Because it commits through the WaitingRoom unit of work, a call from inside
/// <c>ExecuteInTransactionAsync</c> is NOT covered by that transaction. Enqueue after the
/// appointment write has committed, which is also the Node ordering.</para>
///
/// <para><b>There is no read method here on purpose.</b>
/// <c>GET /api/appointments/queue</c> reads <c>appointments</c> and <c>visits</c> — it never
/// touches <c>waiting_queues</c> (appointments.js:1236-1287), and it derives its
/// <c>checkedIn</c>/<c>checkedInAt</c>/<c>room</c> flags by parsing the <c>CHECKIN:</c> marker
/// out of <c>appointments.notes</c>. That endpoint therefore returns fully real data with this
/// port unimplemented. See docs/appointments-surface.md.</para>
/// </summary>
public interface IWaitingQueueWriter
{
    /// <summary>
    /// Adds the patient to the clinic's queue for <see cref="WaitingQueueEnrolment.QueueDate"/>
    /// unless they already hold a WAITING or BEING_SEEN entry that day, assigning the next
    /// <c>queueNumber</c> for that clinic and day.
    ///
    /// Idempotent per (clinic, patient, day) by that status test alone — a patient whose earlier
    /// entry was COMPLETED or CANCELLED is enqueued again, which is what lets someone be seen
    /// twice in one day.
    /// </summary>
    Task EnqueueIfAbsentAsync(WaitingQueueEnrolment enrolment, CancellationToken ct = default);
}

/// <summary>
/// One prospective waiting-room entry. Every field is supplied by the caller rather than
/// resolved in here, so the values the Node route actually writes stay visible at the call site
/// — including the ones that look wrong.
/// </summary>
/// <param name="ClinicId">The clinic whose waiting room is being joined.</param>
/// <param name="PhysicianUserId">
/// A USER id, not a physician-profile id: <c>waiting_queues.physician_user_id</c>.
/// <c>/walk-in</c> resolves it from the appointment's physician profile and falls back to the
/// CALLER's own user id when that profile cannot be read
/// (<c>physicianProfile?.userId || userId</c>, appointments.js:673), so a receptionist can end up
/// recorded as the physician. Reproduce that fallback at the call site.
/// </param>
/// <param name="PatientUserId">
/// The patient's account id. Nullable on the table for chart-only patients, but both
/// appointment paths always have one, so it is required here.
/// </param>
/// <param name="PatientName">
/// Denormalised for display. <c>/walk-in</c> uses the DEPENDANT's name when the booking is for a
/// family member and the account's display name otherwise, defaulting to the literal "Patient"
/// (appointments.js:658-667). <c>/check-in</c> never substitutes the dependant
/// (appointments.js:1053).
/// </param>
/// <param name="AppointmentId">
/// The appointment this entry was created from, stored on the row so the waiting room can link
/// back to the booking.
/// </param>
/// <param name="SubprofileId">
/// The dependant the booking is for, when there is one. Always null on the check-in path, which
/// does not copy it off the appointment (appointments.js:1048-1060).
/// </param>
/// <param name="SubprofileName">
/// Set only by <c>/walk-in</c>, and only when the dependant resolved. Null on the check-in path.
/// </param>
/// <param name="Reason">
/// Free text shown in the queue. <c>/walk-in</c> defaults it to the literal "Walk-in";
/// <c>/check-in</c> copies the appointment's own reason and leaves it null when there is none.
/// </param>
/// <param name="Source">
/// <c>waiting_queues.source</c>. Both appointment paths pass "APPOINTMENT" — including
/// <c>/walk-in</c>, despite the column's own default being "WALK_IN"
/// (appointments.js:679, :1055).
/// </param>
/// <param name="QueueDate">
/// Local midnight of the day being queued, matching
/// <c>new Date(y, m, d)</c> at appointments.js:627 and :1031. The existing-entry check and the
/// <c>queueNumber</c> scan both run over this day.
/// </param>
public sealed record WaitingQueueEnrolment(
    string ClinicId,
    string PhysicianUserId,
    string PatientUserId,
    string PatientName,
    string AppointmentId,
    DateTime QueueDate,
    string? SubprofileId = null,
    string? SubprofileName = null,
    string? Reason = null,
    string Source = "APPOINTMENT");
