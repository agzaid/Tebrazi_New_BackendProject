using Microsoft.EntityFrameworkCore;
using Tebrazi.Appointments.Application.Abstractions.Persistence;
using Tebrazi.Appointments.Domain.Entities;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Appointments.Persistence;

/// <summary>
/// The Appointments module's context: <c>appointments</c> and <c>time_slots</c>.
/// </summary>
public sealed class AppointmentsDbContext(
    DbContextOptions<AppointmentsDbContext> options,
    ICurrentUser currentUser)
    : BaseDbContext(options, currentUser), IAppointmentsDbContext
{
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<TimeSlot> TimeSlots => Set<TimeSlot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppointmentsDbContext).Assembly);

        // ── There is deliberately NO soft-delete query filter here ──────────────
        //
        // server/src/routes/appointments.js never filters on deletedAt — not once in 1,559
        // lines. It does not even import `softDeleteFilter`. Adding a blanket filter would hide
        // rows the Node backend still returns, changing the day view, the queue, the
        // availability check and the ai-optimize statistics all at once.
        //
        // Nor does anything WRITE DeletedAt. DELETE /api/appointments/{id} is a HARD delete
        // (prisma.appointment.delete, appointments.js:948) — use IAppointmentStore.Remove for it,
        // not Appointment.SoftDelete(). The column exists for schema parity alone.
        //
        // A CANCELLED appointment is a different thing again: it still appears in the day view,
        // struck through, and only stops holding its slot.
    }
}
