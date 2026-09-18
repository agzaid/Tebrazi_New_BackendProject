using Tebrazi.Prescriptions.Application.Abstractions.Persistence;
using Tebrazi.Prescriptions.Application.ApiModels.Responses;
using Tebrazi.Prescriptions.Application.Services;
using Tebrazi.Prescriptions.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Prescriptions.Application.UseCases;

// ═════════════════════════════════════════════════════════════════════════════
//  The five read endpoints of server/src/routes/prescriptions.js:
//
//      GET /api/prescriptions                      (L31-L89)
//      GET /api/prescriptions/summary              (L144-L159)
//      GET /api/prescriptions/interaction-history  (L165-L184)
//      GET /api/prescriptions/{id}                 (L190-L233)
//      GET /api/prescriptions/{id}/pdf             (L874-L946)
//
//  Facts that hold for all five, stated once here rather than five times:
//
//  * They are READ endpoints, so per the module's house rule each handler injects ICurrentUser
//    rather than taking the caller id on the request record.
//  * NO deletedAt filter anywhere. Not one query in prescriptions.js mentions the column, so a
//    soft-deleted prescription is listed, counted by /summary, fetched by /{id} and printed by
//    /pdf, with its own `deletedAt` visible in the payload. PrescriptionFilter.IsDeleted stays
//    null on every call below — see docs/prescriptions-surface.md §1.
//  * A caller with NO physician profile is answered with a SHAPE, not a 403. GET / returns
//    `200 []` (L41), GET /summary returns `200 {"signed":0,"sent":0,"total":0}` (L148) and
//    GET /interaction-history returns `200 []` (L171), each through an early return that never
//    issues the real query. A port that gated these on a physician role would 403 and break every
//    patient page.
//  * Each route has its OWN named 500 literal, so each handler is wrapped end to end by
//    RxReadPersistence.RunAsync (the Appointments StatusPersistence pattern): Handle delegates to
//    HandleCore, an AppException passes through untouched so the deliberate 404/403 keep their
//    bodies, and anything else is logged the way Node logs it and rethrown as that route's 500.
//
//  THE CACHE IS DELIBERATELY NOT PORTED. GET / is wrapped in cacheMiddleware(15) (L31) and
//  GET /summary in cacheMiddleware(30) (L144) — a plain in-process Map keyed
//  `route:{userId}:{req.originalUrl}` with ZERO invalidation anywhere in prescriptions.js. So in
//  Node the client's own refresh after a create/sign/send/dispense can legitimately return the
//  pre-mutation array for up to 15s and the pre-mutation counts for up to 30s. This port always
//  computes a fresh response: a fresher answer cannot break the client, a stale one can. The two
//  knock-on facts that vanish with it — `?visitId=x` was a separate cache entry from the bare URL,
//  and so were `GET /` and `GET /?` — are not observable in the body. Restated at each affected
//  handler so a reader diffing against Node does not think the caching was missed.
//
//  ROUTE ORDER, for whoever writes the controller (audit-1's hazards): `GET /summary` and
//  `GET /interaction-history` are single-segment LITERALS and MUST be registered before
//  `GET /{id}`, or they bind as id="summary"/id="interaction-history" and answer
//  404 {"error":"Prescription not found"}. The Node source carries the comment "(must be before
//  /:id)" at L162 for exactly this reason. And do NOT introduce a `/{id}/{action}` catch-all:
//  `/{id}/pdf` shares its shape with seven sibling literals in the write groups.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The route-level guard for this file, mapping any unexpected failure onto the route's OWN 500
/// body.
///
/// <para>Each Node handler is wrapped in a try/catch whose only outcome is a named literal —
/// <c>{"error":"Failed to list prescriptions"}</c>, <c>"Failed to get prescription summary"</c>,
/// <c>"Failed to load interaction history"</c>, <c>"Failed to get prescription"</c>,
/// <c>"Failed to generate PDF"</c>. Letting an EF or port exception escape would emit
/// <c>ExceptionHandlingMiddleware</c>'s generic <c>{"error":"Internal Server Error"}</c> instead,
/// and the React client branches on <c>err.response.data.error</c>.</para>
///
/// <para>The Node try covers the WHOLE handler, so a failure in the physician-profile lookup, the
/// cross-module reads or the HTML render all answer the same named 500 as a failure in the
/// prescription query itself. That is why this wraps <c>HandleCore</c> end to end rather than one
/// statement.</para>
///
/// <para>It also reproduces the TypeErrors. Node dereferences <c>prescription.visit</c> on three
/// of these routes; a prescription whose visit cannot be resolved is a
/// <see cref="InvalidOperationException"/> here, which lands on the same logged 500 Node produces.
/// Declared internal to this file with the group's prefix, because the sibling handler files share
/// the namespace and each needs its own log message and literal.</para>
/// </summary>
internal static class RxReadPersistence
{
    /// <summary>
    /// Runs <paramref name="body"/> under the route's catch-all.
    /// </summary>
    /// <typeparam name="THandler">The calling handler, for the logger category.</typeparam>
    /// <typeparam name="TResponse">The route's response type.</typeparam>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="logMessage">The Node log line, verbatim (e.g. "[Prescriptions] List error").</param>
    /// <param name="failureError">The route's own 500 literal, verbatim.</param>
    /// <param name="cancellationToken">
    /// A client disconnect must not be logged and re-thrown as a server error.
    /// </param>
    /// <param name="body">The handler body.</param>
    /// <returns>The handler's response.</returns>
    public static async Task<TResponse> RunAsync<THandler, TResponse>(
        IAppLogger<THandler> logger,
        string logMessage,
        string failureError,
        CancellationToken cancellationToken,
        Func<Task<TResponse>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception exception)
            when (exception is not AppException && !cancellationToken.IsCancellationRequested)
        {
            logger.Error(logMessage, exception);
            throw RxErrors.ServerError(failureError);
        }
    }
}

/// <summary>The caller checks the five read endpoints share.</summary>
internal static class RxReadCaller
{
    /// <summary>
    /// <c>401 {"error":"No token provided"}</c> — the literal <c>authCheck</c> body, which is why
    /// this is a <see cref="BusinessException"/> and not <see cref="UnauthorizedException"/> (that
    /// one emits <c>{"error":"Unauthorized","message":...}</c>, a shape no prescriptions route
    /// produces). Unreachable behind <c>[Authorize]</c>, and kept so a handler cannot silently
    /// read a null user id as a filter value and answer 200 with somebody else's rows.
    /// </summary>
    /// <param name="currentUser">The ambient caller.</param>
    /// <returns>The caller's user id.</returns>
    public static string RequireUserId(ICurrentUser currentUser)
        => string.IsNullOrEmpty(currentUser.UserId)
            ? throw RxErrors.Node("No token provided", 401)
            : currentUser.UserId;

    /// <summary>
    /// The visit behind a prescription, which Node gets for free from its Prisma <c>include</c>.
    ///
    /// <para><c>Prescription.visitId</c> is a required FK, so this can only fail if the row was
    /// deleted underneath us. Node would then dereference <c>prescription.visit.patientUserId</c>
    /// (L216, L906) on <c>undefined</c> and raise a TypeError into the route's catch, so the
    /// faithful port is an exception the file guard turns into that route's logged 500 — never a
    /// 404 and never a half-populated body.</para>
    /// </summary>
    /// <param name="visits">The Visits port.</param>
    /// <param name="prescription">The prescription whose visit is needed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visit summary.</returns>
    public static async Task<VisitSummary> RequireVisitAsync(
        IVisitDirectory visits, Prescription prescription, CancellationToken cancellationToken)
        => await visits.GetAsync(prescription.VisitId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Prescription {prescription.Id} references visit {prescription.VisitId}, which "
                + "could not be resolved through IVisitDirectory.");
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/prescriptions
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The list route's only input.
/// </summary>
/// <param name="VisitId">
/// <c>?visitId=</c>, applied to BOTH branches as exact equality with no uuid validation
/// (prescriptions.js:47). Kept as a raw <c>string?</c> because the Node gate is
/// <c>if (visitId)</c>: a present-but-valueless key binds to <c>""</c>, which is falsy in
/// JavaScript and therefore not a filter at all. <c>RxJs.Truthy</c> restores that below —
/// without it the store's <c>is not null</c> test would filter on the empty id and return
/// nothing where Node returns everything.
/// </param>
public sealed record RxReadListQuery(string? VisitId) : IRequest<IReadOnlyList<object>>;

/// <summary>
/// Port of <c>GET /api/prescriptions</c> (prescriptions.js:31-89). A BARE JSON ARRAY, never an
/// object and never null, ordered <c>createdAt</c> DESC.
///
/// <para><b>The branch key is <c>userType</c>, not the presence of a physician profile.</b> A
/// PHYSICIAN sees every prescription they authored, in every status including DRAFT. Everyone else
/// — PATIENT, RECEPTIONIST and STAFF alike — takes the patient branch and is scoped to their OWN
/// user id, so a receptionist sees their personal prescriptions and in practice an empty array.
/// That looks like a bug and is the contract.</para>
///
/// <para>The two branches return two different element types, because <c>patientName</c> exists
/// only on the physician one, so the response element type is <c>object</c> — the Visits
/// precedent for an endpoint whose variants have incompatible key sets.</para>
///
/// <para>The patient branch also narrows by status to CONFIRMED, SENT and DISPENSED
/// (prescriptions.js:44). <b>SIGNED is excluded</b> even though <c>POST /</c> creates rows as
/// SIGNED, so a freshly written prescription is invisible to the patient until <c>/sign</c>
/// (→ CONFIRMED) or <c>/send</c> (→ SENT) runs.</para>
///
/// <para><b>The cache is not reproduced.</b> Node wraps this route in <c>cacheMiddleware(15)</c>
/// with no invalidation anywhere in the file, so it can serve a pre-mutation array for up to 15
/// seconds; this handler always queries. See the file header.</para>
/// </summary>
public sealed class RxReadListHandler(
    ICurrentUser currentUser,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IVisitDirectory visits,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IAppLogger<RxReadListHandler> logger)
    : IRequestHandler<RxReadListQuery, IReadOnlyList<object>>
{
    /// <summary>
    /// The patient branch's status filter (prescriptions.js:44). A set, so an empty one would
    /// match nothing — it is never empty.
    /// </summary>
    private static readonly PrescriptionStatus[] PatientVisibleStatuses =
    [
        PrescriptionStatus.CONFIRMED,
        PrescriptionStatus.SENT,
        PrescriptionStatus.DISPENSED
    ];

    public Task<IReadOnlyList<object>> Handle(
        RxReadListQuery request, CancellationToken cancellationToken = default)
        => RxReadPersistence.RunAsync(
            logger, "[Prescriptions] List error", "Failed to list prescriptions", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<object>> HandleCore(
        RxReadListQuery request, CancellationToken cancellationToken)
    {
        var userId = RxReadCaller.RequireUserId(currentUser);
        var visitFilter = RxJs.Truthy(request.VisitId);

        if (currentUser.UserType == "PHYSICIAN")
        {
            var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

            // No profile: 200 [] before any prescription query (prescriptions.js:41).
            if (physician is null) return [];

            var authored = await prescriptions.ListAsync(
                new PrescriptionFilter(PhysicianId: physician.Id, VisitId: visitFilter),
                cancellationToken);

            return await PhysicianRowsAsync(authored, cancellationToken);
        }

        // Node filters relationally — `where.visit = { patientUserId }` inside one query
        // (prescriptions.js:43) — which this port cannot do across a module boundary, so the
        // visit ids are resolved first and passed as a set.
        //
        // AN EMPTY SET IS A REAL FILTER that matches nothing, which is exactly right: a patient
        // with no visits must get [], not every prescription in the table.
        var visitIds = await visits.ListVisitIdsForPatientAsync(
            userId, subprofileId: null, cancellationToken);

        var received = await prescriptions.ListAsync(
            new PrescriptionFilter(
                VisitIds: visitIds,
                StatusIn: PatientVisibleStatuses,
                VisitId: visitFilter),
            cancellationToken);

        return await PatientRowsAsync(received, cancellationToken);
    }

    /// <summary>
    /// The physician key set, with <c>patientName</c> appended from ONE batched user lookup —
    /// prescriptions.js:69-79 is itself written as an explicit N+1 fix.
    /// </summary>
    private async Task<IReadOnlyList<object>> PhysicianRowsAsync(
        IReadOnlyList<Prescription> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];

        var relations = await LoadRelationsAsync(rows, cancellationToken);

        // Node de-dupes with a Set and drops falsy ids; patientUserId is a required column, so
        // the filter is a no-op there and is kept here only because a dictionary miss is possible.
        string[] patientIds =
        [
            .. rows.Select(p => relations.Visit(p).PatientUserId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Distinct()
        ];

        var accounts = patientIds.Length == 0
            ? new Dictionary<string, UserSummary>(0)
            : await identity.GetUsersAsync(patientIds, cancellationToken);

        return
        [
            .. rows.Select(p =>
            {
                var visit = relations.Visit(p);

                // `patientMap[...] || undefined` (prescriptions.js:77): an unresolved account —
                // or one whose displayName is "" — DROPS the key entirely rather than emitting
                // null. RxJs.Truthy is the `||` half; the JsonIgnore attribute on the DTO is the
                // `undefined` half.
                var patientName = RxJs.Truthy(
                    accounts.GetValueOrDefault(visit.PatientUserId)?.DisplayName);

                return (object)RxReadMapper.ToPhysicianListItem(
                    p,
                    RxReadMapper.ToListVisit(visit, relations.Clinic(visit)),
                    RxReadMapper.ToListPhysician(relations.Physician(p)),
                    RxReadMapper.ToListSubprofile(relations.Subprofile(p)),
                    patientName);
            })
        ];
    }

    /// <summary>The non-physician key set: the same rows with no <c>patientName</c> key at all.</summary>
    private async Task<IReadOnlyList<object>> PatientRowsAsync(
        IReadOnlyList<Prescription> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];

        var relations = await LoadRelationsAsync(rows, cancellationToken);

        return
        [
            .. rows.Select(p =>
            {
                var visit = relations.Visit(p);

                return (object)RxReadMapper.ToPatientListItem(
                    p,
                    RxReadMapper.ToListVisit(visit, relations.Clinic(visit)),
                    RxReadMapper.ToListPhysician(relations.Physician(p)),
                    RxReadMapper.ToListSubprofile(relations.Subprofile(p)));
            })
        ];
    }

    /// <summary>
    /// Resolves the three included relations for a whole page in one call per port. Node gets them
    /// from a single Prisma <c>include</c>; four batched cross-module reads are the modular-monolith
    /// equivalent, and doing them per row would be three N+1s.
    /// </summary>
    private async Task<RxReadListRelations> LoadRelationsAsync(
        IReadOnlyList<Prescription> rows, CancellationToken cancellationToken)
    {
        string[] visitIds = [.. rows.Select(p => p.VisitId).Distinct()];
        string[] physicianIds = [.. rows.Select(p => p.PhysicianId).Distinct()];
        string[] subprofileIds =
        [
            .. rows.Select(p => p.SubprofileId).Where(id => id is not null).Select(id => id!).Distinct()
        ];

        var visitMap = await visits.GetManyAsync(visitIds, cancellationToken);
        var physicianMap = await identity.GetPhysiciansAsync(physicianIds, cancellationToken);

        var subprofileMap = subprofileIds.Length == 0
            ? new Dictionary<string, SubprofileSummary>(0)
            : await patients.GetSubprofilesAsync(subprofileIds, cancellationToken);

        string[] clinicIds = [.. visitMap.Values.Select(v => v.ClinicId).Distinct()];

        var clinicMap = clinicIds.Length == 0
            ? new Dictionary<string, ClinicSummary>(0)
            : await clinics.GetClinicsAsync(clinicIds, cancellationToken);

        return new RxReadListRelations(visitMap, clinicMap, physicianMap, subprofileMap);
    }
}

/// <summary>
/// Everything one page of <c>GET /api/prescriptions</c> needs to project, resolved once per port.
/// </summary>
/// <param name="Visits">Visit summaries by visit id.</param>
/// <param name="Clinics">Clinic summaries by clinic id.</param>
/// <param name="Physicians">Author profiles by physician-profile id.</param>
/// <param name="Subprofiles">Subprofile summaries by subprofile id.</param>
internal sealed record RxReadListRelations(
    IReadOnlyDictionary<string, VisitSummary> Visits,
    IReadOnlyDictionary<string, ClinicSummary> Clinics,
    IReadOnlyDictionary<string, PhysicianSummary> Physicians,
    IReadOnlyDictionary<string, SubprofileSummary> Subprofiles)
{
    /// <summary>
    /// The row's visit, which the physician branch dereferences for <c>patientUserId</c>. An
    /// unresolvable visit is Node's TypeError, so it becomes this file's 500 rather than a null
    /// <c>visit</c> key — see <see cref="RxReadCaller.RequireVisitAsync"/>.
    /// </summary>
    /// <param name="prescription">The row being projected.</param>
    /// <returns>The visit summary.</returns>
    public VisitSummary Visit(Prescription prescription)
        => Visits.GetValueOrDefault(prescription.VisitId)
            ?? throw new InvalidOperationException(
                $"Prescription {prescription.Id} references visit {prescription.VisitId}, which "
                + "could not be resolved through IVisitDirectory.");

    /// <summary>
    /// The visit's clinic. <c>Visit.clinicId</c> is required, so Node's include can never yield
    /// null here and Node never dereferences it either — an unresolved clinic is emitted as null.
    /// </summary>
    /// <param name="visit">The row's visit.</param>
    /// <returns>The clinic summary, or null.</returns>
    public ClinicSummary? Clinic(VisitSummary visit) => Clinics.GetValueOrDefault(visit.ClinicId);

    /// <summary>The prescription's AUTHOR, not the caller.</summary>
    /// <param name="prescription">The row being projected.</param>
    /// <returns>The author's profile, or null when it could not be resolved.</returns>
    public PhysicianSummary? Physician(Prescription prescription)
        => Physicians.GetValueOrDefault(prescription.PhysicianId);

    /// <summary>The dependant the prescription was written for, if any.</summary>
    /// <param name="prescription">The row being projected.</param>
    /// <returns>The subprofile summary, or null.</returns>
    public SubprofileSummary? Subprofile(Prescription prescription)
        => prescription.SubprofileId is null
            ? null
            : Subprofiles.GetValueOrDefault(prescription.SubprofileId);
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/prescriptions/summary
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>No input at all — the counts are scoped purely by the caller's physician profile.</summary>
public sealed record RxReadSummaryQuery : IRequest<RxReadSummaryResponse>;

/// <summary>
/// Port of <c>GET /api/prescriptions/summary</c> (prescriptions.js:144-159), the dashboard badge.
///
/// <para><b>There is no <c>userType</c> check.</b> A caller with no physician profile gets
/// <c>200 {"signed":0,"sent":0,"total":0}</c> with LITERAL zeros and no query issued
/// (prescriptions.js:148) — never a 403, which every patient page depends on.</para>
///
/// <para><b>The <c>signed</c> bucket spans TWO statuses</b>, CONFIRMED and SIGNED
/// (prescriptions.js:151), because creation writes SIGNED while <c>/sign</c> writes CONFIRMED.
/// <c>total</c> counts every status, DRAFT and DISPENSED included, so <c>signed + sent</c> does
/// not equal <c>total</c>.</para>
///
/// <para>Node issues three separate COUNT queries in a <c>Promise.all</c> with no shared snapshot,
/// so its three numbers can be mutually inconsistent under concurrent writes. This port derives
/// all three from ONE <c>GROUP BY</c>, which is internally consistent — strictly better, and
/// invisible to the client. No <c>deletedAt</c> filter, so soft-deleted rows are counted in all
/// three.</para>
///
/// <para><b>The cache is not reproduced.</b> Node wraps this route in <c>cacheMiddleware(30)</c>
/// with no invalidation, so its badge can lag the list by up to 30 seconds; this handler always
/// counts. See the file header.</para>
/// </summary>
public sealed class RxReadSummaryHandler(
    ICurrentUser currentUser,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IAppLogger<RxReadSummaryHandler> logger)
    : IRequestHandler<RxReadSummaryQuery, RxReadSummaryResponse>
{
    public Task<RxReadSummaryResponse> Handle(
        RxReadSummaryQuery request, CancellationToken cancellationToken = default)
        => RxReadPersistence.RunAsync(
            logger, "[Prescriptions] Summary error", "Failed to get prescription summary",
            cancellationToken, () => HandleCore(cancellationToken));

    private async Task<RxReadSummaryResponse> HandleCore(CancellationToken cancellationToken)
    {
        var userId = RxReadCaller.RequireUserId(currentUser);

        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);
        if (physician is null) return new RxReadSummaryResponse(0, 0, 0);

        var counts = await prescriptions.CountByStatusAsync(physician.Id, cancellationToken);

        // A status with no rows is ABSENT from the dictionary, not present as 0.
        var signed = counts.GetValueOrDefault(nameof(PrescriptionStatus.CONFIRMED))
            + counts.GetValueOrDefault(nameof(PrescriptionStatus.SIGNED));

        var sent = counts.GetValueOrDefault(nameof(PrescriptionStatus.SENT));

        // Every status, which is why this is a sum of the whole dictionary and not signed + sent.
        var total = counts.Values.Sum();

        return new RxReadSummaryResponse(signed, sent, total);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/prescriptions/interaction-history
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The alert-history input.
/// </summary>
/// <param name="Limit">
/// <c>?limit=</c>, RAW. The Node rule is <c>parseInt(req.query.limit) || 20</c>
/// (prescriptions.js:168) with <b>no <c>Math.max</c>, no cap, and a meaningful negative</b>, so it
/// is emphatically NOT <c>PageRequest.Parse</c> and emphatically not an <c>int?</c> binding —
/// either would diverge on <c>?limit=0</c>, <c>?limit=abc</c>, <c>?limit=1e6</c> and every
/// negative value. See the handler.
/// </param>
public sealed record RxReadInteractionHistoryQuery(string? Limit)
    : IRequest<IReadOnlyList<RxReadInteractionHistoryItem>>;

/// <summary>
/// Port of <c>GET /api/prescriptions/interaction-history</c> (prescriptions.js:165-184) — the ONLY
/// reader of <c>InteractionAlert</c> anywhere. Not called by the React client; ported for parity
/// so the entity is not write-only dead weight (audit-1, missedRoutes).
///
/// <para>A BARE JSON ARRAY of raw rows with no reshaping, ordered <c>checkedAt</c> DESC. Filters on
/// <c>physicianId</c> alone: a caller with no physician profile gets <c>200 []</c> without a query
/// (prescriptions.js:171), even though patient-authored rows exist in the table.</para>
///
/// <para><b>Route order.</b> This is a single-segment literal registered before <c>GET /:id</c>,
/// and the Node source carries the comment "(must be before /:id)". Registered after it, the
/// controller would answer <c>404 {"error":"Prescription not found"}</c> for
/// <c>id="interaction-history"</c>.</para>
/// </summary>
public sealed class RxReadInteractionHistoryHandler(
    ICurrentUser currentUser,
    IInteractionAlertStore alerts,
    IIdentityDirectory identity,
    IAppLogger<RxReadInteractionHistoryHandler> logger)
    : IRequestHandler<RxReadInteractionHistoryQuery, IReadOnlyList<RxReadInteractionHistoryItem>>
{
    public Task<IReadOnlyList<RxReadInteractionHistoryItem>> Handle(
        RxReadInteractionHistoryQuery request, CancellationToken cancellationToken = default)
        => RxReadPersistence.RunAsync(
            logger, "[Prescriptions] Interaction history error", "Failed to load interaction history",
            cancellationToken, () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<RxReadInteractionHistoryItem>> HandleCore(
        RxReadInteractionHistoryQuery request, CancellationToken cancellationToken)
    {
        var userId = RxReadCaller.RequireUserId(currentUser);

        // `parseInt(req.query.limit) || 20` (prescriptions.js:168), reproduced exactly:
        //   ?limit=50     -> 50            ?limit=abc -> NaN, falsy  -> 20
        //   ?limit=50abc  -> 50            ?limit=0   -> 0, falsy    -> 20
        //   ?limit=1e6    -> 1 (parseInt stops at the 'e')
        //   ?limit=0x10   -> 16. `parseInt` is called with NO RADIX, so V8 auto-detects the
        //                   `0x`/`0X` prefix as base 16. A digit-prefix-only scanner would read
        //                   "0", coerce to falsy and answer 20 — a real divergence, which is why
        //                   RxJs.ParseIntNumber handles the prefix.
        //   ?limit=-5     -> -5, PASSED THROUGH: Prisma reads a negative take as reverse-take,
        //                   i.e. the last five of the checkedAt-desc ordering — the five OLDEST
        //                   alerts, still newest-first among themselves. The store reproduces
        //                   that; a clamp here would break it.
        //
        // ParseIntNumber, not ParseInt: a value outside Int32 (`?limit=100000000000`) is a finite,
        // TRUTHY Number in Node and is handed to Prisma's `take` as-is, which returns effectively
        // everything rather than falling back to 20. Only a genuine NaN takes the `|| 20` branch,
        // so the two are kept apart and the out-of-range value SATURATES instead. int.MaxValue is
        // wire-identical for any real alert history, and the store already reads int.MinValue as
        // "the whole tail, reversed".
        var parsed = RxJs.ParseIntNumber(request.Limit);
        var take = parsed is null or 0
            ? 20
            : parsed.Value >= int.MaxValue
                ? int.MaxValue
                : parsed.Value <= int.MinValue
                    ? int.MinValue
                    : (int)parsed.Value;

        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

        // No profile — i.e. every patient: 200 [] with no query (prescriptions.js:171).
        if (physician is null) return [];

        var history = await alerts.ListForPhysicianAsync(physician.Id, take, cancellationToken);

        return [.. history.Select(RxReadMapper.ToInteractionHistoryItem)];
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/prescriptions/{id}
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>One prescription in full.</summary>
/// <param name="PrescriptionId">The raw <c>:id</c> route segment, unvalidated.</param>
public sealed record RxReadDetailQuery(string PrescriptionId) : IRequest<RxReadDetailResponse>;

/// <summary>
/// Port of <c>GET /api/prescriptions/{id}</c> (prescriptions.js:190-233). Not called by the React
/// client — every client call is one of the <c>/{id}/...</c> sub-routes — and ported for parity
/// because of the <c>isPhysician</c> quirk (audit-1, missedRoutes).
///
/// <para><b><c>isPhysician</c> serializes as <c>null</c>, not <c>false</c>, for a caller with no
/// physician profile.</b> Node's <c>physician &amp;&amp; prescription.physicianId === physician.id</c>
/// (L215) is the truthy-AND RESULT, so a patient reading their own prescription receives
/// <c>"isPhysician": null</c>. It is <c>false</c> only for a caller who HAS a profile that did not
/// author this row — which, past the 403, means the visit's patient who is also a physician. The
/// <c>null</c> still gates correctly, because the guard is <c>!isPhysician &amp;&amp; !isPatient</c>.</para>
///
/// <para>Order of operations, preserved: the prescription is read first, so a 404 wins over the
/// access check; then the caller's own profile; then the 403; and only then the patient's user
/// row. Access is the AUTHORING PHYSICIAN or the visit's patient and nobody else — no clinic
/// standing is consulted, so a colleague at the same clinic gets 403.</para>
///
/// <para>No <c>deletedAt</c> filter: a soft-deleted prescription is returned in full.</para>
/// </summary>
public sealed class RxReadDetailHandler(
    ICurrentUser currentUser,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IVisitDirectory visits,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IAppLogger<RxReadDetailHandler> logger)
    : IRequestHandler<RxReadDetailQuery, RxReadDetailResponse>
{
    public Task<RxReadDetailResponse> Handle(
        RxReadDetailQuery request, CancellationToken cancellationToken = default)
        => RxReadPersistence.RunAsync(
            logger, "[Prescriptions] Get error", "Failed to get prescription", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxReadDetailResponse> HandleCore(
        RxReadDetailQuery request, CancellationToken cancellationToken)
    {
        var userId = RxReadCaller.RequireUserId(currentUser);

        var prescription = await prescriptions.GetAsync(request.PrescriptionId, cancellationToken)
            ?? throw RxErrors.NotFound("Prescription not found");

        var visit = await RxReadCaller.RequireVisitAsync(visits, prescription, cancellationToken);

        // The CALLER's own profile, which is a different lookup from the prescription's author.
        var callerPhysician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

        // null — NOT false — when the caller holds no profile. That value is on the wire.
        bool? isPhysician = callerPhysician is null
            ? null
            : prescription.PhysicianId == callerPhysician.Id;

        var isPatient = visit.PatientUserId == userId;

        if (isPhysician != true && !isPatient) throw RxErrors.Forbidden("Access denied");

        var patientUser = await identity.GetUserAsync(visit.PatientUserId, cancellationToken);

        // The AUTHOR's profile for the embedded `physician` object. When the caller is the author
        // it is the same row, so the second query is skipped — Node reads it twice (once as the
        // include, once for the access check) and both reads see identical values.
        var author = isPhysician == true
            ? callerPhysician
            : await identity.GetPhysicianAsync(prescription.PhysicianId, cancellationToken);

        // `physician.user` selects displayName AND email, and PhysicianSummary carries no email.
        var authorUser = author is null
            ? null
            : await identity.GetUserAsync(author.UserId, cancellationToken);

        var clinic = await clinics.GetClinicAsync(visit.ClinicId, cancellationToken);

        var subprofile = prescription.SubprofileId is null
            ? null
            : await patients.GetSubprofileAsync(prescription.SubprofileId, cancellationToken);

        return RxReadMapper.ToDetail(
            prescription,
            RxReadMapper.ToDetailVisit(visit, clinic),
            RxReadMapper.ToDetailPhysician(author, authorUser),
            RxReadMapper.ToDetailSubprofile(subprofile),
            RxReadMapper.ToDetailPatientUser(patientUser),
            isPhysician);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/prescriptions/{id}/pdf
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>The Rx document request.</summary>
/// <param name="PrescriptionId">The raw <c>:id</c> route segment, unvalidated.</param>
public sealed record RxReadPdfQuery(string PrescriptionId) : IRequest<RxReadPdfResponse>;

/// <summary>
/// Port of <c>GET /api/prescriptions/{id}/pdf</c> (prescriptions.js:874-946).
///
/// <para><b>It returns HTML, not a PDF</b> — <c>Content-Type: text/html; charset=utf-8</c>, no PDF
/// bytes, no <c>Content-Disposition</c>. The handler produces the string through
/// <see cref="RxDocumentRenderer.RenderHtml"/> and hands it back in
/// <see cref="RxReadPdfResponse"/>; setting the content type is the controller's job and
/// <see cref="RxReadPdfResponse.ContentType"/> is the exact value to use. Success is HTML while
/// EVERY error stays JSON, because Node's <c>setHeader</c> runs only on the happy path.</para>
///
/// <para>Order of operations, preserved: the prescription is fetched BEFORE the authorization
/// check (a timing side channel, and the order the source has), then the caller's profile, then
/// the 403, and only then the patient-name resolution.</para>
///
/// <para><b>The patient-name precedence is subprofile FIRST</b> (prescriptions.js:925-936):
/// <c>"{name} ({RELATION})"</c> with the RAW uppercase relation, which wins even when a clinic
/// chart exists; then <c>visit.clinicPatient?.name</c>; then the platform account's
/// <c>displayName</c>; then the literal <c>"Patient"</c>. The account lookup is reached ONLY when
/// the first two miss, so it is issued lazily here too.</para>
///
/// <para>No <c>deletedAt</c> filter: a soft-deleted prescription still renders a full document.
/// Nothing is written — <c>pdfUrl</c> is never stored.</para>
/// </summary>
public sealed class RxReadPdfHandler(
    ICurrentUser currentUser,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IVisitDirectory visits,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IClinicPatientDirectory clinicPatients,
    IAppLogger<RxReadPdfHandler> logger)
    : IRequestHandler<RxReadPdfQuery, RxReadPdfResponse>
{
    public Task<RxReadPdfResponse> Handle(
        RxReadPdfQuery request, CancellationToken cancellationToken = default)
        => RxReadPersistence.RunAsync(
            logger, "[Prescriptions] PDF error", "Failed to generate PDF", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxReadPdfResponse> HandleCore(
        RxReadPdfQuery request, CancellationToken cancellationToken)
    {
        var userId = RxReadCaller.RequireUserId(currentUser);

        var prescription = await prescriptions.GetAsync(request.PrescriptionId, cancellationToken)
            ?? throw RxErrors.NotFound("Prescription not found");

        var visit = await RxReadCaller.RequireVisitAsync(visits, prescription, cancellationToken);

        var callerPhysician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);
        var isPhysician = callerPhysician is not null
            && prescription.PhysicianId == callerPhysician.Id;
        var isPatient = visit.PatientUserId == userId;

        if (!isPhysician && !isPatient) throw RxErrors.Forbidden("Access denied");

        var patientName = await ResolvePatientNameAsync(prescription, visit, cancellationToken);

        // Node dereferences `prescription.physician.user.displayName` (L954) — an unresolvable
        // author is its TypeError, so it becomes this route's logged 500 rather than a document
        // with a blank letterhead.
        var author = isPhysician && callerPhysician is not null
            ? callerPhysician
            : await identity.GetPhysicianAsync(prescription.PhysicianId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Prescription {prescription.Id} references physician profile "
                    + $"{prescription.PhysicianId}, which could not be resolved through "
                    + "IIdentityDirectory.");

        var clinic = await clinics.GetClinicAsync(visit.ClinicId, cancellationToken);

        // The renderer performs no lookups of its own and holds every literal, the en-GB date
        // format, the escapeHtml entity set and the em-dash cell fallbacks. `notes` is passed
        // through verbatim: the renderer omits the block when it is empty.
        var document = new RxDocument(
            PrescriptionId: prescription.Id,
            SignedAt: prescription.SignedAt,
            CreatedAt: prescription.CreatedAt,
            Notes: prescription.Notes,
            ClinicName: clinic?.Name,
            ClinicAddress: clinic?.Address,
            ClinicCity: clinic?.City,
            ClinicPhone: clinic?.Phone,
            ClinicLogo: clinic?.Logo,
            PhysicianName: author.DisplayName,
            PhysicianSpecialty: author.Specialty,
            PhysicianLicenseNumber: author.LicenseNumber,
            PatientName: patientName,
            // `Array.isArray(medications) ? medications : []` — a non-array Json value renders an
            // empty table body, never an error, and a medication carrying stoppedAt still prints.
            Medications: RxDocumentRenderer.ParseMedications(prescription.Medications));

        return new RxReadPdfResponse(RxDocumentRenderer.RenderHtml(document));
    }

    /// <summary>
    /// The three-tier fallback at prescriptions.js:925-936, in its exact precedence, with each
    /// tier gated on JavaScript truthiness so an empty stored name falls through rather than
    /// printing a blank.
    /// </summary>
    private async Task<string> ResolvePatientNameAsync(
        Prescription prescription, VisitSummary visit, CancellationToken cancellationToken)
    {
        // Tier 1 — the dependant, tested on the RESOLVED relation (`if (prescription.subprofile)`),
        // so a dangling subprofile id behaves like no subprofile at all. The relation is emitted
        // raw and uppercase: "Ahmed (CHILD)".
        if (prescription.SubprofileId is not null)
        {
            var subprofile = await patients.GetSubprofileAsync(
                prescription.SubprofileId, cancellationToken);

            if (subprofile is not null) return $"{subprofile.Name} ({subprofile.Relation})";
        }

        // Tier 2 — the clinic-owned chart. `visit.clinicPatient?.name` is a truthiness test, so an
        // empty name continues to tier 3.
        if (visit.ClinicPatientId is not null)
        {
            var chart = await clinicPatients.GetAsync(visit.ClinicPatientId, cancellationToken);

            if (RxJs.Truthy(chart?.Name) is { } chartName) return chartName;
        }

        // Tier 3 — the platform account, then the literal fallback. Node reaches this query only
        // here, which is why it is not hoisted above.
        var account = await identity.GetUserAsync(visit.PatientUserId, cancellationToken);

        return RxJs.Truthy(account?.DisplayName) ?? "Patient";
    }
}
