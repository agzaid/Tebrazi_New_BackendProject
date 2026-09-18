using Microsoft.EntityFrameworkCore;
using Tebrazi.Appointments.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Appointments.Persistence.Services;

/// <summary>Appointments' implementation of the published <see cref="IAppointmentDirectory"/> port.</summary>
public sealed class AppointmentDirectory(AppointmentsDbContext context) : IAppointmentDirectory
{
    /// <summary>
    /// No soft-delete filter: appointments.js never applies one, and the Visits handler that
    /// calls this reads the appointment by id exactly as the Node route does.
    /// </summary>
    public Task<AppointmentSummary?> GetAppointmentAsync(
        string appointmentId, CancellationToken ct = default)
        => context.Appointments
            .AsNoTracking()
            .Where(a => a.Id == appointmentId)
            .Select(a => new AppointmentSummary(
                a.Id,
                a.ClinicId,
                a.PhysicianId,
                a.PatientUserId,
                a.SubprofileId,
                a.ClinicPatientId,
                a.Status.ToString(),
                a.AppointmentDate,
                a.StartTime,
                a.EndTime,
                a.AppointmentType.ToString()))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The dashboard's upcoming-appointments query — patients.js:768-792, as FIXED.
    ///
    /// Three things that block are worth stating, because the Node original got all three wrong
    /// and a <c>.catch(() =&gt; [])</c> hid it:
    /// <list type="bullet">
    /// <item>The column is <c>appointmentDate</c>, not <c>date</c>. It is both the filter and
    /// the sort key.</item>
    /// <item>The pending status is <c>PENDING</c>. <c>SCHEDULED</c> is not a member of
    /// <see cref="AppointmentStatus"/> and never was.</item>
    /// <item>The order is ASCENDING — the soonest appointment first — unlike the visit reads
    /// either side of it, which run newest-first.</item>
    /// </list>
    ///
    /// <c>DateTime.UtcNow</c> is read once into a local so the boundary is a parameter rather
    /// than something the provider has to translate, and so every row on the page is compared
    /// against the same instant.
    ///
    /// No soft-delete predicate and no query filter to inherit one from: the string
    /// <c>deletedAt</c> does not occur anywhere in appointments.js.
    ///
    /// <c>ClinicName</c> is projected as null — there is no <c>clinics</c> table in this
    /// context — and <c>ClinicId</c> is on the row so the caller can resolve it through
    /// <c>IClinicDirectory</c>.
    /// </summary>
    public async Task<IReadOnlyList<PatientAppointmentRow>> ListUpcomingForPatientAsync(
        string patientUserId, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        var now = DateTime.UtcNow;

        return await context.Appointments
            .AsNoTracking()
            .Where(a => a.PatientUserId == patientUserId
                     && a.AppointmentDate >= now
                     && (a.Status == AppointmentStatus.PENDING
                      || a.Status == AppointmentStatus.CONFIRMED))
            .OrderBy(a => a.AppointmentDate)
            .Take(limit)
            .Select(a => new PatientAppointmentRow(
                a.Id,
                a.AppointmentDate,
                a.StartTime,
                a.EndTime,
                a.Status.ToString(),
                a.PhysicianId,
                a.ClinicId,
                null,
                a.AppointmentType.ToString()))
            .ToListAsync(ct);
    }
}
