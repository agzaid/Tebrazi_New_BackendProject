using System.Text.Json.Nodes;
using Tebrazi.Appointments.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Appointments.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  The five read endpoints of appointments.js, one response family each.
//
//  There is deliberately NO shared appointment row DTO. The three list endpoints project the
//  same 22 scalars but three DIFFERENT relation sets and three DIFFERENT patient shapes:
//
//    GET /        clinic { id, name, address, phone }  physician { specialty, user }  patientUser { id, displayName, phone }
//    GET /today   clinic { name }                      — no physician key —          patientUser { id, displayName }
//    GET /queue   — no clinic key —                    physician (FULL profile)      patientUser { displayName, phone }
//
//  and GET / drops the patientUser KEY ENTIRELY on the patient branch rather than nulling it
//  (appointments.js:414 vs :418). Merging any two of these changes the bytes the client parses.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// <c>subprofile: { name, relation }</c>. The ONE projection the three list endpoints genuinely
/// share — appointments.js:398, :529 and :1243 all select exactly these two columns — so it is
/// declared once. <c>relation</c> stays a plain string carrying the <c>SubprofileRelation</c>
/// enum (<c>SELF|SPOUSE|CHILD|PARENT|SIBLING|OTHER</c>).
/// </summary>
public sealed record ApptReadSubprofile(string Name, string Relation);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>The list route's clinic projection — four keys (appointments.js:394).</summary>
public sealed record ApptReadListClinic(string Id, string Name, string? Address, string? Phone);

/// <summary>
/// <c>physician: { specialty, user: { displayName } }</c> (appointments.js:395-397). There is NO
/// <c>id</c> and no <c>userId</c> here, so the client cannot correlate this object back to a
/// physician — and <c>POST /</c>'s <c>physician</c>, under the same key name, carries the nested
/// user WITHOUT <c>specialty</c>. Two different DTOs, one relation name.
/// </summary>
public sealed record ApptReadListPhysician(string Specialty, ApptReadListPhysicianUser User);

public sealed record ApptReadListPhysicianUser(string DisplayName);

/// <summary>
/// <c>patientUser</c> on <c>GET /</c> — three keys including <c>phone</c>
/// (appointments.js:410). <c>GET /today</c> has no phone and <c>GET /queue</c> has no id.
/// </summary>
public sealed record ApptReadListPatientUser(string Id, string DisplayName, string? Phone);

/// <summary>
/// One row of the ENRICHED branch of <c>GET /api/appointments</c>: the 22 Appointment scalars in
/// Prisma schema order, then <c>clinic</c>, <c>physician</c>, <c>subprofile</c>, then
/// <c>patientUser</c> appended last by the spread at appointments.js:414.
///
/// <para>The enrichment runs when <c>userType === 'PHYSICIAN' || where.clinicId</c>, so a plain
/// PATIENT who merely passes <c>?clinicId=</c> also gets this shape — with their own name and
/// phone echoed back.</para>
///
/// <para><c>deletedAt</c> IS emitted and soft-deleted rows are NOT filtered: appointments.js
/// never mentions <c>deletedAt</c>, and there is no Prisma soft-delete middleware.</para>
/// </summary>
public sealed record ApptReadListItem(
    string Id,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    DateTime AppointmentDate,
    string StartTime,                       // "09:00" — a STRING column, never a time type
    string EndTime,
    string? Reason,
    string? Notes,                          // may still carry the WALK_IN / CHECKIN: markers
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
    ApptReadListClinic? Clinic,
    ApptReadListPhysician? Physician,
    ApptReadSubprofile? Subprofile,
    ApptReadListPatientUser? PatientUser);

/// <summary>
/// One row of the PLAIN-PATIENT branch of <c>GET /api/appointments</c> (appointments.js:418).
/// Identical to <see cref="ApptReadListItem"/> except that the <c>patientUser</c> key is ABSENT
/// ENTIRELY rather than null — a client testing <c>'patientUser' in appointment</c> can tell the
/// difference, which is why this is a separate record instead of a conditional property.
/// </summary>
public sealed record ApptReadListPatientItem(
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
    ApptReadListClinic? Clinic,
    ApptReadListPhysician? Physician,
    ApptReadSubprofile? Subprofile);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/today
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// <c>clinic: { name }</c> — deliberately narrower than <c>GET /</c>'s four-key clinic
/// (appointments.js:528). No id, no address, no phone.
/// </summary>
public sealed record ApptReadTodayClinic(string Name);

/// <summary>
/// <c>patientUser: { id, displayName }</c> with NO phone (appointments.js:539).
/// </summary>
public sealed record ApptReadTodayPatientUser(string Id, string DisplayName);

/// <summary>
/// One row of <c>GET /api/appointments/today</c>: the 22 scalars, then <c>clinic</c>,
/// <c>subprofile</c>, then <c>patientUser</c> from the spread at appointments.js:543.
///
/// <para>There is NO <c>physician</c> key on this endpoint at all, and <c>status</c> can only be
/// PENDING or CONFIRMED because the query filters on those two — a COMPLETED or NO_SHOW
/// appointment vanishes from today's list the moment it is acted on.</para>
/// </summary>
public sealed record ApptReadTodayItem(
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
    ApptReadTodayClinic? Clinic,
    ApptReadSubprofile? Subprofile,
    ApptReadTodayPatientUser? PatientUser);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/queue
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The queue's <c>physician</c> object. appointments.js:1244 uses a bare Prisma
/// <c>include</c> with no <c>select</c>, so EVERY PhysicianProfile scalar ships to every
/// receptionist who can read the queue — <c>licenseNumber</c>, <c>bio</c> and the physician's
/// private <c>scratchpadNotes</c> included — plus a nested <c>user: { displayName }</c>.
///
/// <para>That is a data-exposure defect, and it is reproduced rather than trimmed: dropping the
/// keys would change the payload. It is flagged in docs/PORT-STATUS.md instead.</para>
/// </summary>
public sealed record ApptReadQueuePhysician(
    string Id,
    string UserId,
    string LicenseNumber,
    string Specialty,
    string? Qualifications,
    string? Bio,
    string? ScratchpadNotes,
    int? YearsOfExperience,
    bool Verified,
    DateTime? VerifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    ApptReadQueuePhysicianUser User);

public sealed record ApptReadQueuePhysicianUser(string DisplayName);

/// <summary>
/// <c>patientUser: { displayName, phone }</c> — no <c>id</c> (appointments.js:1253). It comes
/// from a raw <c>findUnique</c> with no <c>|| null</c> fallback, so a dangling
/// <c>patientUserId</c> yields <c>null</c> rather than an empty object; <c>appointments</c> has
/// no foreign key on that column, so dangling ids really do occur.
/// </summary>
public sealed record ApptReadQueuePatientUser(string DisplayName, string? Phone);

/// <summary>
/// One queue row: the 22 scalars, then <c>subprofile</c> and <c>physician</c> (include order),
/// then <c>patientUser</c>, <c>checkedIn</c>, <c>checkedInAt</c>, <c>room</c> from the first
/// spread (appointments.js:1270), then the four visit-overlay keys from the second
/// (appointments.js:1291-1298).
///
/// <para>There is NO <c>clinic</c> key even though <c>?clinicId</c> is required, and CANCELLED
/// never appears in <c>status</c> because the query excludes it.</para>
///
/// <para><c>checkedInAt</c> and <c>room</c> are raw JSON values lifted verbatim out of the
/// <c>CHECKIN:</c> payload buried in <c>notes</c> — never re-serialized, never validated, and
/// never coerced, so a numeric room stays a number.</para>
///
/// <para>The four <c>visit*</c> keys are RENAMES of Visit columns: <c>visitFollowUp</c> is
/// <c>followUpDate</c>, <c>visitFollowUpNotes</c> is <c>followUpNotes</c>,
/// <c>visitCompletedAt</c> is <c>completedAt</c>. The Visit's own <c>id</c> is read from the
/// database and never emitted. <c>visitStatus</c> is a STRING, not a two-value enum: the visit
/// query applies no status filter, so IN_PROGRESS, COMPLETED, CANCELLED and ARCHIVED are all
/// reachable — the code comment at appointments.js:1294 claiming otherwise is wrong.</para>
/// </summary>
public sealed record ApptReadQueueItem(
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
    ApptReadSubprofile? Subprofile,
    ApptReadQueuePhysician? Physician,
    ApptReadQueuePatientUser? PatientUser,
    bool CheckedIn,
    JsonNode? CheckedInAt,
    JsonNode? Room,
    string? VisitStatus,
    DateTime? VisitFollowUp,
    string? VisitFollowUpNotes,
    DateTime? VisitCompletedAt);

/// <summary>
/// The queue counters (appointments.js:1302-1308). THE BUCKETS OVERLAP AND DO NOT RECONCILE:
/// <c>pending</c> is <c>!checkedIn &amp;&amp; status !== 'COMPLETED'</c>, so every un-checked-in
/// NO_SHOW is counted in both <c>pending</c> and <c>noShow</c>, and <c>total</c> is not
/// <c>arrived + pending + completed</c>. <c>arrived</c> counts the notes-derived flag, not the
/// status, so a CONFIRMED appointment with no CHECKIN marker is not "arrived". Fixing the
/// arithmetic would change numbers the client renders.
/// </summary>
public sealed record ApptReadQueueSummary(
    int Total, int Arrived, int Pending, int Completed, int NoShow);

/// <summary>
/// <c>GET /api/appointments/queue</c> answers an ENVELOPE, unlike <c>GET /</c> and
/// <c>GET /today</c> which answer bare arrays. <c>queue</c> is <c>[]</c> and every counter is 0
/// when the clinic has nothing today.
/// </summary>
public sealed record ApptReadQueueResponse(
    IReadOnlyList<ApptReadQueueItem> Queue, ApptReadQueueSummary Summary);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/available
// ═════════════════════════════════════════════════════════════════════════════

/// <summary><c>clinic: { name }</c> (appointments.js:303).</summary>
public sealed record ApptReadAvailableClinic(string Name);

/// <summary>
/// One row of <c>GET /api/appointments/available</c>: every TimeSlot scalar in schema order,
/// then <c>clinic</c>, then <c>isBooked</c> appended by the spread (appointments.js:331-334).
///
/// <para>These are the RECURRING WEEKLY TEMPLATE rows, returned as they are stored — the
/// endpoint does not expand a window into concrete appointment-sized slots, and there is no
/// per-date slot storage, no holiday handling and no difference between a past and a future
/// date. <c>TimeSlot</c> has no <c>updatedAt</c> column, so none is emitted.</para>
///
/// <para><c>physicianId</c> is the PhysicianProfile id, not the <c>?physicianUserId</c> that was
/// queried. <c>isActive</c> is always true (it is in the <c>where</c>) and is still emitted.
/// Booked slots are RETURNED with <c>isBooked: true</c> rather than filtered out; the client
/// disables them.</para>
/// </summary>
public sealed record ApptReadAvailableSlot(
    string Id,
    string ClinicId,
    string PhysicianId,
    int DayOfWeek,                          // 0 = Sunday .. 6 = Saturday (JS Date.getDay())
    string StartTime,
    string EndTime,
    int SlotDuration,
    bool IsActive,
    DateTime CreatedAt,
    ApptReadAvailableClinic? Clinic,
    bool IsBooked);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/ai-optimize
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One entry of <c>stats.dayStats</c>. <c>day</c> is a hard-coded ENGLISH day name and
/// <c>dayIndex</c> is JavaScript's <c>getDay()</c> (0 = Sunday) — no localization, even though
/// the client is bilingual. Both rates are fractions rounded to 3 decimals.
/// </summary>
public sealed record ApptReadOptimizeDayStat(
    string Day, int DayIndex, int Total, double NoShowRate, double CancelRate);

/// <summary>
/// One entry of <c>stats.hourDistribution</c>. <c>hour</c> is built as
/// <c>`${startTime.split(':')[0]}:00`</c>, so an unpadded stored <c>"9:00"</c> yields the hour
/// <c>"9:00"</c> and not <c>"09:00"</c>.
/// </summary>
public sealed record ApptReadOptimizeHourCount(string Hour, int Count);

/// <summary>
/// The <c>stats</c> object, in the exact key order of the object literal at
/// appointments.js:1463-1482.
///
/// <para>Key-name traps, all reproduced: <c>noShows</c> is plural while <c>noShowRate</c> is
/// not; <c>cancelled</c> has two Ls while <c>cancelRate</c> has one and no <c>-led</c>;
/// <c>avgVisitDurationMin</c> carries the <c>Min</c> suffix while <c>avgSlotDuration</c> does
/// not.</para>
///
/// <para>Every rate is a FRACTION in 0..1 rounded to 3 decimals — never a percentage — except
/// <c>avgDailyPatients</c>, which is rounded to ONE decimal, and <c>slotUtilization</c>, which
/// is rounded to 3 and then clamped by <c>Math.min(x, 1)</c>, so it saturates at exactly 1.
/// JavaScript's <c>parseFloat</c> drops trailing zeros, so 0.100 must serialize as
/// <c>0.1</c>, an exact 0 as <c>0</c> and a saturated utilization as <c>1</c>: these are
/// <c>double</c> for that reason, since System.Text.Json writes the shortest round-trippable
/// form.</para>
/// </summary>
public sealed record ApptReadOptimizeStats(
    int TotalAppointments,
    int Completed,
    int NoShows,
    int Cancelled,
    double NoShowRate,
    double CancelRate,
    double CompletionRate,
    int? AvgVisitDurationMin,               // null when no usable COMPLETED visit in the window
    int AvgSlotDuration,                    // literal 30 when the physician has no active slots
    string PeakHour,                        // `${hourKey}:00`, "09:00" when there is no data
    string BusiestDay,                      // an English day name, or the literal "N/A"
    double AvgDailyPatients,
    double SlotUtilization,
    IReadOnlyList<ApptReadOptimizeDayStat> DayStats,
    IReadOnlyList<ApptReadOptimizeHourCount> HourDistribution,
    // A SPARSE object map keyed only by the AppointmentType values that actually occur — an
    // absent type has NO key, it is not 0. JsonObject and not a Dictionary so the insertion
    // order (first appearance while scanning the window) is guaranteed on the wire.
    JsonObject TypeBreakdown,
    int TotalActiveSlotsPerWeek,
    int AnalyzedDays);

/// <summary>
/// One sanitized AI insight. The CODE limits are the contract, not the prompt's: <c>title</c> is
/// truncated at 120 characters and <c>description</c> at 500 (the prompt asks for 80 and 2-3
/// sentences), an unrecognised <c>type</c> is coerced to <c>THROUGHPUT</c> and an unrecognised
/// <c>impact</c> to <c>MODERATE</c>, and at most 6 insights survive (the prompt asks for 3-5).
/// </summary>
public sealed record ApptReadOptimizeInsight(
    string Type, string Title, string Description, string Impact);

/// <summary>
/// The normal <c>GET /api/appointments/ai-optimize</c> envelope. It has <c>aiProvider</c> and NO
/// <c>message</c> key — the mirror image of <see cref="ApptReadOptimizeNoDataResponse"/>.
///
/// <para><c>insights</c> is always an array, never null: any AI failure degrades to <c>[]</c>
/// with the statistics intact and a 200. <c>aiProvider</c> is null ONLY when the gateway call
/// itself threw; when the call succeeded and only its JSON failed to parse, the real provider
/// string is reported beside an empty <c>insights</c>, because it is assigned before the parse
/// (appointments.js:1522).</para>
/// </summary>
public sealed record ApptReadOptimizeResponse(
    ApptReadOptimizeStats Stats,
    IReadOnlyList<ApptReadOptimizeInsight> Insights,
    string? AiProvider);

/// <summary>
/// The under-five-appointments envelope (appointments.js:1363-1367), a 200 and not an error.
/// <c>stats</c> is literally null, and the <c>aiProvider</c> key is ABSENT — not null — which is
/// why this is a separate record.
/// </summary>
public sealed record ApptReadOptimizeNoDataResponse(
    object? Stats,
    IReadOnlyList<ApptReadOptimizeInsight> Insights,
    string Message);

// ═════════════════════════════════════════════════════════════════════════════
//  Mapping
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Projects appointments, time slots and port summaries onto the read responses. Every method
/// takes its relations already resolved — the handlers batch those lookups once per response, and
/// nothing in here may issue a query.
/// </summary>
public static class AppointmentReadMapper
{
    /// <summary>
    /// Prisma's <c>@updatedAt</c> is written on every create, so <c>updatedAt</c> is never null
    /// on the wire. The column is nullable in this port, so it falls back to <c>createdAt</c>.
    /// </summary>
    private static DateTime Updated(Appointment a) => a.UpdatedAt ?? a.CreatedAt;

    public static ApptReadListClinic? ToListClinic(ClinicSummary? clinic)
        => clinic is null
            ? null
            : new ApptReadListClinic(clinic.Id, clinic.Name, clinic.Address, clinic.Phone);

    public static ApptReadListPhysician? ToListPhysician(PhysicianSummary? physician)
        => physician is null
            ? null
            : new ApptReadListPhysician(
                physician.Specialty, new ApptReadListPhysicianUser(physician.DisplayName));

    public static ApptReadSubprofile? ToSubprofile(SubprofileSummary? subprofile)
        => subprofile is null
            ? null
            : new ApptReadSubprofile(subprofile.Name, subprofile.Relation);

    public static ApptReadListPatientUser? ToListPatientUser(UserSummary? user)
        => user is null
            ? null
            : new ApptReadListPatientUser(user.Id, user.DisplayName, user.Phone);

    public static ApptReadTodayClinic? ToTodayClinic(ClinicSummary? clinic)
        => clinic is null ? null : new ApptReadTodayClinic(clinic.Name);

    public static ApptReadTodayPatientUser? ToTodayPatientUser(UserSummary? user)
        => user is null ? null : new ApptReadTodayPatientUser(user.Id, user.DisplayName);

    public static ApptReadQueuePatientUser? ToQueuePatientUser(UserSummary? user)
        => user is null ? null : new ApptReadQueuePatientUser(user.DisplayName, user.Phone);

    public static ApptReadQueuePhysician? ToQueuePhysician(PhysicianDetail? physician)
        => physician is null
            ? null
            : new ApptReadQueuePhysician(
                physician.Id,
                physician.UserId,
                physician.LicenseNumber,
                physician.Specialty,
                physician.Qualifications,
                physician.Bio,
                physician.ScratchpadNotes,
                physician.YearsOfExperience,
                physician.Verified,
                physician.VerifiedAt,
                physician.CreatedAt,
                physician.UpdatedAt ?? physician.CreatedAt,
                new ApptReadQueuePhysicianUser(physician.DisplayName));

    public static ApptReadListItem ToListItem(
        Appointment a,
        ClinicSummary? clinic,
        PhysicianSummary? physician,
        SubprofileSummary? subprofile,
        UserSummary? patientUser)
        => new(a.Id, a.ClinicId, a.PhysicianId, a.PatientUserId, a.SubprofileId, a.ClinicPatientId,
               a.Status.ToString(), a.AppointmentDate, a.StartTime, a.EndTime, a.Reason, a.Notes,
               a.AppointmentType.ToString(), a.ConfirmedAt, a.CompletedAt, a.CancelledAt,
               a.CancelReason, a.RecurringRule, a.RecurringGroupId, a.CreatedAt, Updated(a),
               a.DeletedAt,
               ToListClinic(clinic), ToListPhysician(physician), ToSubprofile(subprofile),
               ToListPatientUser(patientUser));

    public static ApptReadListPatientItem ToListPatientItem(
        Appointment a,
        ClinicSummary? clinic,
        PhysicianSummary? physician,
        SubprofileSummary? subprofile)
        => new(a.Id, a.ClinicId, a.PhysicianId, a.PatientUserId, a.SubprofileId, a.ClinicPatientId,
               a.Status.ToString(), a.AppointmentDate, a.StartTime, a.EndTime, a.Reason, a.Notes,
               a.AppointmentType.ToString(), a.ConfirmedAt, a.CompletedAt, a.CancelledAt,
               a.CancelReason, a.RecurringRule, a.RecurringGroupId, a.CreatedAt, Updated(a),
               a.DeletedAt,
               ToListClinic(clinic), ToListPhysician(physician), ToSubprofile(subprofile));

    public static ApptReadTodayItem ToTodayItem(
        Appointment a,
        ClinicSummary? clinic,
        SubprofileSummary? subprofile,
        UserSummary? patientUser)
        => new(a.Id, a.ClinicId, a.PhysicianId, a.PatientUserId, a.SubprofileId, a.ClinicPatientId,
               a.Status.ToString(), a.AppointmentDate, a.StartTime, a.EndTime, a.Reason, a.Notes,
               a.AppointmentType.ToString(), a.ConfirmedAt, a.CompletedAt, a.CancelledAt,
               a.CancelReason, a.RecurringRule, a.RecurringGroupId, a.CreatedAt, Updated(a),
               a.DeletedAt,
               ToTodayClinic(clinic), ToSubprofile(subprofile), ToTodayPatientUser(patientUser));

    public static ApptReadQueueItem ToQueueItem(
        Appointment a,
        SubprofileSummary? subprofile,
        PhysicianDetail? physician,
        UserSummary? patientUser,
        bool checkedIn,
        JsonNode? checkedInAt,
        JsonNode? room,
        VisitQueueRow? visit)
        => new(a.Id, a.ClinicId, a.PhysicianId, a.PatientUserId, a.SubprofileId, a.ClinicPatientId,
               a.Status.ToString(), a.AppointmentDate, a.StartTime, a.EndTime, a.Reason, a.Notes,
               a.AppointmentType.ToString(), a.ConfirmedAt, a.CompletedAt, a.CancelledAt,
               a.CancelReason, a.RecurringRule, a.RecurringGroupId, a.CreatedAt, Updated(a),
               a.DeletedAt,
               ToSubprofile(subprofile), ToQueuePhysician(physician),
               ToQueuePatientUser(patientUser),
               checkedIn, checkedInAt, room,
               visit?.Status, visit?.FollowUpDate, visit?.FollowUpNotes, visit?.CompletedAt);

    public static ApptReadAvailableSlot ToAvailableSlot(
        TimeSlot slot, ClinicSummary? clinic, bool isBooked)
        => new(slot.Id, slot.ClinicId, slot.PhysicianId, slot.DayOfWeek, slot.StartTime,
               slot.EndTime, slot.SlotDuration, slot.IsActive, slot.CreatedAt,
               clinic is null ? null : new ApptReadAvailableClinic(clinic.Name),
               isBooked);
}
