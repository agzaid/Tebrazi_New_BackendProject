using Tebrazi.Appointments.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Appointments.Application.ApiModels.Responses;

// ─────────────────────────────────────────────────────────────────────────────
// Response records for the two ASSISTANT endpoints of server/src/routes/appointments.js —
// PUT /api/appointments/{id}/check-in (appointments.js:966-1118) and
// POST /api/appointments/{id}/intake-note (appointments.js:1126-1203).
//
// Two unrelated shapes, and neither one is "an appointment": the check-in body is the appointment
// row with THREE extra top-level keys welded on, and the intake-note body is a PatientNote row
// (a table this module does not own) with a `message` key welded on. Record declaration order IS
// the JSON key order, so each record mirrors the Prisma model's field order followed by whatever
// the Express handler spread after it.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <c>PUT /api/appointments/{id}/check-in</c> → 200. The updated appointment's 22 scalars
/// followed by <c>message</c>, <c>checkedInBy</c> and <c>payment</c>, in that order
/// (appointments.js:1109-1113).
///
/// <para><b>No relation objects.</b> The response is built from <c>prisma.appointment.update</c>
/// with no <c>include</c>, so <c>clinic</c>, <c>physician</c>, <c>subprofile</c> and
/// <c>patientUser</c> are ABSENT even though the handler read the clinic (twice) and the
/// physician for its own use. Do not "improve" this by adding them — the client's stored copy of
/// the appointment would gain keys the Express backend never sends.</para>
///
/// <para><b>There is no top-level <c>checkedInAt</c>.</b> The timestamp exists only inside the
/// JSON blob appended to <c>notes</c> behind <see cref="Appointment.CheckInMarker"/>, and
/// <c>GET /api/appointments/queue</c> parses it back out of there. <c>checkedInBy</c> is the one
/// part of that blob that is also promoted to a top-level key, and it is response-only — no
/// column backs it.</para>
/// </summary>
public sealed record AssistantCheckInResponse(
    string Id,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    DateTime AppointmentDate,
    string StartTime,
    string EndTime,
    string? Reason,
    string? Notes,
    string AppointmentType,
    DateTime? ConfirmedAt,
    DateTime? CompletedAt,
    DateTime? CancelledAt,
    string? CancelReason,
    string? RecurringRule,
    string? RecurringGroupId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    string Message,
    string CheckedInBy,
    AssistantCheckInPaymentResponse? Payment);

/// <summary>
/// The <c>payment</c> object on the check-in response — exactly three keys, hand-built at
/// appointments.js:1112 rather than spread from the created row, so <c>status</c>,
/// <c>method</c>, <c>currency</c>, <c>clinicId</c> and <c>createdAt</c> must NOT appear.
///
/// <para>The whole object is <c>null</c> in four distinct situations, which the client cannot
/// tell apart: a payment already referenced this appointment (so a repeat check-in reports null
/// while a PENDING payment does exist), the clinic row could not be read, the resolved fee was
/// not greater than zero, or the payment write failed. Null therefore does NOT mean "no payment
/// exists".</para>
/// </summary>
/// <param name="Id">The created payment's id.</param>
/// <param name="Amount">
/// Prisma <c>Float</c> → a plain JSON number: <c>300</c>, never <c>300.0</c>.
/// <see cref="double"/> is what makes <c>System.Text.Json</c> write it that way.
/// </param>
/// <param name="Description">
/// Either <c>"Consultation fee"</c> or <c>"Follow-up visit fee"</c>, echoed verbatim from the
/// created row.
/// </param>
public sealed record AssistantCheckInPaymentResponse(string Id, double Amount, string? Description);

/// <summary>
/// <c>POST /api/appointments/{id}/intake-note</c> → <b>200, not 201</b> (appointments.js:1200,
/// which returns 200 even on the create path). The nine <c>PatientNote</c> scalars in schema
/// order, then the appended <c>message</c>.
///
/// <para>The note is NOT linked to the appointment in any column: there is no
/// <c>appointmentId</c> key, no patient name and no physician object. The <c>{id}</c> path
/// segment was used only to resolve the patient and the physician.</para>
/// </summary>
/// <param name="Id">The stored note's id.</param>
/// <param name="PhysicianUserId">
/// A USER id — the caller's own when the caller is the appointment's physician, the appointment's
/// physician user id when a staff member wrote it.
/// </param>
/// <param name="PatientUserId">The appointment's patient, not the caller.</param>
/// <param name="SubprofileId">
/// <b>Always null.</b> The route never copies <c>appointment.subprofileId</c>, so an intake note
/// for a child's appointment is filed under the account holder.
/// </param>
/// <param name="ClinicPatientId">Always null — the route never sets it.</param>
/// <param name="Content">
/// <c>"[INTAKE by &lt;callerUserId&gt;]\n&lt;trimmed note&gt;"</c>. The raw author UUID is part of
/// the stored text and the client renders it verbatim.
/// </param>
/// <param name="IsPinned">Column default <c>false</c>; this route never sets it.</param>
/// <param name="CreatedAt">Row creation timestamp.</param>
/// <param name="UpdatedAt">
/// Prisma's <c>@updatedAt</c> is stamped on INSERT too, so Node reports a timestamp equal to
/// <paramref name="CreatedAt"/> for a note that has never been edited — never null. The mapper
/// falls back to <paramref name="CreatedAt"/> for exactly that reason.
/// </param>
/// <param name="Message">The literal <c>"Intake note saved"</c>, appended last.</param>
public sealed record AssistantIntakeNoteResponse(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Content,
    bool IsPinned,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string Message);

/// <summary>
/// Projections for the two assistant endpoints. Kept beside the records so the JSON key order and
/// the values that fill it are read together.
/// </summary>
public static class AssistantMapper
{
    /// <summary>
    /// The check-in body. <paramref name="checkedInBy"/> is the ACTING user's id echoed straight
    /// back (appointments.js:1111) — deliberately duplicating the <c>checkedInBy</c> already
    /// buried in the notes blob.
    /// </summary>
    public static AssistantCheckInResponse ToCheckInResponse(
        Appointment appointment,
        string message,
        string checkedInBy,
        AppointmentPaymentSummary? payment)
        => new(
            appointment.Id,
            appointment.ClinicId,
            appointment.PhysicianId,
            appointment.PatientUserId,
            appointment.SubprofileId,
            appointment.ClinicPatientId,
            appointment.Status.ToString(),
            appointment.AppointmentDate,
            appointment.StartTime,
            appointment.EndTime,
            appointment.Reason,
            appointment.Notes,
            appointment.AppointmentType.ToString(),
            appointment.ConfirmedAt,
            appointment.CompletedAt,
            appointment.CancelledAt,
            appointment.CancelReason,
            appointment.RecurringRule,
            appointment.RecurringGroupId,
            appointment.CreatedAt,
            // Prisma's @updatedAt is written on INSERT, so the column is never null in Node;
            // MutableEntity leaves it null until the first save. This row was just saved, so the
            // fallback is belt-and-braces rather than the live path.
            appointment.UpdatedAt ?? appointment.CreatedAt,
            appointment.DeletedAt,
            message,
            checkedInBy,
            payment is null
                ? null
                : new AssistantCheckInPaymentResponse(payment.Id, payment.Amount, payment.Description));

    /// <summary>
    /// The intake-note body: the stored row spread, then <paramref name="message"/>. The row comes
    /// from <see cref="IPatientNoteWriter"/> because <c>patient_notes</c> belongs to the
    /// PatientNotes module, not to Appointments.
    /// </summary>
    public static AssistantIntakeNoteResponse ToIntakeNoteResponse(PatientNoteRecord note, string message)
        => new(
            note.Id,
            note.PhysicianUserId,
            note.PatientUserId,
            note.SubprofileId,
            note.ClinicPatientId,
            note.Content,
            note.IsPinned,
            note.CreatedAt,
            note.UpdatedAt ?? note.CreatedAt,
            message);
}
