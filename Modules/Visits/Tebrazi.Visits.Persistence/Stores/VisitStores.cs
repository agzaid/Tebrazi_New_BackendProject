using Microsoft.EntityFrameworkCore;
using Tebrazi.Visits.Application.Abstractions.Persistence;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Persistence.Stores;

/// <summary>
/// Every method here states whether it excludes soft-deleted rows, because the Node route file
/// is inconsistent about it: <c>softDeleteFilter</c> appears exactly once, on the
/// <c>GET /api/visits</c> list. There is no context-level query filter, so a query that does not
/// say <c>DeletedAt == null</c> genuinely returns deleted rows — which is what the Node
/// endpoints do.
/// </summary>
public sealed class VisitStore(VisitsDbContext context) : IVisitStore
{
    /// <summary>No soft-delete filter: the caller is about to act on a specific visit by id.</summary>
    public Task<Visit?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Visits.FirstOrDefaultAsync(v => v.Id == id, ct);

    /// <summary>No soft-delete filter, matching <c>findUnique({ where: { id } })</c>.</summary>
    public Task<Visit?> GetWithInvestigationsAsync(string id, CancellationToken ct = default)
        => context.Visits
            .AsNoTracking()
            .Include(v => v.Investigations)
            .FirstOrDefaultAsync(v => v.Id == id, ct);

    /// <summary>Excludes soft-deleted rows — the one Node query that does.</summary>
    public async Task<(IReadOnlyList<Visit> Items, int TotalCount)> PageForPhysicianAsync(
        PhysicianVisitFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        var query = context.Visits
            .AsNoTracking()
            .Where(v => v.DeletedAt == null && v.PhysicianId == filter.PhysicianId);

        if (filter.ClinicId is not null) query = query.Where(v => v.ClinicId == filter.ClinicId);
        if (filter.PatientUserId is not null) query = query.Where(v => v.PatientUserId == filter.PatientUserId);
        if (filter.SubprofileId is not null) query = query.Where(v => v.SubprofileId == filter.SubprofileId);

        // An explicit status is an equality filter, so ?status=ARCHIVED really does return
        // archived visits. Only the DEFAULT excludes them.
        query = filter.Status.HasValue
            ? query.Where(v => v.Status == filter.Status.Value)
            : query.Where(v => v.Status != VisitStatus.ARCHIVED);

        return await PageAsync(query.OrderByDescending(v => v.VisitDate), page, pageSize, ct);
    }

    /// <summary>Excludes soft-deleted and patient-dismissed rows.</summary>
    public async Task<(IReadOnlyList<Visit> Items, int TotalCount)> PageForPatientAsync(
        string patientUserId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = context.Visits
            .AsNoTracking()
            .Where(v => v.DeletedAt == null
                     && v.PatientUserId == patientUserId
                     // COMPLETED *and* ARCHIVED: archiving is the physician's delete, and the
                     // patient deliberately keeps access to the archived record.
                     && (v.Status == VisitStatus.COMPLETED || v.Status == VisitStatus.ARCHIVED)
                     && v.PatientDismissedAt == null);

        return await PageAsync(query.OrderByDescending(v => v.VisitDate), page, pageSize, ct);
    }

    public async Task<IReadOnlyList<Visit>> ListForPatientAsync(
        string patientUserId, string? subprofileId, CancellationToken ct = default)
    {
        var query = context.Visits
            .AsNoTracking()
            .Where(v => v.DeletedAt == null && v.PatientUserId == patientUserId);

        // A named subprofile narrows to that dependant; omitting it returns the whole account's
        // history, dependants included.
        if (subprofileId is not null)
            query = query.Where(v => v.SubprofileId == subprofileId);

        return await query
            .OrderByDescending(v => v.VisitDate)
            .ThenByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<Visit> Items, int TotalCount)> PageInboxAsync(
        string patientUserId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = context.Visits
            .AsNoTracking()
            .Where(v => v.DeletedAt == null
                     && v.PatientUserId == patientUserId
                     && v.Status == VisitStatus.COMPLETED
                     && v.PatientDismissedAt == null)
            .OrderByDescending(v => v.VisitDate);

        return await PageAsync(query, page, pageSize, ct);
    }

    public async Task<IReadOnlyList<Visit>> ListFollowUpsDueAsync(
        string physicianId, DateTime asOf, CancellationToken ct = default)
        => await context.Visits
            .AsNoTracking()
            .Where(v => v.DeletedAt == null
                     && v.PhysicianId == physicianId
                     && v.FollowUpDate != null
                     && v.FollowUpDate <= asOf
                     && v.Status != VisitStatus.ARCHIVED)
            .OrderBy(v => v.FollowUpDate)
            .ToListAsync(ct);

    public Task<bool> BelongsToPhysicianAsync(string visitId, string physicianId, CancellationToken ct = default)
        => context.Visits
            .AsNoTracking()
            .AnyAsync(v => v.Id == visitId && v.PhysicianId == physicianId, ct);

    public void Add(Visit visit) => context.Visits.Add(visit);

    /// <summary>
    /// One COUNT and one Skip/Take, both in SQL. The bounds copy
    /// <c>server/src/lib/queryHelpers.js</c>: page floors at 1, size floors at 1 and caps at 100.
    /// </summary>
    private static async Task<(IReadOnlyList<Visit> Items, int TotalCount)> PageAsync(
        IOrderedQueryable<Visit> query, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return (items, total);
    }
}

public sealed class InvestigationStore(VisitsDbContext context) : IInvestigationStore
{
    public Task<Investigation?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Investigations.FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<Investigation>> ListForVisitAsync(
        string visitId, CancellationToken ct = default)
        => await context.Investigations
            .AsNoTracking()
            .Where(i => i.VisitId == visitId)
            .OrderByDescending(i => i.RequestedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<string, List<Investigation>>> ListForVisitsAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default)
    {
        if (visitIds.Count == 0) return new Dictionary<string, List<Investigation>>(0);

        var ids = visitIds.Distinct().ToArray();

        var rows = await context.Investigations
            .AsNoTracking()
            .Where(i => ids.Contains(i.VisitId))
            .OrderByDescending(i => i.RequestedAt)
            .ToListAsync(ct);

        return rows.GroupBy(i => i.VisitId).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>One GROUP BY for the whole page, not a count per visit.</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountForVisitsAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default)
    {
        if (visitIds.Count == 0) return new Dictionary<string, int>(0);

        var ids = visitIds.Distinct().ToArray();

        var rows = await context.Investigations
            .AsNoTracking()
            .Where(i => ids.Contains(i.VisitId))
            .GroupBy(i => i.VisitId)
            .Select(g => new { VisitId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.VisitId, r => r.Count);
    }

    public void Add(Investigation investigation) => context.Investigations.Add(investigation);

    public void Remove(Investigation investigation) => context.Investigations.Remove(investigation);
}
