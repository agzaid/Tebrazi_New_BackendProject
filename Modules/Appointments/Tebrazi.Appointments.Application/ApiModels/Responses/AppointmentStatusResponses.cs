using Tebrazi.Appointments.Domain.Entities;

namespace Tebrazi.Appointments.Application.ApiModels.Responses;

// ─────────────────────────────────────────────────────────────────────────────
// Response records for the four status transitions —
// PUT /{id}/confirm, PUT /{id}/cancel, PUT /{id}/complete, PUT /{id}/no-show.
//
// All four answer `res.json({ ...updated, message })` (appointments.js:778, :848, :883, :917), so
// the body is the 22 Appointment scalars in PRISMA DECLARATION ORDER followed by a trailing
// `message`. None of the four `prisma.appointment.update` calls carries an `include`, so
// `clinic` / `physician` / `subprofile` are ABSENT — not null — and must not be added.
//
// One record per endpoint, deliberately. The four key sets happen to be identical today, but each
// one pins a different invariant (`status`, the `message` literal, and which `*At` column the
// transition is guaranteed to have stamped), and a shared record would let a future edit to one
// route silently reshape the other three. Record declaration order IS the JSON key order, so
// `message` must stay last in every one of them.
//
// DELETE /{id} is NOT here: it answers the bare `{"message":"Appointment deleted"}` and therefore
// uses the shared <see cref="MessageResponse"/>.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <c>PUT /api/appointments/{id}/confirm</c> → 200 (appointments.js:760-778).
///
/// <c>status</c> is always <c>"CONFIRMED"</c> and <c>confirmedAt</c> is always non-null and freshly
/// stamped — the update writes <c>confirmedAt: new Date()</c> unconditionally, so a re-confirm moves
/// the timestamp forward and this body reports the NEW instant. <c>message</c> is
/// <c>"Appointment confirmed"</c>.
///
/// Nothing else is touched: a row that was cancelled earlier keeps its <c>cancelledAt</c> and
/// <c>cancelReason</c> while reading <c>CONFIRMED</c>.
/// </summary>
public sealed record StatusConfirmedResponse(
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
    string Message);

/// <summary>
/// <c>PUT /api/appointments/{id}/cancel</c> → 200 (appointments.js:810-848).
///
/// <c>status</c> is always <c>"CANCELLED"</c> and <c>cancelledAt</c> always freshly stamped.
///
/// <c>cancelReason</c> and <c>reason</c> are DIFFERENT columns and both appear here: <c>reason</c> is
/// the untouched booking reason, <c>cancelReason</c> the newly supplied cancellation reason. A client
/// reading <c>reason</c> gets the wrong one. <c>cancelReason</c> is <c>reason || null</c>
/// (appointments.js:815), so an empty-string body reason lands here as <c>null</c>, and a second
/// cancel with no body NULLS OUT the reason the first one stored.
///
/// <c>message</c> is <c>"Appointment cancelled"</c>.
/// </summary>
public sealed record StatusCancelledResponse(
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
    string Message);

/// <summary>
/// <c>PUT /api/appointments/{id}/complete</c> → 200 (appointments.js:878-883).
///
/// <c>status</c> is always <c>"COMPLETED"</c> and <c>completedAt</c> always freshly stamped
/// (appointments.js:880 writes it unconditionally, so a second complete bumps it).
/// <c>cancelledAt</c> / <c>cancelReason</c> are NOT cleared by the transition, so a row can report
/// <c>COMPLETED</c> with a non-null <c>cancelledAt</c> left over from an earlier cancel.
///
/// <c>message</c> is <c>"Appointment completed"</c>.
/// </summary>
public sealed record StatusCompletedResponse(
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
    string Message);

/// <summary>
/// <c>PUT /api/appointments/{id}/no-show</c> → 200 (appointments.js:913-917).
///
/// <c>status</c> is always the underscored <c>"NO_SHOW"</c> — the hyphen belongs to the ROUTE
/// segment only. This is the one transition that stamps NO timestamp: there is no <c>noShowAt</c>
/// column, so <c>confirmedAt</c>, <c>completedAt</c> and <c>cancelledAt</c> all keep whatever they
/// held and <c>updatedAt</c> is the only evidence of when it happened.
///
/// <c>message</c> is <c>"Marked as no-show"</c>, which breaks the <c>"Appointment &lt;verb&gt;"</c>
/// pattern of its three siblings. Do not normalize it.
/// </summary>
public sealed record StatusNoShowResponse(
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
    string Message);

/// <summary>
/// Maps the <see cref="Appointment"/> aggregate onto the four status-transition bodies.
///
/// Two things every method below repeats on purpose:
/// <list type="bullet">
/// <item><c>Status</c> and <c>AppointmentType</c> are projected with <c>ToString()</c>. Prisma emits
/// bare enum STRINGS (<c>"CONFIRMED"</c>, <c>"IN_PERSON"</c>), and rendering them here rather than
/// relying on the API's global <c>JsonStringEnumConverter</c> keeps the contract inside the DTO.</item>
/// <item><c>UpdatedAt</c> falls back to <c>CreatedAt</c>. Prisma's <c>@updatedAt</c> is populated on
/// INSERT as well, so the column is never null on the wire, whereas
/// <c>MutableEntity.UpdatedAt</c> is null until the row is first modified.</item>
/// </list>
/// </summary>
public static class StatusAppointmentMapper
{
    public static StatusConfirmedResponse ToConfirmedResponse(Appointment appointment, string message)
        => new(
            appointment.Id, appointment.ClinicId, appointment.PhysicianId, appointment.PatientUserId,
            appointment.SubprofileId, appointment.ClinicPatientId, appointment.Status.ToString(),
            appointment.AppointmentDate, appointment.StartTime, appointment.EndTime,
            appointment.Reason, appointment.Notes, appointment.AppointmentType.ToString(),
            appointment.ConfirmedAt, appointment.CompletedAt, appointment.CancelledAt,
            appointment.CancelReason, appointment.RecurringRule, appointment.RecurringGroupId,
            appointment.CreatedAt, appointment.UpdatedAt ?? appointment.CreatedAt,
            appointment.DeletedAt, message);

    public static StatusCancelledResponse ToCancelledResponse(Appointment appointment, string message)
        => new(
            appointment.Id, appointment.ClinicId, appointment.PhysicianId, appointment.PatientUserId,
            appointment.SubprofileId, appointment.ClinicPatientId, appointment.Status.ToString(),
            appointment.AppointmentDate, appointment.StartTime, appointment.EndTime,
            appointment.Reason, appointment.Notes, appointment.AppointmentType.ToString(),
            appointment.ConfirmedAt, appointment.CompletedAt, appointment.CancelledAt,
            appointment.CancelReason, appointment.RecurringRule, appointment.RecurringGroupId,
            appointment.CreatedAt, appointment.UpdatedAt ?? appointment.CreatedAt,
            appointment.DeletedAt, message);

    public static StatusCompletedResponse ToCompletedResponse(Appointment appointment, string message)
        => new(
            appointment.Id, appointment.ClinicId, appointment.PhysicianId, appointment.PatientUserId,
            appointment.SubprofileId, appointment.ClinicPatientId, appointment.Status.ToString(),
            appointment.AppointmentDate, appointment.StartTime, appointment.EndTime,
            appointment.Reason, appointment.Notes, appointment.AppointmentType.ToString(),
            appointment.ConfirmedAt, appointment.CompletedAt, appointment.CancelledAt,
            appointment.CancelReason, appointment.RecurringRule, appointment.RecurringGroupId,
            appointment.CreatedAt, appointment.UpdatedAt ?? appointment.CreatedAt,
            appointment.DeletedAt, message);

    public static StatusNoShowResponse ToNoShowResponse(Appointment appointment, string message)
        => new(
            appointment.Id, appointment.ClinicId, appointment.PhysicianId, appointment.PatientUserId,
            appointment.SubprofileId, appointment.ClinicPatientId, appointment.Status.ToString(),
            appointment.AppointmentDate, appointment.StartTime, appointment.EndTime,
            appointment.Reason, appointment.Notes, appointment.AppointmentType.ToString(),
            appointment.ConfirmedAt, appointment.CompletedAt, appointment.CancelledAt,
            appointment.CancelReason, appointment.RecurringRule, appointment.RecurringGroupId,
            appointment.CreatedAt, appointment.UpdatedAt ?? appointment.CreatedAt,
            appointment.DeletedAt, message);
}
