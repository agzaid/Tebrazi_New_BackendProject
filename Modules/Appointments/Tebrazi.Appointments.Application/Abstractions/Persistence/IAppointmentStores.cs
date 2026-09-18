using Tebrazi.Appointments.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Persistence;

namespace Tebrazi.Appointments.Application.Abstractions.Persistence;

/// <summary>The Appointments module's unit of work. Handlers inject THIS, never the concrete context.</summary>
public interface IAppointmentsDbContext : IDbContext;

/// <summary>
/// The filter behind <c>GET /api/appointments</c>. A null member is not applied, matching the
/// way the Node handler assembles its <c>where</c> key by key.
/// </summary>
public sealed record AppointmentFilter(
    string? ClinicId = null,
    string? PhysicianId = null,
    string? PatientUserId = null,
    string? ClinicPatientId = null,
    AppointmentStatus? Status = null,
    DateTime? Date = null,
    DateTime? From = null,
    DateTime? To = null);

/// <summary>The two fields the availability check reads off a booked appointment.</summary>
public sealed record BookedSlot(string StartTime, string EndTime);

public interface IAppointmentStore
{
    /// <summary>Tracked, for a handler that is about to modify the appointment.</summary>
    Task<Appointment?> GetForUpdateAsync(string id, CancellationToken ct = default);

    Task<Appointment?> GetAsync(string id, CancellationToken ct = default);

    Task<(IReadOnlyList<Appointment> Items, int TotalCount)> PageAsync(
        AppointmentFilter filter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// The UNPAGINATED list behind <c>GET /api/appointments</c>, ordered by appointment date and
    /// truncated to <paramref name="take"/> rows.
    ///
    /// That route has no pagination at all: it is a bare <c>findMany</c> with
    /// <c>orderBy: { appointmentDate: 'asc' }</c> and <c>take: 200</c>
    /// (appointments.js:400-401), and it returns a naked JSON ARRAY with no total and no
    /// metadata. <see cref="PageAsync"/> cannot express it — that one caps the page at 100 and
    /// adds <c>startTime</c> as a second sort key, so it would both truncate differently and
    /// reorder same-day rows.
    ///
    /// Rows sharing an <c>appointment_date</c> come back in whatever order the database
    /// chooses, exactly as in Node: no tie-breaker is applied, deliberately, because adding one
    /// would make the .NET order deterministic where Node's is not and mask the difference.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ListAsync(
        AppointmentFilter filter, int take, CancellationToken ct = default);

    /// <summary>
    /// Every appointment on one calendar day, ordered by start time. The date is matched over
    /// the whole day rather than for equality: a stored value carrying a time component would
    /// never match midnight.
    ///
    /// <paramref name="statuses"/> narrows the result and is REQUIRED by both day-scoped
    /// endpoints, which use different sets: <c>GET /api/appointments/today</c> asks for
    /// PENDING and CONFIRMED only (appointments.js:525), while
    /// <c>GET /api/appointments/queue</c> asks for PENDING, CONFIRMED, COMPLETED and NO_SHOW —
    /// everything except CANCELLED (appointments.js:1240). Pass null for no status filter; no
    /// endpoint currently does.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ListForDayAsync(
        string? clinicId,
        string? physicianId,
        DateTime date,
        IReadOnlyCollection<AppointmentStatus>? statuses = null,
        CancellationToken ct = default);

    /// <summary>
    /// The start/end times already booked in a physician's day, for the availability check.
    ///
    /// Only PENDING and CONFIRMED hold a slot. COMPLETED does NOT — the Node query is
    /// <c>status: { in: ['PENDING','CONFIRMED'] }</c> (appointments.js:314), and including
    /// COMPLETED would show yesterday's finished slots as unavailable.
    /// </summary>
    Task<IReadOnlyList<BookedSlot>> ListBookedAsync(
        string physicianId, DateTime date, CancellationToken ct = default);

    /// <summary>
    /// The double-booking guard behind the 409 on <c>POST /api/appointments</c>.
    ///
    /// It is an EXACT <c>start_time</c> string match, not an interval overlap: the Node query
    /// (appointments.js:450-458) filters on <c>startTime</c> equality alone. So an 09:00-10:00
    /// booking does NOT block a new 09:30-10:00 one, and a port that checks overlap would reject
    /// bookings the Node backend accepts.
    ///
    /// It is also NOT scoped to the clinic, so a booking at one of the physician's clinics
    /// blocks the same start time at another. Both are the contract.
    /// </summary>
    Task<bool> HasBookingAtAsync(
        string physicianId, DateTime date, string startTime, CancellationToken ct = default);

    /// <summary>Every instance of one recurring series.</summary>
    Task<IReadOnlyList<Appointment>> ListByRecurringGroupAsync(
        string recurringGroupId, CancellationToken ct = default);

    /// <summary>
    /// A physician's appointments from a date onward, for the schedule optimiser's 90-day
    /// window. No status filter — cancellations and no-shows are exactly what it analyses.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ListForPhysicianSinceAsync(
        string physicianId, DateTime since, CancellationToken ct = default);

    void Add(Appointment appointment);
    void AddRange(IEnumerable<Appointment> appointments);

    /// <summary>
    /// A HARD delete. <c>DELETE /api/appointments/{id}</c> calls
    /// <c>prisma.appointment.delete</c> (appointments.js:948) — nothing in the Node backend ever
    /// writes <c>appointments.deleted_at</c>. Use this, not <c>Appointment.SoftDelete()</c>.
    /// </summary>
    void Remove(Appointment appointment);
}

public interface ITimeSlotStore
{
    Task<TimeSlot?> GetForUpdateAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// A physician's ACTIVE weekly template, ordered by weekday then start time. Backs
    /// <c>GET /api/appointments/slots</c>, which filters <c>isActive: true</c>.
    /// </summary>
    Task<IReadOnlyList<TimeSlot>> ListActiveAsync(
        string physicianId, string? clinicId = null, CancellationToken ct = default);

    /// <summary>
    /// The active windows for one weekday, which the availability query expands. The optional
    /// clinic narrows it, as <c>?clinicId=</c> does on <c>GET /api/appointments/available</c>.
    /// </summary>
    Task<IReadOnlyList<TimeSlot>> ListActiveForDayOfWeekAsync(
        string physicianId, int dayOfWeek, string? clinicId = null, CancellationToken ct = default);

    /// <summary>
    /// Rows for the destructive slot paths, with NO <c>IsActive</c> filter — the
    /// <c>deleteMany</c> in <c>POST /slots/bulk</c> and <c>POST /slots/sync-from-hours</c>
    /// removes deactivated rows too. Pass <paramref name="dayOfWeek"/> for the bulk path, which
    /// scopes its delete to one weekday; omit it for sync-from-hours, which clears every day.
    /// </summary>
    Task<IReadOnlyList<TimeSlot>> ListAllAsync(
        string physicianId, string clinicId, int? dayOfWeek = null, CancellationToken ct = default);

    /// <summary>
    /// The <c>deleteMany</c> itself, issued as ONE statement that COMMITS ON ITS OWN, and the
    /// method the two destructive slot routes should use.
    ///
    /// <para>Committing immediately is the point, not a side effect. Both routes delete FIRST
    /// and can then fail — <c>POST /slots/bulk</c> answers
    /// <c>400 "Time range too short for slot duration"</c> after the delete has already run
    /// (appointments.js:144 then :170), and either route's <c>createMany</c> can throw — and
    /// Node LEAVES THE DELETION IN PLACE, so the physician is left with no slots at all. Wrapping
    /// the delete and the create in <c>ExecuteInTransactionAsync</c> would roll the deletion back
    /// and CHANGE that observable behaviour: the client would keep its old slots where Node
    /// loses them. Do not do it.</para>
    ///
    /// <para>Scope, and no <c>IsActive</c> filter: <paramref name="dayOfWeek"/> narrows to one
    /// weekday for <c>POST /slots/bulk</c> (appointments.js:145); omit it for
    /// <c>POST /slots/sync-from-hours</c>, which wipes EVERY weekday at the clinic
    /// (appointments.js:201-202) — so a schedule posting Monday alone erases Tuesday through
    /// Sunday. Deactivated rows go too, because the Node filter carries no <c>isActive</c>.</para>
    /// </summary>
    /// <returns>How many rows were deleted.</returns>
    Task<int> DeleteAllAsync(
        string physicianId, string clinicId, int? dayOfWeek = null, CancellationToken ct = default);

    void Add(TimeSlot slot);
    void AddRange(IEnumerable<TimeSlot> slots);
    void Remove(TimeSlot slot);
    void RemoveRange(IEnumerable<TimeSlot> slots);
}
