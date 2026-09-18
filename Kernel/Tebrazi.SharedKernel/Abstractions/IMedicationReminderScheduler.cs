namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The published write port onto <c>reminders</c>, needed by exactly one endpoint:
/// <c>PUT /api/prescriptions/{id}/send</c> auto-creates DAILY-recurring MEDICATION reminders for
/// the patient, one per dose time per drug, after first purging that drug's existing ACTIVE
/// medication reminders (prescriptions.js:347-410). Prescriptions does not own the table — the
/// Reminders module will, and it does not exist yet.
///
/// <para><b>The schedule itself is computed on the CALLER's side of this port, not in here.</b>
/// <c>parseFrequency</c> / <c>getDefaultDoseTimes</c>
/// (server/src/utils/medicationScheduleUtils.js) turn a prescription's own free-text
/// <c>frequency</c> string into a dose count and a list of "HH:mm" times, and the titles,
/// descriptions and <c>notes</c> JSON are literals composed by the prescriptions route. All of
/// that is contract belonging to <c>/send</c>, byte-visible in the rows it writes, so it stays
/// where a reviewer can compare it against the Node handler:
/// <c>Tebrazi.Prescriptions.Application.Services.MedicationSchedule</c>. This port therefore
/// receives rows that are already fully formed and only has to write them.</para>
///
/// <para><b>This port is BEST-EFFORT and never throws.</b> The Node call site wraps the entire
/// reminder block in its own try/catch that logs and continues (prescriptions.js:346, :411), so a
/// reminder that cannot be written must not turn a committed SENT prescription into a 500. The
/// response body carries nothing about the reminders either (no count, no ids), so the caller has
/// nothing to report. A handler MUST NOT treat a completed call as proof rows were written.</para>
///
/// <para>Because it commits through the Reminders unit of work, a call from inside
/// <c>ExecuteInTransactionAsync</c> is NOT covered by that transaction. Schedule after the
/// prescription write has committed, which is also the Node ordering.</para>
/// </summary>
public interface IMedicationReminderScheduler
{
    /// <summary>
    /// Purges and re-creates the patient's medication reminders for one prescription. For each
    /// group, in order: delete every reminder of this patient whose <c>type</c> is "MEDICATION",
    /// whose <c>status</c> is "ACTIVE" and whose <c>title</c> CONTAINS
    /// <see cref="MedicationReminderGroup.DrugName"/> — a case-SENSITIVE substring match, which
    /// is why it also wipes that drug's reminders originating from any OTHER prescription
    /// (prescriptions.js:369-377) — then insert every row in
    /// <see cref="MedicationReminderGroup.Doses"/>.
    ///
    /// <para>The return value drives the caller's OWN observable side effect: the
    /// <c>MEDICATION_REMINDER_SET</c> notification fires only when it is greater than zero
    /// (prescriptions.js:413-427). So the count matters even though the HTTP body never shows
    /// it.</para>
    /// </summary>
    /// <param name="plan">The purge keys and the fully formed reminder rows.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of reminder rows written. <b>Zero on any failure, and never a partial count</b>
    /// — a mid-loop throw in Node skips the notification entirely because the
    /// <c>if (remindersCreated > 0)</c> test lives inside the same try block, so reporting a
    /// partial count would fire a notification Node would not. Rows already written before the
    /// failure stay written, exactly as in Node; only the reported count is squashed.
    /// </returns>
    Task<int> ScheduleAsync(MedicationReminderPlan plan, CancellationToken ct = default);
}

/// <summary>
/// One prescription's worth of medication reminders. Everything is resolved by the caller so the
/// values the Node route actually writes stay visible at the call site.
/// </summary>
/// <param name="PatientUserId">
/// <c>reminders.user_id</c> — whose reminder it is. Resolved from the prescription's visit
/// (<c>visit.patientUserId</c>, prescriptions.js:351-355), NOT the caller. The Node block runs
/// only when this resolves to a truthy value, so a caller with no patient must not call at all.
/// </param>
/// <param name="CreatedByUserId">
/// <c>reminders.created_by_id</c> — the SENDING PHYSICIAN's user id (<c>req.user.id</c>,
/// prescriptions.js:394). Note the asymmetry the Node code leaves in place:
/// <c>reminders.patient_user_id</c> is NOT set on this path even though the schema comment says
/// it is populated when a physician creates a reminder for a patient.
/// </param>
/// <param name="PrescriptionId">
/// <c>reminders.prescription_id</c>, stamped on every created row (prescriptions.js:401). It is
/// the raw <c>:id</c> route segment in Node, not the reloaded row's id.
/// </param>
/// <param name="Medications">
/// One group per medication that has BOTH a <c>drugName</c> and a <c>frequency</c>; Node skips
/// the others outright (<c>if (!med.frequency || !med.drugName) continue</c>,
/// prescriptions.js:361), so a skipped medication contributes neither a purge nor a row. Groups
/// are processed in array order, and the purge for group N runs before group N's inserts —
/// which matters when two medications share a substring.
/// </param>
public sealed record MedicationReminderPlan(
    string PatientUserId,
    string CreatedByUserId,
    string PrescriptionId,
    IReadOnlyList<MedicationReminderGroup> Medications);

/// <summary>One drug: its purge key and its dose rows.</summary>
/// <param name="DrugName">
/// The purge key, used as a case-sensitive <c>contains</c> against existing reminder titles. It
/// is the medication's raw <c>drugName</c> — NOT the emoji-prefixed title — so a short name that
/// is a substring of another drug's name will purge that other drug's reminders too
/// (prescriptions.js:373).
/// </param>
/// <param name="Doses">
/// The rows to insert, in dose order. Never empty for a group that reaches here: a resolved
/// schedule is always 1-12 doses.
/// </param>
public sealed record MedicationReminderGroup(
    string DrugName,
    IReadOnlyList<MedicationReminderRow> Doses);

/// <summary>
/// One <c>reminders</c> row, fully composed. Nothing in here is derived by the implementation:
/// every string is a literal the prescriptions route builds, and reproducing them elsewhere would
/// put them out of reach of a byte-for-byte comparison against Node.
/// </summary>
/// <param name="Title">
/// <c>"💊 {drugName}{doseLabel}"</c> — the pill emoji, a space, the drug name, then
/// <c>" (Dose {i+1}/{dosesPerDay})"</c> when there is more than one dose a day and the empty
/// string when there is exactly one (prescriptions.js:396). Also what the NEXT send's purge
/// substring-matches against.
/// </param>
/// <param name="Description">
/// <c>"{dosage} — Take at {timeStr}"</c>, or <c>"Take at {timeStr}"</c> when the medication has
/// no dosage — the separator is an em dash U+2014 with a space either side, and it disappears
/// entirely rather than leaving a dangling dash (prescriptions.js:397).
/// </param>
/// <param name="RemindAt">
/// The first occurrence: today at the dose's local wall-clock time, bumped to tomorrow when that
/// instant has already passed (prescriptions.js:385-391). Computed in the SERVER's local
/// timezone, not the patient's, and passed as a UTC instant.
/// </param>
/// <param name="Notes">
/// A JSON STRING, not a JSON column: <c>JSON.stringify({ drugName, dosage, frequency,
/// doseNumber, totalDoses, intervalHours })</c> with those keys in that order
/// (prescriptions.js:402). Stored as text verbatim.
/// </param>
/// <param name="Type">
/// <c>reminders.type</c>. Always "MEDICATION" on this path, and the purge filters on the same
/// literal.
/// </param>
/// <param name="Recurrence">
/// <c>reminders.recurrence</c>. Always "DAILY" on this path (prescriptions.js:400).
/// </param>
public sealed record MedicationReminderRow(
    string Title,
    string Description,
    DateTime RemindAt,
    string Notes,
    string Type = "MEDICATION",
    string Recurrence = "DAILY");
