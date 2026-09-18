using Tebrazi.SharedKernel.Abstractions.Persistence;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Application.Abstractions.Persistence;

/// <summary>The Visits module's unit of work. Handlers inject THIS, never the concrete context.</summary>
public interface IVisitsDbContext : IDbContext;

/// <summary>
/// The physician half of <c>GET /api/visits</c>. A null member is not applied.
/// </summary>
/// <param name="Status">
/// When set, it is applied as an equality filter — <c>?status=ARCHIVED</c> therefore DOES return
/// archived visits. When null, the filter becomes <c>status != ARCHIVED</c> instead. The code
/// comment in the Node route claims physicians never see archived visits; that is not what the
/// code does, and the port keeps the code's behaviour.
/// </param>
public sealed record PhysicianVisitFilter(
    string PhysicianId,
    string? ClinicId = null,
    string? PatientUserId = null,
    string? SubprofileId = null,
    VisitStatus? Status = null);

public interface IVisitStore
{
    /// <summary>Tracked, for a handler that is about to modify the visit.</summary>
    Task<Visit?> GetForUpdateAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Read-only by id, with investigations loaded.
    ///
    /// It does NOT exclude soft-deleted or ARCHIVED rows, because
    /// <c>GET /api/visits/{id}</c> is a bare <c>findUnique({ where: { id } })</c>. A visit the
    /// physician archived is still readable by id, by design — that is how the patient keeps
    /// access to it.
    /// </summary>
    Task<Visit?> GetWithInvestigationsAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The physician's page of <c>GET /api/visits</c>: excludes <c>deleted_at</c> rows, which is
    /// the one place in the whole Node route file that applies the soft-delete filter.
    /// </summary>
    Task<(IReadOnlyList<Visit> Items, int TotalCount)> PageForPhysicianAsync(
        PhysicianVisitFilter filter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// The patient's page of <c>GET /api/visits</c>: their own visits, COMPLETED or ARCHIVED
    /// only, excluding ones they dismissed and ones with <c>deleted_at</c> set.
    ///
    /// Any <c>?status=</c> the caller sent is ignored — the Node route overwrites it.
    /// </summary>
    Task<(IReadOnlyList<Visit> Items, int TotalCount)> PageForPatientAsync(
        string patientUserId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// One patient's visit history, newest first, as a BARE list.
    ///
    /// Backs <c>GET /api/visits/patient/{patientUserId}</c>, which does not exist in the Node
    /// backend at all — the client calls it and receives a 404. See docs/PORT-STATUS.md: this
    /// port implements it rather than reproducing the 404, and returns a bare array because
    /// that is what the calling component already expects.
    /// </summary>
    Task<IReadOnlyList<Visit>> ListForPatientAsync(
        string patientUserId, string? subprofileId, CancellationToken ct = default);

    /// <summary>
    /// The patient's inbox: their COMPLETED visits, excluding dismissed and soft-deleted ones.
    /// Backs <c>GET /api/visits/inbox</c>, which is unreachable in the Node backend because
    /// <c>GET /:id</c> is registered first and swallows it.
    /// </summary>
    Task<(IReadOnlyList<Visit> Items, int TotalCount)> PageInboxAsync(
        string patientUserId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Visits whose follow-up date has arrived. Backs <c>GET /api/visits/follow-ups-due</c>,
    /// also unreachable in Node for the same route-order reason.
    /// </summary>
    Task<IReadOnlyList<Visit>> ListFollowUpsDueAsync(
        string physicianId, DateTime asOf, CancellationToken ct = default);

    /// <summary>Confirms a visit exists and belongs to a physician, without loading it.</summary>
    Task<bool> BelongsToPhysicianAsync(string visitId, string physicianId, CancellationToken ct = default);

    void Add(Visit visit);
}

public interface IInvestigationStore
{
    Task<Investigation?> GetForUpdateAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<Investigation>> ListForVisitAsync(string visitId, CancellationToken ct = default);

    /// <summary>Loaded in one query for a set of visits, so a list response is not N+1.</summary>
    Task<IReadOnlyDictionary<string, List<Investigation>>> ListForVisitsAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default);

    /// <summary>
    /// Per-visit counts for the <c>_count</c> object on the list response. Counts EVERY row —
    /// investigations have no soft delete, and the Node <c>_count</c> applies no filter.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> CountForVisitsAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default);

    void Add(Investigation investigation);
    void Remove(Investigation investigation);
}
