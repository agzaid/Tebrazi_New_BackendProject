using Microsoft.EntityFrameworkCore;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Persistence.Services;

/// <summary>
/// Visits' implementation of the published <see cref="IVisitDirectory"/> port.
///
/// It projects in SQL rather than materialising <c>Visit</c> entities: the summary needs twelve
/// columns, and a visit row carries the whole SOAP note plus the raw transcript. Loading those
/// to render a prescription list would move megabytes to read a date.
///
/// Soft-deleted visits are NOT excluded. <c>VisitsDbContext</c> deliberately carries no
/// soft-delete query filter — <c>softDeleteFilter</c> appears exactly once in all 1,442 lines of
/// visits.js, on the GET /api/visits list — so every method here sees a row with a non-null
/// <c>deleted_at</c> just as the Node route it mirrors does. Nothing in visits.js even writes
/// that column: DELETE /api/visits/{id} moves the status to ARCHIVED instead.
/// </summary>
public sealed class VisitDirectory(VisitsDbContext context) : IVisitDirectory
{
    public Task<VisitSummary?> GetAsync(string visitId, CancellationToken ct = default)
        => context.Visits
            .AsNoTracking()
            .Where(v => v.Id == visitId)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, VisitSummary>> GetManyAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default)
    {
        if (visitIds.Count == 0) return new Dictionary<string, VisitSummary>(0);

        var ids = visitIds.Distinct().ToArray();

        var rows = await context.Visits
            .AsNoTracking()
            .Where(v => ids.Contains(v.Id))
            .Select(Projection)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id);
    }

    public async Task<IReadOnlyList<string>> ListVisitIdsForPatientAsync(
        string patientUserId, string? subprofileId = null, CancellationToken ct = default)
    {
        var query = context.Visits.AsNoTracking().Where(v => v.PatientUserId == patientUserId);

        if (subprofileId is not null)
            query = query.Where(v => v.SubprofileId == subprofileId);

        return await query.Select(v => v.Id).ToListAsync(ct);
    }

    public Task<bool> BelongsToPhysicianAsync(
        string visitId, string physicianId, CancellationToken ct = default)
        => context.Visits
            .AsNoTracking()
            .AnyAsync(v => v.Id == visitId && v.PhysicianId == physicianId, ct);

    public Task<int> CountCompletedAsync(
        string clinicId, string patientUserId, CancellationToken ct = default)
        => context.Visits
            .AsNoTracking()
            .CountAsync(v => v.ClinicId == clinicId
                          && v.PatientUserId == patientUserId
                          && v.Status == VisitStatus.COMPLETED, ct);

    /// <summary>
    /// No status filter, matching the Node query — ARCHIVED and CANCELLED visits appear in the
    /// queue. The half-open window keeps a visit stamped at exactly midnight on the next day out.
    /// </summary>
    public async Task<IReadOnlyList<VisitQueueRow>> ListForClinicDayAsync(
        string clinicId, DateTime dayStartUtc, DateTime dayEndUtc, CancellationToken ct = default)
        => await context.Visits
            .AsNoTracking()
            .Where(v => v.ClinicId == clinicId
                     && v.VisitDate >= dayStartUtc
                     && v.VisitDate < dayEndUtc)
            .OrderBy(v => v.VisitDate)
            .Select(v => new VisitQueueRow(
                v.Id,
                v.PatientUserId,
                v.SubprofileId,
                v.ClinicPatientId,
                v.Status.ToString(),
                v.VisitDate,
                v.FollowUpDate,
                v.FollowUpNotes,
                v.CompletedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VisitDuration>> ListCompletedDurationsAsync(
        string physicianId, DateTime since, CancellationToken ct = default)
        => await context.Visits
            .AsNoTracking()
            .Where(v => v.PhysicianId == physicianId
                     && v.Status == VisitStatus.COMPLETED
                     && v.CompletedAt != null
                     && v.VisitDate >= since)
            .Select(v => new VisitDuration(v.VisitDate, v.CompletedAt!.Value))
            .ToListAsync(ct);

    /// <summary>
    /// The patient-facing recent-visits read, shared by two Node queries with different filters.
    ///
    /// <c>GET /api/patients/dashboard</c> (patients.js:794-812) asks for
    /// <c>status in (COMPLETED, ARCHIVED)</c> with <c>patientDismissedAt: null</c> and take 5;
    /// <c>GET /api/patients/health-summary</c> (patients.js:953-962) asks for
    /// <c>status in (COMPLETED, IN_PROGRESS)</c> with NO dismissed clause and take 5. Hence the
    /// caller-supplied statuses and the <paramref name="excludePatientDismissed"/> switch —
    /// hard-coding either would make one of the two endpoints wrong.
    ///
    /// Both order <c>visitDate: 'desc'</c>, and NEITHER carries a <c>deletedAt</c> clause.
    ///
    /// An unparseable status name is dropped rather than throwing, so an empty status set ends
    /// as an empty result — a filter that matches nothing, never one that matches everything.
    ///
    /// <c>ClinicName</c> is projected as null on purpose: the Visits context holds
    /// <c>visits</c> and <c>investigations</c> and there is no <c>clinics</c> table in it to
    /// join. The caller fills the name from <c>IClinicDirectory</c> using <c>ClinicId</c>,
    /// which is why that id is on the row.
    /// </summary>
    public async Task<IReadOnlyList<PatientVisitRow>> ListRecentForPatientAsync(
        string patientUserId,
        IReadOnlyCollection<string> statuses,
        bool excludePatientDismissed,
        int limit,
        CancellationToken ct = default)
    {
        var wanted = ParseStatuses(statuses);
        if (wanted.Length == 0 || limit <= 0) return [];

        var query = context.Visits
            .AsNoTracking()
            .Where(v => v.PatientUserId == patientUserId && wanted.Contains(v.Status));

        if (excludePatientDismissed)
            query = query.Where(v => v.PatientDismissedAt == null);

        return await query
            .OrderByDescending(v => v.VisitDate)
            .Take(limit)
            .Select(v => new PatientVisitRow(
                v.Id,
                v.VisitDate,
                v.Status.ToString(),
                v.ChiefComplaint,
                v.Diagnosis,
                v.FollowUpDate,
                v.FollowUpNotes,
                v.PhysicianId,
                v.SubprofileId,
                v.Subjective,
                v.Assessment,
                v.Plan,
                v.ClinicId,
                null,
                v.PatientDismissedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// <c>prisma.visit.count({ where: { patientUserId: userId } })</c> — patients.js:838-840,
    /// the dashboard's <c>stats.totalVisits</c>.
    ///
    /// One clause, and that is the whole query. No status filter, so CANCELLED and ARCHIVED
    /// visits count; no <c>patientDismissedAt</c> clause, so visits the patient hid from the
    /// list below the counter still count toward it; and no <c>deletedAt</c> clause, so
    /// soft-deleted rows count too. Adding any of the three would make this smaller than the
    /// number Node reports.
    /// </summary>
    public Task<int> CountForPatientAsync(string patientUserId, CancellationToken ct = default)
        => context.Visits
            .AsNoTracking()
            .CountAsync(v => v.PatientUserId == patientUserId, ct);

    /// <summary>
    /// Turns the port's status STRINGS into <see cref="VisitStatus"/> values the provider can
    /// compare against the string-converted column.
    ///
    /// Case-sensitive, matching <c>InvestigationUseCases</c>: these names come from Prisma's
    /// uppercase enum, and accepting "completed" here would let a typo through that Node
    /// rejects. Unknown names are dropped, and duplicates collapse.
    /// </summary>
    private static VisitStatus[] ParseStatuses(IReadOnlyCollection<string> statuses)
    {
        var parsed = new List<VisitStatus>(statuses.Count);

        foreach (var name in statuses)
            if (Enum.TryParse<VisitStatus>(name, ignoreCase: false, out var status)
                && !parsed.Contains(status))
                parsed.Add(status);

        return [.. parsed];
    }

    // ── Added for the Connections module ─────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, int>> CountByClinicPatientAsync(
        IReadOnlyCollection<string> clinicPatientIds, CancellationToken ct = default)
    {
        if (clinicPatientIds.Count == 0) return new Dictionary<string, int>(0);

        var ids = clinicPatientIds.Distinct().ToArray();

        // Prisma's `_count` on the relation counts every row: no status filter, no deletedAt
        // filter (connections.js:841).
        var rows = await context.Visits
            .AsNoTracking()
            .Where(v => v.ClinicPatientId != null && ids.Contains(v.ClinicPatientId))
            .GroupBy(v => v.ClinicPatientId!)
            .Select(g => new { ClinicPatientId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ClinicPatientId, r => r.Count);
    }

    public async Task<IReadOnlyDictionary<string, PhysicianPatientVisitSummary>>
        GetPatientVisitSummariesAsync(
            string physicianId,
            IReadOnlyCollection<string> patientUserIds,
            DateTime overdueFollowUpCutoff,
            CancellationToken ct = default)
    {
        if (patientUserIds.Count == 0)
            return new Dictionary<string, PhysicianPatientVisitSummary>(0);

        var ids = patientUserIds.Distinct().ToArray();

        var scoped = context.Visits
            .AsNoTracking()
            .Where(v => v.PhysicianId == physicianId && ids.Contains(v.PatientUserId));

        // Last visit + count in one pass. No status filter on either, matching
        // connections.js:1061-1068 — an IN_PROGRESS or CANCELLED visit can be the "last" one.
        var aggregates = await scoped
            .GroupBy(v => v.PatientUserId)
            .Select(g => new
            {
                PatientUserId = g.Key,
                Count = g.Count(),
                LastVisitDate = g.Max(v => (DateTime?)v.VisitDate)
            })
            .ToListAsync(ct);

        // The chief complaint of the row that carries that max date. Fetched separately because
        // SQL Server cannot project a non-aggregated column out of a GROUP BY, and a correlated
        // sub-select per patient is what the single ORDER BY below replaces.
        //
        // ⚠ NO `ChiefComplaint != null` FILTER. Node reads both keys off ONE row — the single
        // `findFirst({ orderBy: { visitDate: 'desc' } })` at connections.js:1061-1065 — and then
        // emits `lastVisit?.visitDate` and `lastVisit?.chiefComplaint` from it (:1110-1111). A
        // filter here would order over a SUBSET, skip past a latest visit whose complaint is NULL,
        // and report an OLDER visit's complaint against the newer visit's date: the physician
        // dashboard would show a stale complaint as if it belonged to the most recent encounter.
        // The two keys must always come from the same visit, null complaint included.
        var lastComplaints = await scoped
            .Select(v => new { v.PatientUserId, v.VisitDate, v.ChiefComplaint })
            .ToListAsync(ct);

        var complaintByPatient = lastComplaints
            .GroupBy(v => v.PatientUserId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(v => v.VisitDate).First().ChiefComplaint);

        // DISTINCT visited dependants (connections.js:1083-1087). No status filter.
        var visitedSubprofiles = await scoped
            .Where(v => v.SubprofileId != null)
            .Select(v => new { v.PatientUserId, SubprofileId = v.SubprofileId! })
            .Distinct()
            .ToListAsync(ct);

        var subprofilesByPatient = visitedSubprofiles
            .GroupBy(v => v.PatientUserId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<string> (g) => g.Select(v => v.SubprofileId).ToList());

        // The ONLY query in the group with a status filter (connections.js:1091-1099).
        var overdue = await scoped
            .Where(v => v.Status == VisitStatus.COMPLETED
                        && v.FollowUpDate != null
                        && v.FollowUpDate <= overdueFollowUpCutoff)
            .Select(v => new { v.PatientUserId, v.FollowUpDate })
            .ToListAsync(ct);

        var overdueByPatient = overdue
            .GroupBy(v => v.PatientUserId)
            // Node takes findFirst with no orderBy; earliest is the deterministic choice and is
            // also the one a physician means by "overdue".
            .ToDictionary(g => g.Key, g => g.Min(v => v.FollowUpDate));

        return aggregates.ToDictionary(
            a => a.PatientUserId,
            a => new PhysicianPatientVisitSummary(
                a.LastVisitDate,
                complaintByPatient.GetValueOrDefault(a.PatientUserId),
                a.Count,
                subprofilesByPatient.GetValueOrDefault(a.PatientUserId, []),
                overdueByPatient.GetValueOrDefault(a.PatientUserId)));
    }

    /// <summary>Shared by both reads so the two projections cannot drift apart.</summary>
    private static readonly System.Linq.Expressions.Expression<
        Func<Domain.Entities.Visit, VisitSummary>> Projection =
        v => new VisitSummary(
            v.Id,
            v.OrganizationId,
            v.ClinicId,
            v.PhysicianId,
            v.PatientUserId,
            v.SubprofileId,
            v.ClinicPatientId,
            v.VisitDate,
            v.Status.ToString(),
            v.ChiefComplaint,
            v.Diagnosis,
            v.Plan);
}
