using Microsoft.EntityFrameworkCore;
using Tebrazi.Appointments.Application.Abstractions.Persistence;
using Tebrazi.Appointments.Domain.Entities;

namespace Tebrazi.Appointments.Persistence.Stores;

/// <summary>
/// No method here filters <c>DeletedAt</c>. appointments.js never does — it does not even
/// import the soft-delete helper — so a filter would hide rows the Node backend returns.
/// </summary>
public sealed class AppointmentStore(AppointmentsDbContext context) : IAppointmentStore
{
    /// <summary>
    /// The statuses that hold a slot. COMPLETED is deliberately absent: appointments.js:314 and
    /// :456 both use <c>['PENDING','CONFIRMED']</c>.
    /// </summary>
    private static readonly AppointmentStatus[] SlotHolding =
    [
        AppointmentStatus.PENDING,
        AppointmentStatus.CONFIRMED
    ];

    public Task<Appointment?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<Appointment?> GetAsync(string id, CancellationToken ct = default)
        => context.Appointments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<(IReadOnlyList<Appointment> Items, int TotalCount)> PageAsync(
        AppointmentFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = Apply(context.Appointments.AsNoTracking(), filter)
            .OrderBy(a => a.AppointmentDate)
            .ThenBy(a => a.StartTime);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return (items, total);
    }

    /// <summary>
    /// <c>take: 200</c> with no total and no second sort key, matching appointments.js:400-401.
    /// </summary>
    public async Task<IReadOnlyList<Appointment>> ListAsync(
        AppointmentFilter filter, int take, CancellationToken ct = default)
    {
        if (take < 1) return [];

        return await Apply(context.Appointments.AsNoTracking(), filter)
            .OrderBy(a => a.AppointmentDate)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Appointment>> ListForDayAsync(
        string? clinicId,
        string? physicianId,
        DateTime date,
        IReadOnlyCollection<AppointmentStatus>? statuses = null,
        CancellationToken ct = default)
    {
        var query = WithinDay(context.Appointments.AsNoTracking(), date);

        if (clinicId is not null) query = query.Where(a => a.ClinicId == clinicId);
        if (physicianId is not null) query = query.Where(a => a.PhysicianId == physicianId);

        // Materialised to an array so EF translates it as a parameterised IN list. An empty set
        // is an explicit "match nothing", not "no filter" — a caller passing one has asked for
        // no statuses.
        if (statuses is not null)
        {
            var wanted = statuses as AppointmentStatus[] ?? [.. statuses];
            query = query.Where(a => wanted.Contains(a.Status));
        }

        return await query.OrderBy(a => a.StartTime).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BookedSlot>> ListBookedAsync(
        string physicianId, DateTime date, CancellationToken ct = default)
        => await WithinDay(context.Appointments.AsNoTracking(), date)
            .Where(a => a.PhysicianId == physicianId && SlotHolding.Contains(a.Status))
            .Select(a => new BookedSlot(a.StartTime, a.EndTime))
            .ToListAsync(ct);

    /// <summary>
    /// Exact start-time equality, physician-wide. See the port's note — this is NOT an overlap
    /// test and is NOT clinic-scoped.
    /// </summary>
    public Task<bool> HasBookingAtAsync(
        string physicianId, DateTime date, string startTime, CancellationToken ct = default)
        => WithinDay(context.Appointments.AsNoTracking(), date)
            .AnyAsync(a => a.PhysicianId == physicianId
                        && a.StartTime == startTime
                        && SlotHolding.Contains(a.Status), ct);

    public async Task<IReadOnlyList<Appointment>> ListByRecurringGroupAsync(
        string recurringGroupId, CancellationToken ct = default)
        => await context.Appointments
            .AsNoTracking()
            .Where(a => a.RecurringGroupId == recurringGroupId)
            .OrderBy(a => a.AppointmentDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Appointment>> ListForPhysicianSinceAsync(
        string physicianId, DateTime since, CancellationToken ct = default)
        => await context.Appointments
            .AsNoTracking()
            .Where(a => a.PhysicianId == physicianId && a.AppointmentDate >= since)
            .OrderBy(a => a.AppointmentDate)
            .ToListAsync(ct);

    public void Add(Appointment appointment) => context.Appointments.Add(appointment);

    public void AddRange(IEnumerable<Appointment> appointments)
        => context.Appointments.AddRange(appointments);

    public void Remove(Appointment appointment) => context.Appointments.Remove(appointment);

    private static IQueryable<Appointment> Apply(IQueryable<Appointment> query, AppointmentFilter f)
    {
        if (f.ClinicId is not null) query = query.Where(a => a.ClinicId == f.ClinicId);
        if (f.PhysicianId is not null) query = query.Where(a => a.PhysicianId == f.PhysicianId);
        if (f.PatientUserId is not null) query = query.Where(a => a.PatientUserId == f.PatientUserId);
        if (f.ClinicPatientId is not null) query = query.Where(a => a.ClinicPatientId == f.ClinicPatientId);
        if (f.Status.HasValue) query = query.Where(a => a.Status == f.Status.Value);

        if (f.Date.HasValue) query = WithinDay(query, f.Date.Value);
        if (f.From.HasValue) query = query.Where(a => a.AppointmentDate >= f.From.Value.Date);
        if (f.To.HasValue) query = query.Where(a => a.AppointmentDate < f.To.Value.Date.AddDays(1));

        return query;
    }

    /// <summary>
    /// Matches a whole calendar day rather than an exact timestamp. A stored appointment_date
    /// carrying a time component would never equal midnight, so equality would silently return
    /// nothing.
    /// </summary>
    private static IQueryable<Appointment> WithinDay(IQueryable<Appointment> query, DateTime date)
    {
        var start = date.Date;
        var end = start.AddDays(1);
        return query.Where(a => a.AppointmentDate >= start && a.AppointmentDate < end);
    }
}

public sealed class TimeSlotStore(AppointmentsDbContext context) : ITimeSlotStore
{
    public Task<TimeSlot?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.TimeSlots.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<TimeSlot>> ListActiveAsync(
        string physicianId, string? clinicId = null, CancellationToken ct = default)
    {
        var query = context.TimeSlots
            .AsNoTracking()
            .Where(s => s.PhysicianId == physicianId && s.IsActive);

        if (clinicId is not null) query = query.Where(s => s.ClinicId == clinicId);

        return await query
            .OrderBy(s => s.DayOfWeek)
            .ThenBy(s => s.StartTime)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TimeSlot>> ListActiveForDayOfWeekAsync(
        string physicianId, int dayOfWeek, string? clinicId = null, CancellationToken ct = default)
    {
        var query = context.TimeSlots
            .AsNoTracking()
            .Where(s => s.PhysicianId == physicianId && s.DayOfWeek == dayOfWeek && s.IsActive);

        if (clinicId is not null) query = query.Where(s => s.ClinicId == clinicId);

        return await query.OrderBy(s => s.StartTime).ToListAsync(ct);
    }

    /// <summary>
    /// Tracked, and with no IsActive filter — the caller is about to delete these rows, and the
    /// Node <c>deleteMany</c> takes deactivated ones with it.
    /// </summary>
    public async Task<IReadOnlyList<TimeSlot>> ListAllAsync(
        string physicianId, string clinicId, int? dayOfWeek = null, CancellationToken ct = default)
    {
        var query = context.TimeSlots
            .Where(s => s.PhysicianId == physicianId && s.ClinicId == clinicId);

        if (dayOfWeek.HasValue) query = query.Where(s => s.DayOfWeek == dayOfWeek.Value);

        return await query.ToListAsync(ct);
    }

    /// <summary>
    /// <c>ExecuteDeleteAsync</c>, not load-then-<c>RemoveRange</c>: it issues one DELETE that is
    /// committed the moment it returns, independently of the next <c>SaveChangesAsync</c>. That is
    /// what makes the routes' destructive-then-fail behaviour reproducible — see the port's note
    /// on the interface.
    /// </summary>
    public Task<int> DeleteAllAsync(
        string physicianId, string clinicId, int? dayOfWeek = null, CancellationToken ct = default)
    {
        var query = context.TimeSlots
            .Where(s => s.PhysicianId == physicianId && s.ClinicId == clinicId);

        if (dayOfWeek.HasValue) query = query.Where(s => s.DayOfWeek == dayOfWeek.Value);

        return query.ExecuteDeleteAsync(ct);
    }

    public void Add(TimeSlot slot) => context.TimeSlots.Add(slot);

    public void AddRange(IEnumerable<TimeSlot> slots) => context.TimeSlots.AddRange(slots);

    public void Remove(TimeSlot slot) => context.TimeSlots.Remove(slot);

    public void RemoveRange(IEnumerable<TimeSlot> slots) => context.TimeSlots.RemoveRange(slots);
}
