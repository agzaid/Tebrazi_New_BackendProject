using Tebrazi.Appointments.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Appointments.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  The five time-slot endpoints of server/src/routes/appointments.js:
//
//    GET    /api/appointments/slots                  (appointments.js:28-50)
//    POST   /api/appointments/slots                  (appointments.js:57-90)
//    DELETE /api/appointments/slots/{id}             (appointments.js:96-115)
//    POST   /api/appointments/slots/bulk             (appointments.js:126-178)
//    POST   /api/appointments/slots/sync-from-hours  (appointments.js:183-248)
//
//  ROUTE ORDER MATTERS: /slots/bulk and /slots/sync-from-hours are LITERAL segments that must be
//  mapped ahead of DELETE /slots/{id}, and POST /slots must not swallow either of them.
//
//  DELETE /slots/{id} is NOT represented here: it answers the shared
//  <see cref="MessageResponse"/> ({"message":"Slot removed"}), which
//  CommonAppointmentResponses.cs already publishes for exactly that reason.
//
//  The two generation routes ship a byte-identical body and are STILL given a record each,
//  because their STATUS CODES differ — bulk is 201, sync-from-hours is 200 (appointments.js:171
//  vs :246) — and a shared type would invite a port to unify the two routes. See each record.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The <c>clinic</c> relation on a <c>GET /api/appointments/slots</c> row: the Prisma
/// <c>include: { clinic: { select: { name: true } } }</c> (appointments.js:40), so EXACTLY one
/// key. No id, no address, no phone — the client reads <c>slot.clinic.name</c> and the key must
/// not be flattened to <c>clinicName</c>.
/// </summary>
public sealed record SlotListClinic(string Name);

/// <summary>
/// One row of <c>GET /api/appointments/slots</c> — the nine <c>TimeSlot</c> scalars in Prisma
/// declaration order (schema.prisma:1195-1204), then the included <c>clinic</c> relation last.
/// The route answers a BARE JSON ARRAY of these, never an envelope.
/// </summary>
/// <param name="Id">The slot's uuid.</param>
/// <param name="ClinicId">Required FK; the row cannot exist without a clinic.</param>
/// <param name="PhysicianId">The physician PROFILE id, not a user id.</param>
/// <param name="DayOfWeek">
/// 0 = Sunday through 6 = Saturday, a JSON NUMBER. Never validated on write, so rows outside
/// 0..6 can exist in Node's data and are returned as they are found.
/// </param>
/// <param name="StartTime">"HH:mm" as a STRING. Sorted lexicographically, not chronologically.</param>
/// <param name="EndTime">"HH:mm" as a STRING.</param>
/// <param name="SlotDuration">Minutes, an Int (column default 30).</param>
/// <param name="IsActive">
/// Always <c>true</c> on this route — the query hard-filters <c>isActive: true</c>, so a slot
/// soft-deleted by <c>DELETE /slots/{id}</c> is invisible here forever — but the KEY IS PRESENT.
/// </param>
/// <param name="CreatedAt">Row creation timestamp. <c>TimeSlot</c> has no <c>updatedAt</c> column.</param>
/// <param name="Clinic">
/// Never null in practice: <c>clinicId</c> is a required FK with <c>onDelete: Cascade</c>, so
/// Prisma's include always resolves. Nullable here only so a clinic row the Clinics module
/// cannot return degrades to <c>"clinic": null</c> rather than a fabricated name.
/// </param>
public sealed record SlotListItem(
    string Id,
    string ClinicId,
    string PhysicianId,
    int DayOfWeek,
    string StartTime,
    string EndTime,
    int SlotDuration,
    bool IsActive,
    DateTime CreatedAt,
    SlotListClinic? Clinic);

/// <summary>
/// The <c>201</c> body of <c>POST /api/appointments/slots/bulk</c> (appointments.js:171) —
/// exactly two keys, in this order, and NOT the created rows: the client must refetch
/// <c>GET /api/appointments/slots</c>.
///
/// <para><c>Message</c> is the template literal <c>`${created.count} slots created`</c>, so the
/// number appears TWICE and always agrees with <c>Count</c>, and the word is ALWAYS PLURAL — a
/// single slot reads "1 slots created". Do not pluralize.</para>
///
/// <para>Byte-identical to <see cref="SlotSyncFromHoursResponse"/> and to
/// <see cref="SlotCreatedResponse"/> (appointments.js:86), but each route keeps its own type so
/// no port is tempted to unify the three: this one and <c>POST /slots</c> are 201,
/// <c>sync-from-hours</c> is 200.</para>
/// </summary>
public sealed record SlotBulkCreatedResponse(int Count, string Message);

/// <summary>
/// The <c>201</c> body of <c>POST /api/appointments/slots</c> (appointments.js:86) — the same two
/// keys and the same always-plural <c>`${created.count} slots created`</c> template as
/// <see cref="SlotBulkCreatedResponse"/>, and NOT the created rows.
///
/// <para><c>Count</c> is the number of rows in the posted <c>slots</c> array, because this route
/// generates nothing: unlike its two siblings it has NO deleteMany and NO slot-walking loop, so
/// it APPENDS and can create exact duplicates of rows that already exist.</para>
/// </summary>
public sealed record SlotCreatedResponse(int Count, string Message);

/// <summary>
/// The <c>200</c> body of <c>POST /api/appointments/slots/sync-from-hours</c>
/// (appointments.js:246). The same two keys as <see cref="SlotBulkCreatedResponse"/>, with the
/// same always-plural message, but the sibling route returns 201 and this one returns 200 —
/// preserve the difference.
///
/// <para><c>Count</c> is legitimately <c>0</c>: an empty <c>schedule</c> array, or one whose
/// every entry was skipped as malformed, still succeeds with
/// <c>{"count":0,"message":"0 slots created"}</c> after the clinic's whole slot grid has been
/// wiped. Neither the created rows nor the updated clinic is returned.</para>
/// </summary>
public sealed record SlotSyncFromHoursResponse(int Count, string Message);

/// <summary>Projection for the one slot endpoint that returns rows.</summary>
public static class SlotResponseMapper
{
    /// <summary>
    /// One <c>GET /api/appointments/slots</c> row. <paramref name="clinic"/> is the resolved
    /// clinic, or null when the Clinics module has no row for <c>slot.ClinicId</c> — which the
    /// cascade FK makes unreachable, but which must not invent a name if it happens.
    /// </summary>
    public static SlotListItem ToListItem(TimeSlot slot, ClinicSummary? clinic)
        => new(
            slot.Id,
            slot.ClinicId,
            slot.PhysicianId,
            slot.DayOfWeek,
            slot.StartTime,
            slot.EndTime,
            slot.SlotDuration,
            slot.IsActive,
            slot.CreatedAt,
            clinic is null ? null : new SlotListClinic(clinic.Name));
}
