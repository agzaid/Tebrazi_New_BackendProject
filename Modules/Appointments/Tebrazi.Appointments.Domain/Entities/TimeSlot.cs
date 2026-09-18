using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Appointments.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>TimeSlot</c> model (table <c>time_slots</c>) — a physician's recurring
/// weekly availability at a clinic, from which bookable slots are generated.
///
/// A row is a WEEKLY template, not a bookable instance: <c>DayOfWeek</c> plus a start and end
/// time, repeating every week. <c>GET /api/appointments/available</c> expands these against a
/// concrete date and subtracts the appointments already booked.
///
/// Prisma declares no <c>updatedAt</c> on this model, so it derives from
/// <see cref="ImmutableEntity{TKey}"/>: slots are added, deactivated and deleted, never edited.
/// </summary>
public sealed class TimeSlot : ImmutableEntity<string>
{
    private TimeSlot() { }

    public string ClinicId { get; private set; } = null!;
    public string PhysicianId { get; private set; } = null!;

    /// <summary>0 = Sunday through 6 = Saturday, matching JavaScript's <c>Date.getDay()</c>.</summary>
    public int DayOfWeek { get; private set; }

    /// <summary>"HH:mm".</summary>
    public string StartTime { get; private set; } = null!;

    /// <summary>"HH:mm".</summary>
    public string EndTime { get; private set; } = null!;

    /// <summary>Minutes per bookable slot within the window. Defaults to 30.</summary>
    public int SlotDuration { get; private set; } = 30;

    public bool IsActive { get; private set; } = true;

    public static TimeSlot Create(
        string clinicId,
        string physicianId,
        int dayOfWeek,
        string startTime,
        string endTime,
        int slotDuration = 30,
        bool isActive = true)
    {
        // DELIBERATELY UNVALIDATED BEYOND NULL, because the contract is Node's, and Node's three
        // slot-creating routes write these columns with no validation whatsoever:
        //
        //   * dayOfWeek is `parseInt(dayOfWeek)` straight into a bare `Int` column with no check
        //     constraint (server/prisma/schema.prisma:1199), so 7, 99 and -1 are all STORED and
        //     the routes answer 201/200. Such a row simply never matches GET /available's
        //     `targetDate.getDay()`. A 0..6 guard here would turn those into a 500 — and on
        //     /slots/sync-from-hours it would do so AFTER the destructive wipe has committed,
        //     destroying a whole clinic's grid where Node rebuilds it.
        //   * slotDuration is `slotDuration || 30` into a bare `Int` column, so a negative value
        //     is stored too (POST /slots has no generation loop to filter it out).
        //   * startTime/endTime are passed through verbatim with no trim and no format check, so
        //     "", "  " and "not a time" are all legal stored values.
        //
        // Null is the one genuine rejection: the columns are NOT NULL, so Prisma fails the whole
        // statement and the route answers its own 500. Keep these four as null-only checks.
        ArgumentNullException.ThrowIfNull(clinicId);
        ArgumentNullException.ThrowIfNull(physicianId);
        ArgumentNullException.ThrowIfNull(startTime);
        ArgumentNullException.ThrowIfNull(endTime);

        return new TimeSlot
        {
            Id = Guid.NewGuid().ToString(),
            ClinicId = clinicId,
            PhysicianId = physicianId,
            DayOfWeek = dayOfWeek,
            StartTime = startTime,
            EndTime = endTime,
            SlotDuration = slotDuration,
            IsActive = isActive
        };
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
