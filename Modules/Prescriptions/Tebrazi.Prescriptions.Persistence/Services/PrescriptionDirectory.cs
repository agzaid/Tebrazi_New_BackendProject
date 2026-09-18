using Microsoft.EntityFrameworkCore;
using Tebrazi.Prescriptions.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Prescriptions.Persistence.Services;

/// <summary>
/// Prescriptions' implementation of the published <see cref="IPrescriptionDirectory"/> port.
///
/// No method here filters soft-deleted rows. That is deliberate: <c>prescriptions: true</c> and
/// <c>_count</c> in the Node visit routes carry no <c>where</c>, so deleted prescriptions are
/// both counted and returned, with their <c>deletedAt</c> visible to the client.
/// </summary>
public sealed class PrescriptionDirectory(PrescriptionsDbContext context) : IPrescriptionDirectory
{
    public async Task<IReadOnlyDictionary<string, int>> CountByVisitAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default)
    {
        if (visitIds.Count == 0) return new Dictionary<string, int>(0);

        var ids = visitIds.Distinct().ToArray();

        var rows = await context.Prescriptions
            .AsNoTracking()
            .Where(p => ids.Contains(p.VisitId))
            .GroupBy(p => p.VisitId)
            .Select(g => new { VisitId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.VisitId, r => r.Count);
    }

    public async Task<IReadOnlyList<PrescriptionRow>> ListByVisitAsync(
        string visitId, CancellationToken ct = default)
        => await context.Prescriptions
            .AsNoTracking()
            .Where(p => p.VisitId == visitId)
            // The Node include has no orderBy, so Postgres returns these in arbitrary order.
            // Ordering by creation makes the .NET response deterministic; the divergence is
            // recorded in docs/PORT-STATUS.md.
            .OrderBy(p => p.CreatedAt)
            .Select(p => new PrescriptionRow(
                p.Id,
                p.VisitId,
                p.PhysicianId,
                p.SubprofileId,
                p.Status.ToString(),
                p.Medications,
                p.Notes,
                p.SignedAt,
                p.SentToPatientAt,
                p.PdfUrl,
                p.RefillRequestedAt,
                p.RefillStatus,
                p.RefillNotes,
                p.CreatedAt,
                p.UpdatedAt,
                p.DeletedAt))
            .ToListAsync(ct);

    /// <summary>
    /// The patient-facing read behind three Node queries that all share one shape:
    /// <c>where: { visit: { patientUserId }, status: { in: [...] } }, orderBy: { createdAt:
    /// 'desc' }</c> — patients.js:561-576 (/medications/reconciled, no <c>take</c>),
    /// patients.js:739-748 (/dashboard, take 10) and patients.js:1008-1013 (/health-summary,
    /// take 20, and the only one of the three that also accepts SIGNED).
    ///
    /// The relation filter cannot be expressed here. <c>prescriptions</c> has no patient column
    /// and this context does not own <c>visits</c>, so <c>visit: { patientUserId }</c> becomes
    /// an id set the caller resolved through <c>IVisitDirectory.ListVisitIdsForPatientAsync</c>.
    ///
    /// <b>An empty id set therefore returns nothing.</b> It means "this patient has no visits",
    /// and dropping the predicate instead — the obvious way to write this — would return every
    /// prescription in the table to a patient who has never been seen.
    ///
    /// No <c>deletedAt</c> clause: none of the three Node queries has one.
    ///
    /// <c>VisitDate</c> and <c>VisitChiefComplaint</c> are projected as null for the same reason
    /// the relation filter is a parameter — the visit is not in this context. <c>VisitId</c> is
    /// on the row so the caller can fill both from <c>IVisitDirectory.GetManyAsync</c>.
    /// </summary>
    public async Task<IReadOnlyList<PatientPrescriptionRow>> ListByVisitIdsAsync(
        IReadOnlyCollection<string> visitIds,
        IReadOnlyCollection<string> statuses,
        int limit,
        CancellationToken ct = default)
    {
        if (visitIds.Count == 0 || limit <= 0) return [];

        var wanted = ParseStatuses(statuses);
        if (wanted.Length == 0) return [];

        var ids = visitIds.Distinct().ToArray();

        return await context.Prescriptions
            .AsNoTracking()
            .Where(p => ids.Contains(p.VisitId) && wanted.Contains(p.Status))
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .Select(p => new PatientPrescriptionRow(
                p.Id,
                p.VisitId,
                p.PhysicianId,
                p.SubprofileId,
                p.Status.ToString(),
                p.Medications,
                null,
                null,
                p.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Turns the port's status STRINGS into <see cref="PrescriptionStatus"/> values the provider
    /// can compare against the string-converted column. Case-sensitive — the names come from
    /// Prisma's uppercase enum — with unknown names dropped and duplicates collapsed.
    /// </summary>
    private static PrescriptionStatus[] ParseStatuses(IReadOnlyCollection<string> statuses)
    {
        var parsed = new List<PrescriptionStatus>(statuses.Count);

        foreach (var name in statuses)
            if (Enum.TryParse<PrescriptionStatus>(name, ignoreCase: false, out var status)
                && !parsed.Contains(status))
                parsed.Add(status);

        return [.. parsed];
    }
}
