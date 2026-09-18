using Microsoft.EntityFrameworkCore;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Appointments.Persistence.Services;

/// <summary>
/// Appointments' implementation of <see cref="IAppointmentClinicPatientWriter"/> — the only write
/// another module may make to <c>appointments</c>, and only for
/// <c>DELETE /api/connections/clinic-patients/{id}</c> (connections.js:879).
/// </summary>
public sealed class AppointmentClinicPatientWriter(AppointmentsDbContext context)
    : IAppointmentClinicPatientWriter
{
    public Task<int> DeleteForClinicPatientAsync(
        string clinicPatientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clinicPatientId);

        // No status filter and no deletedAt predicate, matching `deleteMany`. Time slots are
        // deliberately NOT released: the Node transaction deletes appointment rows and nothing
        // else, so a slot one of them booked stays booked in both backends.
        //
        // ExecuteDeleteAsync issues one DELETE and commits on its own, so it does not join the
        // caller's unit of work — the Node $transaction cannot be reproduced across three module
        // contexts anyway. See docs/connections-surface.md §10.
        return context.Appointments
            .Where(a => a.ClinicPatientId == clinicPatientId)
            .ExecuteDeleteAsync(ct);
    }
}
