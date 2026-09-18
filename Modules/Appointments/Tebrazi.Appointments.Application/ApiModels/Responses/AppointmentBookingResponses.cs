using Tebrazi.Appointments.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Appointments.Application.ApiModels.Responses;

// ─────────────────────────────────────────────────────────────────────────────
// Response records for the three BOOKING endpoints, ported from
// server/src/routes/appointments.js:
//
//   POST /api/appointments          (appointments.js:430-505)
//   POST /api/appointments/recurring(appointments.js:700-735)
//   POST /api/appointments/walk-in  (appointments.js:556-695)
//
// Three shapes, deliberately NOT one shared DTO, because the Prisma `include` differs on every
// one of them:
//
//   * POST /            → 22 scalars + clinic { name } + physician { user { displayName } }
//                         + subprofile { name, relation } | null
//   * POST /walk-in     → 22 scalars + clinic { name } and NOTHING else — no physician and no
//                         subprofile key at all, even when subprofileId was supplied
//                         (appointments.js:599, audit-2)
//   * POST /recurring   → an ENVELOPE { count, groupId, appointments: [...] } whose rows come from
//                         a bare `create` with no include, so they carry the 22 scalars and no
//                         relation objects whatsoever
//
// Note in particular that POST /'s `physician` has NO `specialty`, while GET /api/appointments'
// physician for the same relation name DOES (audit-2, `breaks-client`). Reusing one record across
// the two would put an extra key on this response.
//
// Record declaration order IS the JSON key order, and it mirrors the Prisma model field order the
// Express responses had: the 22 scalars first (id … deletedAt), then the relations.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <c>POST /api/appointments</c> → <b>201</b>. The created appointment plus the three relations of
/// the Prisma <c>include</c> at appointments.js:475-479.
///
/// <para><c>Subprofile</c> is emitted as <c>null</c> — key PRESENT, not omitted — when the booking
/// is not for a family member.</para>
///
/// <para>There is NO <c>patientUser</c> key here, unlike <c>GET /api/appointments</c> and
/// <c>GET /api/appointments/today</c>, which append <c>patientUser: { … } | null</c>. A client
/// reusing one renderer across the two has to tolerate its absence.</para>
/// </summary>
public sealed record BookingCreatedResponse(
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
    BookingClinicNameResponse Clinic,
    BookingCreatedPhysicianResponse Physician,
    BookingCreatedSubprofileResponse? Subprofile);

/// <summary>
/// <c>select: { name: true }</c> — the clinic's name and nothing else. Shared by
/// <c>POST /</c> and <c>POST /walk-in</c>, which project the clinic identically
/// (appointments.js:476 and :599).
///
/// Deliberately NARROWER than <c>GET /api/appointments</c>' <c>clinic</c>, which is
/// <c>{ id, name, address, phone }</c> for the very same rows.
/// </summary>
public sealed record BookingClinicNameResponse(string Name);

/// <summary>
/// <c>physician: { select: { user: { select: { displayName } } } }</c> (appointments.js:477) — a
/// wrapper object carrying ONLY the nested user. No physician <c>id</c>, and — unlike
/// <c>GET /api/appointments</c> — no <c>specialty</c>.
/// </summary>
public sealed record BookingCreatedPhysicianResponse(BookingCreatedPhysicianUserResponse User);

/// <summary>One key. The physician's display name.</summary>
public sealed record BookingCreatedPhysicianUserResponse(string DisplayName);

/// <summary>
/// <c>subprofile: { select: { name: true, relation: true } }</c> (appointments.js:478). No
/// <c>id</c>. <c>Relation</c> stays a STRING carrying the <c>SubprofileRelation</c> enum
/// (<c>SELF|SPOUSE|CHILD|PARENT|SIBLING|OTHER</c>).
/// </summary>
public sealed record BookingCreatedSubprofileResponse(string Name, string Relation);

/// <summary>
/// <c>POST /api/appointments/walk-in</c> → <b>201</b>. The 22 scalars plus <c>clinic { name }</c>,
/// which is the route's ONLY include (appointments.js:599, audit-2).
///
/// <para><c>physician</c>, <c>subprofile</c> and <c>patientUser</c> keys are ABSENT — not null —
/// and so are the created waiting-room row and its <c>queueNumber</c>. The body is byte-identical
/// whether the auto-check-in succeeded, was skipped as a duplicate, or failed, which is why the
/// client has to re-fetch <c>GET /api/appointments/queue</c> to learn the truth.</para>
///
/// <para>Values this route fixes and the request cannot override: <c>Status</c> is always
/// <c>CONFIRMED</c>, <c>ConfirmedAt</c> is never null, <c>Notes</c> is the literal
/// <c>"WALK_IN"</c>, <c>AppointmentType</c> is <c>WALK_IN</c>, and <c>Reason</c> is never null
/// because it falls back to the literal <c>"Walk-in"</c>. <c>EndTime</c> can EQUAL
/// <c>StartTime</c> — a zero-length appointment — when the body omitted it.</para>
/// </summary>
public sealed record BookingWalkInResponse(
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
    BookingClinicNameResponse Clinic);

/// <summary>
/// <c>POST /api/appointments/recurring</c> → <b>201</b>. A hand-built envelope
/// (appointments.js:729), not an appointment row.
///
/// <para><c>Count</c> is <c>created.length</c> — the rows ACTUALLY inserted, not the requested or
/// clamped occurrence count. <c>GroupId</c> is the same value each row carries under the DIFFERENT
/// name <c>recurringGroupId</c>: one payload, two names for one id.</para>
///
/// <para><c>Appointments</c> can be an EMPTY ARRAY on a 201 — never null. A negative or
/// non-numeric <c>count</c> makes the loop body never run, and the client still receives a
/// <c>groupId</c> that belongs to nothing.</para>
/// </summary>
public sealed record BookingRecurringResponse(
    int Count,
    string GroupId,
    IReadOnlyList<BookingRecurringAppointmentResponse> Appointments);

/// <summary>
/// One occurrence. The <c>prisma.appointment.create</c> at appointments.js:718-726 carries NO
/// <c>include</c>, so these rows have no <c>clinic</c>, no <c>physician</c>, no <c>subprofile</c>
/// and no <c>patientUser</c> keys — unlike <c>POST /</c>'s 201 for the same table.
///
/// <para><c>SubprofileId</c>, <c>Notes</c>, <c>ConfirmedAt</c>, <c>CompletedAt</c>,
/// <c>CancelledAt</c>, <c>CancelReason</c> and <c>DeletedAt</c> are always null on this route, and
/// <c>AppointmentType</c> is always the <c>IN_PERSON</c> schema default, because the handler never
/// reads them from the body. <c>RecurringRule</c> is the body's <c>rule</c> stored VERBATIM and
/// unvalidated, so it can disagree with the spacing actually used.</para>
/// </summary>
public sealed record BookingRecurringAppointmentResponse(
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
    DateTime? DeletedAt);

/// <summary>Maps the <see cref="Appointment"/> aggregate onto the booking endpoints' shapes.</summary>
public static class BookingMapper
{
    /// <summary>
    /// <c>POST /api/appointments</c>. <c>physicianDisplayName</c> is the physician's
    /// <c>user.displayName</c>, resolved through <c>IIdentityDirectory</c> because the appointment
    /// row stores only the physician PROFILE id; <c>subprofile</c> is null when the booking is not
    /// for a family member, which renders as a PRESENT <c>"subprofile": null</c> key.
    /// </summary>
    public static BookingCreatedResponse ToCreatedResponse(
        Appointment appointment,
        ClinicSummary clinic,
        string physicianDisplayName,
        SubprofileSummary? subprofile)
        => new(
            appointment.Id, appointment.ClinicId, appointment.PhysicianId, appointment.PatientUserId,
            appointment.SubprofileId, appointment.ClinicPatientId, appointment.Status.ToString(),
            appointment.AppointmentDate, appointment.StartTime, appointment.EndTime,
            appointment.Reason, appointment.Notes, appointment.AppointmentType.ToString(),
            appointment.ConfirmedAt, appointment.CompletedAt, appointment.CancelledAt,
            appointment.CancelReason, appointment.RecurringRule, appointment.RecurringGroupId,
            appointment.CreatedAt, UpdatedAtOf(appointment), appointment.DeletedAt,
            new BookingClinicNameResponse(clinic.Name),
            new BookingCreatedPhysicianResponse(
                new BookingCreatedPhysicianUserResponse(physicianDisplayName)),
            subprofile is null
                ? null
                : new BookingCreatedSubprofileResponse(subprofile.Name, subprofile.Relation));

    public static BookingWalkInResponse ToWalkInResponse(Appointment appointment, ClinicSummary clinic)
        => new(
            appointment.Id, appointment.ClinicId, appointment.PhysicianId, appointment.PatientUserId,
            appointment.SubprofileId, appointment.ClinicPatientId, appointment.Status.ToString(),
            appointment.AppointmentDate, appointment.StartTime, appointment.EndTime,
            appointment.Reason, appointment.Notes, appointment.AppointmentType.ToString(),
            appointment.ConfirmedAt, appointment.CompletedAt, appointment.CancelledAt,
            appointment.CancelReason, appointment.RecurringRule, appointment.RecurringGroupId,
            appointment.CreatedAt, UpdatedAtOf(appointment), appointment.DeletedAt,
            new BookingClinicNameResponse(clinic.Name));

    public static BookingRecurringAppointmentResponse ToRecurringRow(Appointment appointment)
        => new(
            appointment.Id, appointment.ClinicId, appointment.PhysicianId, appointment.PatientUserId,
            appointment.SubprofileId, appointment.ClinicPatientId, appointment.Status.ToString(),
            appointment.AppointmentDate, appointment.StartTime, appointment.EndTime,
            appointment.Reason, appointment.Notes, appointment.AppointmentType.ToString(),
            appointment.ConfirmedAt, appointment.CompletedAt, appointment.CancelledAt,
            appointment.CancelReason, appointment.RecurringRule, appointment.RecurringGroupId,
            appointment.CreatedAt, UpdatedAtOf(appointment), appointment.DeletedAt);

    /// <summary>
    /// Prisma's <c>@updatedAt</c> is written on INSERT as well as on UPDATE, so a freshly created
    /// row reports a timestamp equal to <c>createdAt</c>. <c>MutableEntity.UpdatedAt</c> is left
    /// null until the first modification, which would put <c>"updatedAt": null</c> on the wire for
    /// every one of these three 201s.
    /// </summary>
    private static DateTime UpdatedAtOf(Appointment appointment)
        => appointment.UpdatedAt ?? appointment.CreatedAt;
}
