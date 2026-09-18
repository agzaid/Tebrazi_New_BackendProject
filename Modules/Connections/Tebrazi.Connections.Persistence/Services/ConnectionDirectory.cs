using Microsoft.EntityFrameworkCore;
using Tebrazi.Connections.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Connections.Persistence.Services;

/// <summary>
/// Connections' implementation of the published <see cref="IConnectionDirectory"/> port.
///
/// It lives in Persistence, next to <c>ClinicPatientDirectory</c>'s equivalent, because it
/// projects straight out of the context: mapping in SQL rather than materialising entities keeps
/// a dashboard read to the three columns it needs.
/// </summary>
public sealed class ConnectionDirectory(ConnectionsDbContext context) : IConnectionDirectory
{
    public async Task<IReadOnlyList<PatientDoctorRow>> ListAcceptedDoctorsForPatientAsync(
        string patientUserId, CancellationToken ct = default)
        => await context.Connections
            .AsNoTracking()
            // Status only. No subprofile predicate: a patient connected to one doctor for
            // themselves and for two children legitimately gets three rows (patients.js:703).
            .Where(c => c.PatientUserId == patientUserId && c.Status == ConnectionStatus.ACCEPTED)
            .OrderByDescending(c => c.ConnectedAt)
            .Select(c => new PatientDoctorRow(c.PhysicianUserId, c.SubprofileId, c.ConnectedAt))
            .ToListAsync(ct);

    public Task<bool> HasAcceptedConnectionAsync(
        string physicianUserId, string patientUserId, CancellationToken ct = default)
        => context.Connections
            .AsNoTracking()
            .AnyAsync(
                c => c.PhysicianUserId == physicianUserId
                     && c.PatientUserId == patientUserId
                     && c.Status == ConnectionStatus.ACCEPTED,
                ct);
}
