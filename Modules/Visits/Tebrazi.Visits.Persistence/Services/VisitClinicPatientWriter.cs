using Microsoft.EntityFrameworkCore;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Visits.Persistence.Services;

/// <summary>
/// Visits' implementation of <see cref="IVisitClinicPatientWriter"/> — the only write another
/// module may make to <c>visits</c>, and only for
/// <c>DELETE /api/connections/clinic-patients/{id}</c> (connections.js:877).
/// </summary>
public sealed class VisitClinicPatientWriter(VisitsDbContext context) : IVisitClinicPatientWriter
{
    public Task<int> DeleteForClinicPatientAsync(
        string clinicPatientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clinicPatientId);

        // A HARD delete, unlike DELETE /api/visits/{id}, which only moves the status to ARCHIVED.
        // The chart is going away and these rows have nothing left to reference. No deletedAt
        // predicate: already-soft-deleted visits go too, matching `deleteMany`.
        //
        // ExecuteDeleteAsync issues one DELETE statement and commits on its own, so it does not
        // participate in the caller's unit of work — which is fine here, because the Node
        // $transaction cannot be reproduced across three module contexts anyway. See
        // docs/connections-surface.md §10.
        return context.Visits
            .Where(v => v.ClinicPatientId == clinicPatientId)
            .ExecuteDeleteAsync(ct);
    }
}
