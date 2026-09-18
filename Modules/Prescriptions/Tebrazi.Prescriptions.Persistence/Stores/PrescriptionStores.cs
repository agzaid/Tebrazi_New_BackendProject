using Microsoft.EntityFrameworkCore;
using Tebrazi.Prescriptions.Application.Abstractions.Persistence;
using Tebrazi.Prescriptions.Domain.Entities;

namespace Tebrazi.Prescriptions.Persistence.Stores;

public sealed class PrescriptionStore(PrescriptionsDbContext context) : IPrescriptionStore
{
    public Task<Prescription?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Prescriptions.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Prescription?> GetAsync(string id, CancellationToken ct = default)
        => context.Prescriptions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<(IReadOnlyList<Prescription> Items, int TotalCount)> PageAsync(
        PrescriptionFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;

        var query = Apply(context.Prescriptions.AsNoTracking(), filter)
            .OrderByDescending(p => p.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return (items, total);
    }

    public async Task<IReadOnlyList<Prescription>> ListAsync(
        PrescriptionFilter filter, CancellationToken ct = default)
        => await Apply(context.Prescriptions.AsNoTracking(), filter)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Prescription>> ListForVisitAsync(
        string visitId, CancellationToken ct = default)
        => await context.Prescriptions
            .AsNoTracking()
            .Where(p => p.VisitId == visitId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<string, List<Prescription>>> ListForVisitsAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default)
    {
        if (visitIds.Count == 0) return new Dictionary<string, List<Prescription>>(0);

        var rows = await context.Prescriptions
            .AsNoTracking()
            .Where(p => visitIds.Contains(p.VisitId))
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return rows.GroupBy(p => p.VisitId).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// The cross-prescription sweep for check-interactions. Ordered by <c>created_at</c> ASC
    /// because Node's findMany at prescriptions.js:768-774 carries NO orderBy — Postgres returns
    /// physical order, which for this append-only table is insertion order, and the order is
    /// observable in the response's <c>drugsChecked</c>. No deletedAt filter: the Node query has
    /// none (audit-1 confirms it), so a soft-deleted prescription still contributes its drugs.
    /// </summary>
    public async Task<IReadOnlyList<Prescription>> ListForInteractionCheckAsync(
        IReadOnlyCollection<string> visitIds,
        IReadOnlyCollection<PrescriptionStatus> statuses,
        CancellationToken ct = default)
    {
        // Both are real filters when empty: no visits and no statuses each match nothing.
        if (visitIds.Count == 0 || statuses.Count == 0) return [];

        return await context.Prescriptions
            .AsNoTracking()
            .Where(p => visitIds.Contains(p.VisitId) && statuses.Contains(p.Status))
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>One GROUP BY in the database, not a set of counts issued per status.</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(
        string physicianId, CancellationToken ct = default)
    {
        var rows = await context.Prescriptions
            .AsNoTracking()
            .Where(p => p.PhysicianId == physicianId)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Status.ToString(), r => r.Count);
    }

    public void Add(Prescription prescription) => context.Prescriptions.Add(prescription);

    /// <summary>
    /// Marks the entity Modified so SaveChanges emits an UPDATE with no property change, which is
    /// what stamps <c>updated_at</c>. Reproduces Prisma's <c>@updatedAt</c> firing for
    /// <c>prisma.update({ data: {} })</c> when PUT /api/prescriptions/{id} receives an empty body
    /// (prescriptions.js:264-269).
    /// </summary>
    public void MarkModified(Prescription prescription)
        => context.Entry(prescription).State = EntityState.Modified;

    private static IQueryable<Prescription> Apply(IQueryable<Prescription> query, PrescriptionFilter f)
    {
        if (f.PhysicianId is not null) query = query.Where(p => p.PhysicianId == f.PhysicianId);
        if (f.VisitId is not null) query = query.Where(p => p.VisitId == f.VisitId);
        if (f.SubprofileId is not null) query = query.Where(p => p.SubprofileId == f.SubprofileId);
        if (f.Status.HasValue) query = query.Where(p => p.Status == f.Status.Value);
        if (f.RefillStatus is not null) query = query.Where(p => p.RefillStatus == f.RefillStatus);

        // Like VisitIds below, an empty set is a filter that matches nothing.
        if (f.StatusIn is not null) query = query.Where(p => f.StatusIn.Contains(p.Status));

        if (f.From.HasValue) query = query.Where(p => p.CreatedAt >= f.From.Value);
        if (f.To.HasValue) query = query.Where(p => p.CreatedAt <= f.To.Value);

        // An EMPTY collection is a real filter meaning "no visits matched", and it must return
        // nothing. Treating it as "unfiltered" would leak every prescription in the table.
        if (f.VisitIds is not null)
            query = query.Where(p => f.VisitIds.Contains(p.VisitId));

        // Soft delete is filtered ONLY when the caller asks. Null — which is what all 17 ported
        // endpoints pass — leaves deleted and live rows alike, because not one query in
        // prescriptions.js mentions deletedAt.
        if (f.IsDeleted is not null)
            query = f.IsDeleted.Value
                ? query.Where(p => p.DeletedAt != null)
                : query.Where(p => p.DeletedAt == null);

        return query;
    }
}

public sealed class InteractionAlertStore(PrescriptionsDbContext context) : IInteractionAlertStore
{
    public void Add(InteractionAlert alert) => context.InteractionAlerts.Add(alert);

    /// <summary>
    /// The physician's alert history, newest first, for GET /api/prescriptions/interaction-history
    /// (prescriptions.js:176-180). Filters on physician_id alone: the route short-circuits with
    /// 200 [] for any caller without a physician profile, so patient-authored rows are never read
    /// back by this endpoint.
    ///
    /// A negative <paramref name="take"/> is Prisma reverse-take, not an error — the LAST |take|
    /// rows of the checkedAt-desc ordering, i.e. the oldest alerts, still presented newest-first.
    /// Reproduced by flipping the sort, taking, then reversing.
    /// </summary>
    public async Task<IReadOnlyList<InteractionAlert>> ListForPhysicianAsync(
        string physicianId, int take, CancellationToken ct = default)
    {
        if (take == 0) return [];

        var query = context.InteractionAlerts
            .AsNoTracking()
            .Where(a => a.PhysicianId == physicianId);

        if (take > 0)
        {
            return await query
                .OrderByDescending(a => a.CheckedAt)
                .Take(take)
                .ToListAsync(ct);
        }

        // -int.MinValue overflows back to itself, and Take() rejects a negative count.
        var tailCount = take == int.MinValue ? int.MaxValue : -take;

        var tail = await query
            .OrderBy(a => a.CheckedAt)
            .Take(tailCount)
            .ToListAsync(ct);

        tail.Reverse();
        return tail;
    }
}
