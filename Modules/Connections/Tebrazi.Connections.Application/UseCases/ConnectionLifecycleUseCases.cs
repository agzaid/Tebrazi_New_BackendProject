using System.Text.Json;
using Tebrazi.Connections.Application.Abstractions.Persistence;
using Tebrazi.Connections.Application.ApiModels.Responses;
using Tebrazi.Connections.Application.Services;
using Tebrazi.Connections.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Connections.Application.UseCases;

// ═════════════════════════════════════════════════════════════════════════════
//  The LINK LIFECYCLE group of server/src/routes/connections.js — the edge itself, from list to
//  request to accept/reject to removal, plus the physician dashboard's roll-up:
//
//      GET    /api/connections                   (L31-L114)    -> 200, a BARE ARRAY
//      POST   /api/connections/request           (L131-L226)   -> 201 create | 200 re-send
//      PUT    /api/connections/{id}/accept       (L240-L271)   -> 200
//      PUT    /api/connections/{id}/reject       (L277-L296)   -> 200
//      DELETE /api/connections/{id}              (L401-L433)   -> 200
//      GET    /api/connections/patient-summaries (L1042-L1131) -> 200, an OBJECT keyed by patient id
//
//  Facts that hold across the group, stated once:
//
//  * GET / and GET /patient-summaries are READS, so per the module's house rule they inject
//    ICurrentUser. The four writes take CallerUserId (and, where Node reads it, CallerUserType) on
//    the command and inject no caller context.
//  * NO soft-delete filter anywhere, because there is no column. `deletedAt` does not occur in the
//    1,818 lines of connections.js and `model DoctorPatientConnection` (schema.prisma:507-530)
//    declares no such field. DELETE /{id} is a HARD prisma.delete (:427).
//  * Each route has its OWN named 500 literal, so each handler is wrapped end to end by
//    ConnLinkPersistence.RunAsync: Handle delegates to HandleCore, an AppException passes through
//    untouched so the deliberate 400/403/404/409 keep their bodies, and anything else is logged
//    the way Node logs it and rethrown as that route's own 500. The generic middleware body
//    ({"error":"Internal Server Error"}) reproduces none of the nineteen literals, and the React
//    client branches on err.response.data.error.
//  * EVERY error body in connections.js is a bare {"error":"…"}. There is no `message` key and no
//    `details` anywhere in the file, which is why this group uses ConnErrors throughout and never
//    ValidationException or ConflictException — both prepend a generic label and push the real
//    text into a second key the client does not read.
//
//  THE CACHE IS DELIBERATELY NOT PORTED. GET / is wrapped in cacheMiddleware(15) (L31) — the only
//  cached route in the file, and there is NO invalidation anywhere in it, so live Node can return
//  the pre-mutation array for up to 15 seconds after every accept, reject, request or delete in
//  this very group. This handler always queries: a fresher response cannot break the client, a
//  stale one can. Recorded in PORT-STATUS.md and in docs/connections-surface.md §10.11.
//
//  FOR WHOEVER WRITES THE CONTROLLER — the route-order and binding hazards of this group:
//
//  * `GET /patient-summaries` is a single-segment LITERAL and MUST be registered before any
//    `GET /{id}` route. There is no `GET /{id}` in Connections today, but `DELETE /{id}` already
//    exists and the module has eight more literal segments (`/search`, `/clinic-patients`,
//    `/invite-link`, `/qr-code`, …) sharing that shape. Declare every literal first, and do NOT
//    introduce a `/{id}/{action}` catch-all: it would swallow `/{id}/accept` and `/{id}/reject`.
//  * `POST /request` binds an OPTIONAL body. Use
//    [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnLinkRequestBody? — `[FromBody] X?`
//    alone does NOT make a body optional and answers 400 {"error":"Validation failed"} for a
//    Content-Length: 0 request, which is a body this router never produces.
//  * `PUT /{id}/accept` and `PUT /{id}/reject` take NO body at all. Node reads only req.params.id.
//    The client calls them through axios with no data, so axios drops Content-Type entirely; bind
//    nothing rather than an empty body record, or the action 415s.
//  * X-Clinic-Id: GET / is the ONE route in the whole module where the HEADER wins over
//    ?clinicId= (L37), which is exactly IClinicContext's precedence, so that handler injects it.
//    POST /request is body-first (L143: `clinicId || req.headers['x-clinic-id']`) and does NOT
//    consult the query string at all, so its handler takes the header explicitly on the command —
//    read Request.Headers["X-Clinic-Id"] in the controller and pass it through. Using
//    IClinicContext there would silently add `?clinicId=` as a third source Node never reads.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The route-level guard for this file, mapping any unexpected failure onto the route's OWN 500
/// body.
///
/// <para>Each Node handler in this group is wrapped in a try/catch whose only outcome is a named
/// literal — <c>{"error":"Failed to list connections"}</c>,
/// <c>"Failed to send connection request"</c>, <c>"Failed to accept connection"</c>,
/// <c>"Failed to reject connection"</c>, <c>"Failed to remove connection"</c>,
/// <c>"Failed to load summaries"</c>. Letting an EF or port exception escape would emit
/// <c>ExceptionHandlingMiddleware</c>'s generic body instead.</para>
///
/// <para>The Node <c>try</c> covers the WHOLE handler, so a failure in the staff resolution, in a
/// cross-module read or in the response projection answers the same named 500 as a failure in the
/// connection query itself. That is why this wraps <c>HandleCore</c> end to end rather than one
/// statement.</para>
///
/// <para>It also reproduces the TypeErrors and the Prisma validation errors. <c>GET /</c> passes
/// <c>?status=</c> straight into Prisma unvalidated (:40), so an unrecognised value is a Prisma
/// validation error and therefore this route's 500 — not a 400 and not "no filter". Those arrive
/// here as an <see cref="InvalidOperationException"/> and land on the same logged 500 Node
/// produces.</para>
///
/// <para>Declared internal to this file with the group's prefix, because the four sibling handler
/// files share this namespace and each needs its own log message and literal.</para>
/// </summary>
internal static class ConnLinkPersistence
{
    /// <summary>
    /// Runs <paramref name="body"/> under the route's catch-all.
    /// </summary>
    /// <typeparam name="THandler">The calling handler, for the logger category.</typeparam>
    /// <typeparam name="TResponse">The route's response type.</typeparam>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="logMessage">The Node log line, verbatim (e.g. "[Connections] List error").</param>
    /// <param name="failureError">The route's own 500 literal, verbatim.</param>
    /// <param name="cancellationToken">
    /// A client disconnect must not be logged and rethrown as a server error.
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
            throw ConnErrors.ServerError(failureError);
        }
    }
}

/// <summary>
/// The small pieces the six handlers share: the caller gate, the <c>?status=</c> coercion, and the
/// two nested-object projections that recur across the branches of <c>GET /</c>.
/// </summary>
internal static class ConnLinkCommon
{
    /// <summary>
    /// <c>401 {"error":"No token provided"}</c> — the literal <c>authCheck</c> body
    /// (server/src/middleware/auth.js:12), which is why this is a
    /// <see cref="BusinessException"/> and not <see cref="UnauthorizedException"/>: that one emits
    /// <c>{"error":"Unauthorized","message":…}</c>, a shape no connections route produces.
    /// Unreachable behind <c>[Authorize]</c>, and kept so a handler cannot silently read a null
    /// user id as a filter value and answer 200 with somebody else's rows.
    /// </summary>
    /// <param name="currentUser">The ambient caller.</param>
    /// <returns>The caller's user id.</returns>
    public static string RequireUserId(ICurrentUser currentUser)
        => string.IsNullOrEmpty(currentUser.UserId)
            ? throw ConnErrors.Node("No token provided", 401)
            : currentUser.UserId;

    /// <summary>
    /// <c>if (statusFilter) where.status = statusFilter</c> (connections.js:39-40) —
    /// <b>passed RAW to Prisma, never validated</b>.
    ///
    /// <para>Two behaviours have to survive. <c>?status=</c> with no value binds to <c>""</c>,
    /// which is falsy in JavaScript and applies NO filter at all; an <c>is not null</c> store test
    /// would otherwise match the empty string and return nothing. And an unrecognised value such
    /// as <c>?status=banana</c> is a Prisma validation error inside the route's own try, so the
    /// wire answer is <c>500 {"error":"Failed to list connections"}</c> — not a 400, and not
    /// "ignore the filter". Throwing <see cref="InvalidOperationException"/> rather than the
    /// <see cref="BusinessException"/> directly means the file guard also writes the log line Node
    /// writes, and the rendered body is identical either way.</para>
    ///
    /// <para>Parsed case-SENSITIVELY, because Prisma's enum values are case-sensitive:
    /// <c>?status=accepted</c> is a Prisma error in Node, so it must be a 500 here too rather than
    /// a silent match on ACCEPTED.</para>
    /// </summary>
    /// <param name="raw">The raw <c>?status=</c> value as the model binder produced it.</param>
    /// <returns>The parsed status, or null for "no filter".</returns>
    public static ConnectionStatus? ParseStatusFilter(string? raw)
    {
        if (ConnJs.Truthy(raw) is not { } value) return null;

        // The second test rejects what TryParse accepts and Prisma does not: a NUMERIC string
        // ("?status=0" would otherwise become PENDING) and a comma-separated list. Comparing the
        // round-tripped NAME byte for byte leaves only the four literal member names standing.
        if (!Enum.TryParse<ConnectionStatus>(value, ignoreCase: false, out var parsed)
            || !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"?status={value} is not a ConnectionStatus. Node passes this value straight to "
                + "Prisma, which rejects it, so the route answers its own 500.");
        }

        return parsed;
    }

    /// <summary>
    /// The account behind one end of an edge. Both FKs are required columns with a Prisma
    /// <c>include</c>, so Node always has the row; a miss here means the user was hard-deleted
    /// underneath us, which is exactly the case the schema's cascade warning is about. Node would
    /// dereference <c>undefined</c> and raise a TypeError into the route's catch, so the faithful
    /// port is an exception the file guard turns into that route's logged 500 — never a 404 and
    /// never a half-populated body.
    /// </summary>
    /// <param name="accounts">The batched lookup result.</param>
    /// <param name="userId">The id to resolve.</param>
    /// <returns>The account.</returns>
    public static UserSummary RequireAccount(
        IReadOnlyDictionary<string, UserSummary> accounts, string userId)
        => accounts.GetValueOrDefault(userId)
            ?? throw new InvalidOperationException(
                $"doctor_patient_connections references user {userId}, which could not be "
                + "resolved through IIdentityDirectory.");
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/connections
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The list route's two query inputs. The third input, the clinic id, is NOT here: this is the one
/// route in the module whose precedence is <c>header || query.clinicId</c> (connections.js:37),
/// which is precisely <see cref="IClinicContext"/>'s, so the handler injects it.
/// </summary>
/// <param name="Status">
/// <c>?status=</c>, raw. See <see cref="ConnLinkCommon.ParseStatusFilter"/> — a present-but-empty
/// key is no filter, and an unrecognised value is this route's 500.
/// </param>
/// <param name="Search">
/// <c>?search=</c>, raw. <b>It is applied on the STAFF branch ONLY</b> (connections.js:58-62); the
/// physician and patient branches ignore it completely, so a doctor filtering their own patient
/// list by name gets the unfiltered list back. Node trims it first
/// (<c>req.query.search?.trim()</c>, :36) and then tests truthiness, so a whitespace-only value is
/// no filter.
/// </param>
public sealed record ConnLinkListQuery(string? Status, string? Search)
    : IRequest<IReadOnlyList<object>>;

/// <summary>
/// Port of <c>GET /api/connections</c> (connections.js:31-114). A BARE JSON ARRAY, never an object
/// and never null, ordered <c>createdAt</c> DESC on all three branches.
///
/// <para><b>Three branches, two shapes, and the shapes are not interchangeable.</b> A physician
/// sees their patients; a patient sees their doctors; a staff member holding an
/// <c>X-Clinic-Id</c> sees the clinic physician's patients. The staff and physician branches emit
/// <see cref="ConnLinkListPhysicianViewItem"/>; the patient branch emits
/// <see cref="ConnLinkListPatientViewItem"/>, whose <c>subprofile</c> is NARROWER by two keys.
/// That is why the element type is <c>object</c> — the Visits and Prescriptions precedent for a
/// route whose variants have incompatible key sets.</para>
///
/// <para><b>Three differences between the branches that are easy to miss:</b></para>
/// <list type="bullet">
/// <item>Only the PHYSICIAN branch filters <c>subprofileId: null</c> (:82). The staff branch does
/// not, so a receptionist sees dependant edges the doctor's own "My Patients" list hides.</item>
/// <item>Only the STAFF branch honours <c>?search=</c>, and it does so as a relation filter on the
/// patient's <c>displayName</c> (<c>contains</c>, case-insensitive). That is ported by resolving
/// the matching user ids first and passing them as <c>PatientUserIdIn</c>; an EMPTY set is a real
/// filter meaning "nobody matched" and returns <c>[]</c>, not the whole book.</item>
/// <item>The patient branch is the <c>else</c>, not a PATIENT test: RECEPTIONIST, STAFF and an
/// unknown <c>userType</c> all land there once the staff fallback declines.</item>
/// </list>
///
/// <para><b>The staff fallback FALLS THROUGH on every failure here</b> (:45-78) — no clinic
/// header, not active staff, no such clinic, or a clinic whose physician cannot be resolved all
/// continue into the <c>userType</c> branches, and a staff caller then sees their own (empty)
/// patient list. It is one of seven spellings of this fallback in the file and one of seven
/// different failure behaviours; see docs/connections-surface.md §10.1.</para>
///
/// <para><b>The cache is not reproduced.</b> See the file header.</para>
/// </summary>
public sealed class ConnLinkListHandler(
    ICurrentUser currentUser,
    IClinicContext clinicContext,
    IDoctorPatientConnectionStore connections,
    IIdentityDirectory identity,
    IPatientDirectory patients,
    ConnStaffResolver staffResolver,
    IAppLogger<ConnLinkListHandler> logger)
    : IRequestHandler<ConnLinkListQuery, IReadOnlyList<object>>
{
    private const string LogMessage = "[Connections] List error";
    private const string FailureError = "Failed to list connections";

    public Task<IReadOnlyList<object>> Handle(
        ConnLinkListQuery request, CancellationToken cancellationToken = default)
        => ConnLinkPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<object>> HandleCore(
        ConnLinkListQuery request, CancellationToken cancellationToken)
    {
        var userId = ConnLinkCommon.RequireUserId(currentUser);
        var status = ConnLinkCommon.ParseStatusFilter(request.Status);

        // `req.query.search?.trim()` then truthiness (:36) — whitespace-only is no filter.
        var search = ConnJs.TrimToNull(request.Search);

        // `req.headers['x-clinic-id'] || req.query.clinicId` (:37). HEADER FIRST, and this is the
        // only route in the module where that is the precedence.
        var clinicId = ConnJs.Truthy(clinicContext.ClinicId);

        // ── Staff context: the clinic physician's patients (:43-78) ──────────
        //
        // The userType test is a string comparison against "PHYSICIAN" and NOT a parse into the
        // UserType enum: the claim can carry a value the enum does not have, and RECEPTIONIST,
        // STAFF, PATIENT and an unknown value must all take this same path.
        if (currentUser.UserType != "PHYSICIAN" && clinicId is not null)
        {
            var resolution = await staffResolver.ResolveAsync(userId, clinicId, cancellationToken);

            // Every failure FALLS THROUGH to the userType branches below. No 403, no 404.
            if (resolution.IsResolved)
            {
                var staffRows = await connections.ListAsync(
                    new ConnectionFilter(
                        PhysicianUserId: resolution.PhysicianUserId,
                        Status: status,
                        PatientUserIdIn: await MatchingPatientIdsAsync(search, cancellationToken)),
                    cancellationToken);

                return await PhysicianViewAsync(staffRows, cancellationToken);
            }
        }

        // ── Physician: my patients, PARENT EDGES ONLY (:80-91) ───────────────
        if (currentUser.UserType == "PHYSICIAN")
        {
            var ownRows = await connections.ListAsync(
                new ConnectionFilter(
                    PhysicianUserId: userId,
                    Status: status,
                    SubprofileIdIsNull: true),
                cancellationToken);

            return await PhysicianViewAsync(ownRows, cancellationToken);
        }

        // ── Everyone else: my doctors, INCLUDING dependant edges (:93-111) ───
        var doctorRows = await connections.ListAsync(
            new ConnectionFilter(PatientUserId: userId, Status: status),
            cancellationToken);

        return await PatientViewAsync(doctorRows, cancellationToken);
    }

    /// <summary>
    /// The staff branch's <c>patientUser: { displayName: { contains, mode: 'insensitive' } }</c>
    /// (connections.js:58-62), which Node expresses as a relation filter inside the one connection
    /// query. <c>users</c> lives in the Identity context, so the ids are resolved first.
    ///
    /// <para>Returns null when there is no search term, meaning "no predicate". Returns a possibly
    /// EMPTY collection when there is one — and an empty collection is a real filter that matches
    /// nothing, which is right: a term that matched nobody must yield <c>[]</c>, not the
    /// physician's entire patient book.</para>
    ///
    /// <para>The filter is <c>new UserSearchFilter(search)</c> and nothing else: no
    /// <c>userType</c> narrowing, no <c>active</c> gate and NO take, because Node's relation filter
    /// has none of those either. Adding <c>Take</c> here would silently cap a clinic's search.</para>
    /// </summary>
    /// <param name="search">The trimmed, truthy search term, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching user ids, or null for "do not filter".</returns>
    private async Task<IReadOnlyCollection<string>?> MatchingPatientIdsAsync(
        string? search, CancellationToken cancellationToken)
    {
        if (search is null) return null;

        var matches = await identity.SearchUsersAsync(
            new UserSearchFilter(search), cancellationToken);

        return [.. matches.Select(m => m.Id).Distinct()];
    }

    /// <summary>
    /// The staff and physician element shape: the nine scalars plus
    /// <c>patientUser{id,displayName,email,phone}</c> and the WIDE
    /// <c>subprofile{id,name,relation,dateOfBirth,gender}</c>.
    ///
    /// <para>Node gets both from one Prisma <c>include</c>; across module boundaries they are two
    /// batched port calls, so a physician with 200 patients still makes three round trips rather
    /// than 401.</para>
    /// </summary>
    /// <param name="rows">The connection rows, already ordered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The projected elements.</returns>
    private async Task<IReadOnlyList<object>> PhysicianViewAsync(
        IReadOnlyList<DoctorPatientConnection> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];

        var accounts = await identity.GetUsersAsync(
            [.. rows.Select(r => r.PatientUserId).Distinct()], cancellationToken);

        var dependants = await LoadDependantsAsync(rows, cancellationToken);

        return
        [
            .. rows.Select(row =>
            {
                var patient = ConnLinkCommon.RequireAccount(accounts, row.PatientUserId);

                return (object)new ConnLinkListPhysicianViewItem(
                    row.Id,
                    row.PhysicianUserId,
                    row.PatientUserId,
                    row.SubprofileId,
                    row.Status.ToString(),
                    row.InitiatedBy,
                    row.ConnectedAt,
                    row.CreatedAt,
                    row.UpdatedAt,
                    new ConnLinkListPatientUser(
                        patient.Id, patient.DisplayName, patient.Email, patient.Phone),
                    Dependant(dependants, row.SubprofileId) is { } dependant
                        ? new ConnLinkListWideDependant(
                            dependant.Id,
                            dependant.Name,
                            dependant.Relation,
                            dependant.DateOfBirth,
                            dependant.Gender)
                        : null);
            })
        ];
    }

    /// <summary>
    /// The patient element shape: the nine scalars plus
    /// <c>physicianUser{id,displayName,email,phone,physicianProfile{specialty,verified}}</c> and
    /// the NARROW <c>subprofile{id,name,relation}</c>.
    ///
    /// <para>The physician profile is fetched one id at a time because
    /// <see cref="IIdentityDirectory"/> batches physicians by PROFILE id
    /// (<c>GetPhysiciansAsync</c>) and there is no by-USER-id batch. The loop runs over DISTINCT
    /// physician user ids, and a patient's doctor list is a handful of rows, so the cost is bounded
    /// — but it is a real gap in the port, recorded rather than papered over.</para>
    /// </summary>
    /// <param name="rows">The connection rows, already ordered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The projected elements.</returns>
    private async Task<IReadOnlyList<object>> PatientViewAsync(
        IReadOnlyList<DoctorPatientConnection> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];

        string[] physicianUserIds = [.. rows.Select(r => r.PhysicianUserId).Distinct()];

        var accounts = await identity.GetUsersAsync(physicianUserIds, cancellationToken);

        var profiles = new Dictionary<string, PhysicianSummary>(physicianUserIds.Length);
        foreach (var physicianUserId in physicianUserIds)
        {
            // Null is a legitimate answer: Node's nested include emits `physicianProfile: null`
            // for a user with no profile row rather than dropping the key.
            var profile = await identity.GetPhysicianByUserIdAsync(
                physicianUserId, cancellationToken);

            if (profile is not null) profiles[physicianUserId] = profile;
        }

        var dependants = await LoadDependantsAsync(rows, cancellationToken);

        return
        [
            .. rows.Select(row =>
            {
                var physician = ConnLinkCommon.RequireAccount(accounts, row.PhysicianUserId);
                var profile = profiles.GetValueOrDefault(row.PhysicianUserId);

                return (object)new ConnLinkListPatientViewItem(
                    row.Id,
                    row.PhysicianUserId,
                    row.PatientUserId,
                    row.SubprofileId,
                    row.Status.ToString(),
                    row.InitiatedBy,
                    row.ConnectedAt,
                    row.CreatedAt,
                    row.UpdatedAt,
                    new ConnLinkListPhysicianUser(
                        physician.Id,
                        physician.DisplayName,
                        physician.Email,
                        physician.Phone,
                        profile is null
                            ? null
                            : new ConnLinkListPhysicianProfile(profile.Specialty, profile.Verified)),
                    Dependant(dependants, row.SubprofileId) is { } dependant
                        ? new ConnLinkListNarrowDependant(
                            dependant.Id, dependant.Name, dependant.Relation)
                        : null);
            })
        ];
    }

    /// <summary>
    /// The dependants referenced by a page of edges, in one call. Rows whose
    /// <c>subprofileId</c> is null contribute nothing.
    /// </summary>
    /// <param name="rows">The connection rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The dependants, keyed by id.</returns>
    private async Task<IReadOnlyDictionary<string, SubprofileSummary>> LoadDependantsAsync(
        IReadOnlyList<DoctorPatientConnection> rows, CancellationToken cancellationToken)
    {
        string[] ids =
        [
            .. rows.Select(r => r.SubprofileId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Select(id => id!)
                   .Distinct()
        ];

        return ids.Length == 0
            ? new Dictionary<string, SubprofileSummary>(0)
            : await patients.GetSubprofilesAsync(ids, cancellationToken);
    }

    /// <summary>
    /// The dependant for one edge, or null.
    ///
    /// <para>A null <c>subprofileId</c> is Node's <c>subprofile: null</c>. A NON-null id that the
    /// Patients port cannot resolve is also emitted as null rather than thrown: the FK is
    /// <c>onDelete: SetNull</c>, so an id pointing at a row that no longer exists should not be
    /// possible, and if it happens the "My Doctors" card degrades to "Self" instead of 500-ing a
    /// list that is otherwise complete.</para>
    /// </summary>
    /// <param name="dependants">The batched lookup result.</param>
    /// <param name="subprofileId">The edge's dependant id, or null.</param>
    /// <returns>The dependant, or null.</returns>
    private static SubprofileSummary? Dependant(
        IReadOnlyDictionary<string, SubprofileSummary> dependants, string? subprofileId)
        => subprofileId is null ? null : dependants.GetValueOrDefault(subprofileId);
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/connections/request
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The three body fields <c>POST /api/connections/request</c> reads (connections.js:132). Every
/// other key is ignored.
///
/// <para>All three are non-nullable <see cref="JsonElement"/> rather than typed members because
/// Node applies JavaScript truthiness and then hands the raw value to Prisma, and both halves are
/// observable. A typed <c>string?</c> would let the model binder answer
/// <c>400 {"error":"Validation failed", …}</c> — a body this router never produces — for inputs
/// that must reach the handler's own 400 or the route's 500. Non-nullable so an ABSENT key
/// (<see cref="JsonValueKind.Undefined"/>) stays distinguishable from an explicit <c>null</c>;
/// this route treats them alike, but one convention across the module is safer than two.</para>
/// </summary>
/// <param name="TargetEmail">
/// The person to connect to, by email. Gated by <c>if (!targetEmail)</c>, so <c>""</c>, <c>0</c>,
/// <c>false</c>, <c>null</c> and an absent key all take the 400.
/// </param>
/// <param name="SubprofileId">
/// The dependant this edge is for. Node writes <c>subprofileId || null</c> (:193, :215), so a
/// falsy value means the PARENT edge. <b>It is never validated</b> — no ownership check, no
/// existence check — so a patient can request a connection for somebody else's dependant and the
/// FK is the only thing that stops them.
/// </param>
/// <param name="ClinicId">
/// The clinic whose physician the caller is acting for. Body first, then the <c>X-Clinic-Id</c>
/// header (:143); the query string is NOT consulted on this route.
/// </param>
public sealed record ConnLinkRequestBody(
    JsonElement TargetEmail,
    JsonElement SubprofileId,
    JsonElement ClinicId);

/// <summary>
/// The two outcomes of <c>POST /api/connections/request</c>, which answers <b>201</b> when it
/// inserts an edge and <b>200</b> when it revives a REJECTED one, with two DIFFERENT bodies.
///
/// <para>This is a handler-to-controller record and never reaches the wire; only one of the two
/// payloads does. The controller reads it as
/// <c>outcome.Created ? CreatedPayload(outcome.CreatedBody!) : Payload(outcome.ResentBody!)</c>.
/// Exactly one of the two is non-null, selected by <see cref="Created"/>.</para>
/// </summary>
/// <param name="Created">True for the 201 insert path, false for the 200 re-send path.</param>
/// <param name="CreatedBody">The 201 payload, non-null exactly when <see cref="Created"/> is true.</param>
/// <param name="ResentBody">The 200 payload, non-null exactly when <see cref="Created"/> is false.</param>
public sealed record ConnLinkRequestOutcome(
    bool Created,
    ConnLinkRequestCreatedResponse? CreatedBody,
    ConnLinkRequestResentResponse? ResentBody);

/// <summary>
/// The connection-request command.
/// </summary>
/// <param name="CallerUserId">
/// <c>req.user.id</c>. It becomes <c>initiatedBy</c> on BOTH paths, including the staff path where
/// the physician end of the edge is somebody else entirely (:218).
/// </param>
/// <param name="CallerUserType">
/// <c>req.user.userType</c>, forwarded RAW and UNPARSED. It is compared as a string against
/// <c>"PHYSICIAN"</c> and <c>"PATIENT"</c>; parsing it into the <c>UserType</c> enum would reject
/// a claim value the enum does not have, and the third branch is reached by every value that is
/// neither.
/// </param>
/// <param name="ClinicIdHeader">
/// The raw <c>X-Clinic-Id</c> header, or null. Read in the controller and passed here rather than
/// taken from <see cref="IClinicContext"/>, because that would add <c>?clinicId=</c> as a source
/// Node does not read on this route.
/// </param>
/// <param name="Body">The request body, or null for an absent one.</param>
public sealed record ConnLinkRequestCommand(
    string CallerUserId,
    string? CallerUserType,
    string? ClinicIdHeader,
    ConnLinkRequestBody? Body) : IRequest<ConnLinkRequestOutcome>;

/// <summary>
/// Port of <c>POST /api/connections/request</c> (connections.js:131-226) — the entry point for the
/// whole Add-Doctor and Add-Patient flow.
///
/// <para><b>A repeat request is a 409, not an idempotent 200</b>, and there are two of them
/// (:196-201): an ACCEPTED edge answers <c>409 {"error":"Already connected"}</c> and a PENDING one
/// answers <c>409 {"error":"Connection request already pending"}</c>. Only a REJECTED edge is
/// revived, and that path answers 200 with a narrower body. The handler probes before inserting,
/// so the <c>@@unique([physicianUserId, patientUserId, subprofileId])</c> constraint is never the
/// thing that reports a duplicate.</para>
///
/// <para><b>The existence probe is keyed on the FULL triple</b> —
/// <c>FindPairAsync(physician, patient, subprofileId)</c> with <c>subprofileId</c> compared as a
/// VALUE, so null matches only rows whose column IS NULL. That is deliberately different from
/// <c>POST /add-by-phone</c>, which omits the key entirely and therefore reports "already
/// connected" to a physician who is linked only to the patient's CHILD. Reproduce each route's own
/// choice; see docs/connections-surface.md §10.6.</para>
///
/// <para><b>The 400 order matters.</b> <c>Target email is required</c> comes FIRST, before the
/// staff resolution and before the user lookup, so a staff member with a bad clinic id and no
/// email still gets the email 400. Then <c>404 User not found…</c>, then
/// <c>400 Connections must be between a physician and a patient</c>, then the two 409s.</para>
///
/// <para><b>The staff fallback FALLS THROUGH on failure</b> (:143-157). It does not 403 and it
/// does not 404: <c>actingAsPhysicianId</c> simply stays null, the caller keeps their own
/// <c>userType</c>, and a RECEPTIONIST then fails the pairing test at :182 and receives
/// <c>400 Connections must be between a physician and a patient</c>. That 400 is the observable
/// consequence of a clinic mis-configuration on this route.</para>
///
/// <para><b>⚠ OPEN DECISION — this route may be a live 500 in Node.</b> Line 160 is
/// <c>prisma.user.findUnique({ where: { email: targetEmail } })</c>, but <c>model User</c> has no
/// standalone <c>@unique</c> on <c>email</c> — it carries <c>@@index([email])</c> and
/// <c>@@unique([email, userType])</c> (schema.prisma:103, 132, 137). If Prisma rejects that
/// <c>where</c>, every call to this endpoint answers
/// <c>500 {"error":"Failed to send connection request"}</c>. This port does what the code plainly
/// intends — first match on email, no <c>userType</c> narrowing, through
/// <see cref="IIdentityDirectory.GetUserByEmailAsync"/> — because reproducing a Prisma validation
/// crash is reproducing a crash, not a contract. The only evidence that settles it is running both
/// backends against the same request; record the outcome in PORT-STATUS.md either way. See
/// docs/connections-surface.md §11.6.</para>
///
/// <para>One further quirk kept as-is: <b>Node never checks that the two ends are different
/// people</b>. A dual-role account whose <c>userType</c> is PHYSICIAN and whose own email is the
/// target passes the <c>userType === 'PHYSICIAN' &amp;&amp; target.userType === 'PATIENT'</c> test
/// only if the target row says PATIENT, so it is hard to reach — but nothing here forbids it, and
/// <c>POST /connect-by-pin</c> has an explicit "Cannot connect to yourself" guard that this route
/// does not.</para>
/// </summary>
public sealed class ConnLinkRequestHandler(
    IConnectionsDbContext dbContext,
    IDoctorPatientConnectionStore connections,
    IIdentityDirectory identity,
    ConnStaffResolver staffResolver,
    IAppLogger<ConnLinkRequestHandler> logger)
    : IRequestHandler<ConnLinkRequestCommand, ConnLinkRequestOutcome>
{
    private const string LogMessage = "[Connections] Request error";
    private const string FailureError = "Failed to send connection request";

    public Task<ConnLinkRequestOutcome> Handle(
        ConnLinkRequestCommand request, CancellationToken cancellationToken = default)
        => ConnLinkPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnLinkRequestOutcome> HandleCore(
        ConnLinkRequestCommand request, CancellationToken cancellationToken)
    {
        var body = request.Body;

        // `if (!targetEmail)` (:138). An absent body destructures to undefined, which is falsy.
        var targetEmail = body is null ? null : StringOrCrash(body.TargetEmail, "targetEmail");
        if (targetEmail is null) throw ConnErrors.BadRequest("Target email is required");

        // `const staffClinicId = clinicId || req.headers['x-clinic-id']` (:143). Body first; no
        // query string. ⚠ Only the TRUTHINESS selection happens here. The string coercion is
        // deferred into the branch below, because Node never hands this value to Prisma outside
        // `if (userType !== 'PHYSICIAN' && staffClinicId)` (:144-147): a PHYSICIAN caller who posts
        // `{"clinicId": 7}` has that value ignored entirely and receives the normal 201, so
        // coercing it here would 500 a request Node completes.
        var bodyClinicId = body?.ClinicId ?? default;
        var hasBodyClinicId = ConnJs.IsTruthy(bodyClinicId);
        var headerClinicId = ConnJs.Truthy(request.ClinicIdHeader);
        var hasStaffClinicId = hasBodyClinicId || headerClinicId is not null;

        // ── Staff fallback (:145-157). Falls through on every failure. ───────
        string? actingAsPhysicianId = null;
        if (request.CallerUserType != "PHYSICIAN" && hasStaffClinicId)
        {
            // NOW the value reaches Prisma (:146), so NOW a truthy non-string is the route's 500.
            var staffClinicId = hasBodyClinicId
                ? StringOrCrash(bodyClinicId, "clinicId")!
                : headerClinicId!;

            var resolution = await staffResolver.ResolveAsync(
                request.CallerUserId, staffClinicId, cancellationToken);

            if (resolution.IsResolved) actingAsPhysicianId = resolution.PhysicianUserId;
        }

        // ── Target user (:160-163). See the OPEN DECISION on the class. ──────
        var targetUser = await identity.GetUserByEmailAsync(targetEmail, ct: cancellationToken);
        if (targetUser is null)
        {
            throw ConnErrors.NotFound("User not found. They must register on Tebrazi first.");
        }

        // ── Which end is which (:170-184) ────────────────────────────────────
        string physicianUserId;
        string patientUserId;

        if (actingAsPhysicianId is not null && targetUser.UserType == "PATIENT")
        {
            physicianUserId = actingAsPhysicianId;
            patientUserId = targetUser.Id;
        }
        else if (request.CallerUserType == "PHYSICIAN" && targetUser.UserType == "PATIENT")
        {
            physicianUserId = request.CallerUserId;
            patientUserId = targetUser.Id;
        }
        else if (request.CallerUserType == "PATIENT" && targetUser.UserType == "PHYSICIAN")
        {
            physicianUserId = targetUser.Id;
            patientUserId = request.CallerUserId;
        }
        else
        {
            throw ConnErrors.BadRequest("Connections must be between a physician and a patient");
        }

        // `subprofileId || null` (:193, :215). ⚠ Coerced HERE and not at the top of the handler:
        // :193 is the FIRST point Node hands the value to Prisma, i.e. after the 404 at :166 and
        // after the pairing 400 at :183. A request carrying `{"subprofileId": 42}` for an email
        // nobody owns must answer Node's 404, not this route's 500.
        var subprofileId = body is null
            ? null
            : StringOrCrash(body.SubprofileId, "subprofileId");

        // ── Existing edge on the FULL triple (:188-206) ──────────────────────
        var existing = await connections.FindPairAsync(
            physicianUserId, patientUserId, subprofileId,
            status: null, tracked: true, ct: cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == ConnectionStatus.ACCEPTED)
            {
                throw ConnErrors.Conflict("Already connected");
            }

            if (existing.Status == ConnectionStatus.PENDING)
            {
                throw ConnErrors.Conflict("Connection request already pending");
            }

            // REJECTED (and, in principle, REMOVED): revive it. `connectedAt` is NOT cleared.
            existing.Reopen(request.CallerUserId);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new ConnLinkRequestOutcome(
                Created: false,
                CreatedBody: null,
                ResentBody: new ConnLinkRequestResentResponse(
                    existing.Id,
                    existing.PhysicianUserId,
                    existing.PatientUserId,
                    existing.SubprofileId,
                    existing.Status.ToString(),
                    existing.InitiatedBy,
                    existing.ConnectedAt,
                    existing.CreatedAt,
                    existing.UpdatedAt,
                    "Connection request re-sent"));
        }

        // ── Insert (:207-224) ────────────────────────────────────────────────
        var connection = DoctorPatientConnection.CreatePending(
            physicianUserId, patientUserId, request.CallerUserId, subprofileId);

        connections.Add(connection);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Node's create carries `include: { physicianUser: { select: { displayName, email } },
        // patientUser: { select: { displayName, email } } }` (:218-221) — two keys each, NO id.
        var accounts = await identity.GetUsersAsync(
            [physicianUserId, patientUserId], cancellationToken);

        var physician = ConnLinkCommon.RequireAccount(accounts, physicianUserId);
        var patient = ConnLinkCommon.RequireAccount(accounts, patientUserId);

        return new ConnLinkRequestOutcome(
            Created: true,
            CreatedBody: new ConnLinkRequestCreatedResponse(
                connection.Id,
                connection.PhysicianUserId,
                connection.PatientUserId,
                connection.SubprofileId,
                connection.Status.ToString(),
                connection.InitiatedBy,
                connection.ConnectedAt,
                connection.CreatedAt,
                connection.UpdatedAt,
                new ConnLinkRequestParty(physician.DisplayName, physician.Email),
                new ConnLinkRequestParty(patient.DisplayName, patient.Email)),
            ResentBody: null);
    }

    /// <summary>
    /// <c>value || null</c> for a body field Node then hands to Prisma as a string.
    ///
    /// <para>A falsy value is null, which is what every <c>||</c> in this handler wants. A TRUTHY
    /// NON-STRING — <c>{ "targetEmail": 42 }</c>, <c>{ "subprofileId": [] }</c> — is not silently
    /// dropped: Prisma rejects a non-string where a string column is expected, which lands in the
    /// route's own catch, so this raises into the file guard and the wire answer is
    /// <c>500 {"error":"Failed to send connection request"}</c> with Node's log line. Mapping it to
    /// null instead would turn a Node 500 into a 201 with a missing dependant.</para>
    /// </summary>
    /// <param name="value">The bound JSON value.</param>
    /// <param name="field">The field name, for the log.</param>
    /// <returns>The string value, or null when the field is falsy.</returns>
    private static string? StringOrCrash(JsonElement value, string field)
    {
        if (!ConnJs.IsTruthy(value)) return null;

        return ConnJs.AsString(value)
            ?? throw new InvalidOperationException(
                $"{field} is a {value.ValueKind}, not a string. Prisma rejects it, so the route "
                + "answers its own 500.");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  PUT /api/connections/{id}/accept
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The accept command. There is NO body — Node reads <c>req.params.id</c> and nothing else.
/// </summary>
/// <param name="ConnectionId">The route's <c>{id}</c>, unvalidated. A miss is the 404.</param>
/// <param name="CallerUserId">
/// <c>req.user.id</c>. It is tested against <c>initiatedBy</c> first and then against both ends of
/// the edge, in that order.
/// </param>
public sealed record ConnLinkAcceptCommand(string ConnectionId, string CallerUserId)
    : IRequest<ConnLinkAcceptResponse>;

/// <summary>
/// Port of <c>PUT /api/connections/{id}/accept</c> (connections.js:240-271).
///
/// <para><b>FOUR gates, and the ORDER is on the wire.</b> Reordering any pair changes the status
/// code a real client sees:</para>
/// <list type="number">
/// <item><c>404 {"error":"Connection not found"}</c> — the row does not exist (:246). This is
/// checked with NO ownership predicate, which is why 404 precedes 403 here and on the two sibling
/// routes: a stranger probing an id learns the row exists before being refused.</item>
/// <item><c>400 {"error":"Cannot accept your own request"}</c> — <c>initiatedBy === caller</c>
/// (:249-251). <b>This runs BEFORE the party check</b>, so the initiator of an edge is told "your
/// own request" rather than "access denied", and a staff member who requested on a physician's
/// behalf is blocked from accepting it even though they are not a party at all.</item>
/// <item><c>403 {"error":"Access denied"}</c> — the caller is neither end (:254-256).</item>
/// <item><c>400 {"error":"Connection is already &lt;status&gt;"}</c> — anything but PENDING
/// (:258-260). The status is <b>INTERPOLATED and LOWERCASED</b> from the stored value, so the
/// wire carries <c>Connection is already accepted</c>, <c>…already rejected</c> or
/// <c>…already removed</c>. Three literals would be wrong: this is one template.</item>
/// </list>
///
/// <para>The write is <c>{ status: 'ACCEPTED', connectedAt: new Date() }</c> (:268) — an
/// UNCONDITIONAL assignment, not a <c>||</c> keep, so re-accepting moves the stamp. That is the
/// same class of bug as the three <c>??=</c> found in Appointments, in the opposite direction:
/// here the overwrite IS the contract.</para>
///
/// <para>No notification is published. Despite the name, <c>CONNECTION_ACCEPTED</c> is emitted by
/// <c>add-by-phone</c> and <c>create-patient</c>, never by this route — the accepting user is
/// already looking at the screen and the requester is told nothing.</para>
/// </summary>
public sealed class ConnLinkAcceptHandler(
    IConnectionsDbContext dbContext,
    IDoctorPatientConnectionStore connections,
    IAppLogger<ConnLinkAcceptHandler> logger)
    : IRequestHandler<ConnLinkAcceptCommand, ConnLinkAcceptResponse>
{
    private const string LogMessage = "[Connections] Accept error";
    private const string FailureError = "Failed to accept connection";

    public Task<ConnLinkAcceptResponse> Handle(
        ConnLinkAcceptCommand request, CancellationToken cancellationToken = default)
        => ConnLinkPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnLinkAcceptResponse> HandleCore(
        ConnLinkAcceptCommand request, CancellationToken cancellationToken)
    {
        var connection = await connections.GetForUpdateAsync(request.ConnectionId, cancellationToken)
            ?? throw ConnErrors.NotFound("Connection not found");

        // (2) before (3): the initiator test runs before the party test.
        if (connection.InitiatedBy == request.CallerUserId)
        {
            throw ConnErrors.BadRequest("Cannot accept your own request");
        }

        if (!connection.InvolvesUser(request.CallerUserId))
        {
            throw ConnErrors.Forbidden("Access denied");
        }

        if (connection.Status != ConnectionStatus.PENDING)
        {
            // `Connection is already ${conn.status.toLowerCase()}` (:259). JavaScript's
            // toLowerCase is not culture-sensitive, so ToLowerInvariant is the faithful operator —
            // ToLower() under a Turkish culture would emit "accepted" with a dotless i.
            throw ConnErrors.BadRequest(
                $"Connection is already {connection.Status.ToString().ToLowerInvariant()}");
        }

        connection.Accept();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ConnLinkAcceptResponse(
            connection.Id,
            connection.PhysicianUserId,
            connection.PatientUserId,
            connection.SubprofileId,
            connection.Status.ToString(),
            connection.InitiatedBy,
            connection.ConnectedAt,
            connection.CreatedAt,
            connection.UpdatedAt,
            "Connection accepted");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  PUT /api/connections/{id}/reject
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The reject command. No body.
/// </summary>
/// <param name="ConnectionId">The route's <c>{id}</c>, unvalidated.</param>
/// <param name="CallerUserId">
/// <c>req.user.id</c>. Tested ONLY against the two ends of the edge — there is no
/// <c>initiatedBy</c> gate on this route, so the requester can reject their own request.
/// </param>
public sealed record ConnLinkRejectCommand(string ConnectionId, string CallerUserId)
    : IRequest<ConnLinkRejectResponse>;

/// <summary>
/// Port of <c>PUT /api/connections/{id}/reject</c> (connections.js:277-296).
///
/// <para><b>TWO gates, not four, and that asymmetry with <c>/accept</c> is the contract.</b>
/// <c>404 {"error":"Connection not found"}</c> (:282) then
/// <c>403 {"error":"Access denied"}</c> (:284-286), in that order — 404 first, because the row is
/// loaded with no ownership predicate. And then nothing else: there is no "cannot reject your own
/// request" and NO status precondition, so an already-ACCEPTED edge, an already-REJECTED edge and
/// one the caller initiated themselves are all rejected with a 200.</para>
///
/// <para>The write is <c>{ status: 'REJECTED' }</c> and nothing else (:293), so
/// <b><c>connectedAt</c> is deliberately left standing</b> — rejecting an accepted connection
/// answers 200 with a non-null "when we connected". <c>DoctorPatientConnection.Reject</c>
/// reproduces that by not touching the column.</para>
///
/// <para>Rejecting is therefore a soft revocation that keeps the row, while
/// <c>DELETE /{id}</c> removes it outright. Both are reachable from the same client screen.</para>
/// </summary>
public sealed class ConnLinkRejectHandler(
    IConnectionsDbContext dbContext,
    IDoctorPatientConnectionStore connections,
    IAppLogger<ConnLinkRejectHandler> logger)
    : IRequestHandler<ConnLinkRejectCommand, ConnLinkRejectResponse>
{
    private const string LogMessage = "[Connections] Reject error";
    private const string FailureError = "Failed to reject connection";

    public Task<ConnLinkRejectResponse> Handle(
        ConnLinkRejectCommand request, CancellationToken cancellationToken = default)
        => ConnLinkPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnLinkRejectResponse> HandleCore(
        ConnLinkRejectCommand request, CancellationToken cancellationToken)
    {
        var connection = await connections.GetForUpdateAsync(request.ConnectionId, cancellationToken)
            ?? throw ConnErrors.NotFound("Connection not found");

        if (!connection.InvolvesUser(request.CallerUserId))
        {
            throw ConnErrors.Forbidden("Access denied");
        }

        connection.Reject();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ConnLinkRejectResponse(
            connection.Id,
            connection.PhysicianUserId,
            connection.PatientUserId,
            connection.SubprofileId,
            connection.Status.ToString(),
            connection.InitiatedBy,
            connection.ConnectedAt,
            connection.CreatedAt,
            connection.UpdatedAt,
            "Connection rejected");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  DELETE /api/connections/{id}
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The disconnect command. No body.
/// </summary>
/// <param name="ConnectionOrUserId">
/// The route's <c>{id}</c>. Named for what it actually is: the handler first treats it as a
/// CONNECTION id and, failing that, as the OTHER PARTY'S USER id. Both are live client usages.
/// </param>
/// <param name="CallerUserId"><c>req.user.id</c>. Either party may disconnect.</param>
public sealed record ConnLinkDeleteCommand(string ConnectionOrUserId, string CallerUserId)
    : IRequest<ConnLinkDeleteResponse>;

/// <summary>
/// Port of <c>DELETE /api/connections/{id}</c> (connections.js:401-433).
///
/// <para><b>It HARD deletes.</b> <c>prisma.doctorPatientConnection.delete</c> (:427), not a status
/// write — <c>REMOVED</c> exists in the enum and no route in the file ever assigns it. There is no
/// <c>deletedAt</c> column on this model, so there is no softer option, and removing the row
/// revokes the physician's access to the chart. This is the opposite of
/// <c>DELETE /api/visits/{id}</c>, which only sets <c>status = 'ARCHIVED'</c>; both patterns exist
/// in this port and only the source settles which applies.</para>
///
/// <para><b>The <c>{id}</c> is overloaded, and the fallback is the point of the route</b>
/// (:409-419). When no connection has that id, the handler looks for an edge joining the CALLER to
/// the user whose id was passed, in either direction, so the client's "remove this doctor" button
/// can send a user id it already has instead of hunting for the connection id. Two consequences
/// worth knowing: the fallback has NO subprofile clause, so it can return and delete a DEPENDANT
/// edge when the caller meant the parent one; and it takes the FIRST match, so a patient connected
/// to one doctor for themselves and two children loses one edge per call, oldest first
/// (Node leaves the order to Postgres; the store orders by <c>created_at</c> ascending for
/// determinism).</para>
///
/// <para><b>404 precedes 403.</b> Both lookups run before any ownership test, so an id that
/// resolves to nothing is <c>404 {"error":"Connection not found"}</c> (:421) and an id that
/// resolves to somebody else's edge is <c>403 {"error":"Access denied"}</c> (:423-425). The
/// fallback lookup is already caller-scoped, so in practice the 403 is reachable only through the
/// first, by-id lookup.</para>
/// </summary>
public sealed class ConnLinkDeleteHandler(
    IConnectionsDbContext dbContext,
    IDoctorPatientConnectionStore connections,
    IAppLogger<ConnLinkDeleteHandler> logger)
    : IRequestHandler<ConnLinkDeleteCommand, ConnLinkDeleteResponse>
{
    private const string LogMessage = "[Connections] Delete error";
    private const string FailureError = "Failed to remove connection";

    public Task<ConnLinkDeleteResponse> Handle(
        ConnLinkDeleteCommand request, CancellationToken cancellationToken = default)
        => ConnLinkPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnLinkDeleteResponse> HandleCore(
        ConnLinkDeleteCommand request, CancellationToken cancellationToken)
    {
        var connection =
            await connections.GetForUpdateAsync(request.ConnectionOrUserId, cancellationToken)
            ?? await connections.FindForUpdateByEitherSideAsync(
                request.CallerUserId, request.ConnectionOrUserId, cancellationToken);

        if (connection is null) throw ConnErrors.NotFound("Connection not found");

        if (!connection.InvolvesUser(request.CallerUserId))
        {
            throw ConnErrors.Forbidden("Access denied");
        }

        connections.Remove(connection);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ConnLinkDeleteResponse("Connection removed");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/connections/patient-summaries
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The physician dashboard's roll-up. No inputs at all — Node reads only <c>req.user.id</c>, with
/// no query string, no pagination and no clinic header.
///
/// <para><b>OWNERSHIP: SETTLED — this file owns it.</b> The parallel-writing pass left
/// docs/connections-surface.md §8 and <c>ConnectionResponseConventions</c> filing this endpoint
/// under group B (<c>ConnSubprofile</c>) while it was in fact written here under <c>ConnLink</c>.
/// Both documents now say <c>ConnLink</c>. There is exactly one implementation — no
/// <c>ConnSubprofilePatientSummaries*</c> type exists and none is to be added. The prefixes made
/// the duplicate compile-clean, which is precisely why it had to be resolved before the controller
/// was written: two handlers for one request type fail at RUNTIME, not at build.</para>
/// </summary>
public sealed record ConnLinkPatientSummariesQuery
    : IRequest<IReadOnlyDictionary<string, ConnLinkPatientSummary>>;

/// <summary>
/// Port of <c>GET /api/connections/patient-summaries</c> (connections.js:1042-1131) — the clinical
/// context strip on the physician dashboard.
///
/// <para><b>The response is an OBJECT KEYED BY PATIENT USER ID, not an array.</b> A caller with no
/// <c>physician_profiles</c> row gets <c>200 {}</c> through an early return that never issues the
/// connection query (:1045-1046) — never a 403 and never <c>[]</c>. Every non-physician therefore
/// gets an empty object, which is the shape the client already handles.</para>
///
/// <para><b>The patient set is DISTINCT patient ids from ACCEPTED edges</b> (:1048-1053), keyed on
/// the physician's USER id. There is no <c>subprofileId</c> clause, so a physician connected to one
/// family for the parent and two children still gets ONE key — the de-duplication is what makes
/// the map shape work.</para>
///
/// <para><b>Six aggregates per patient, batched.</b> Node issues seven queries per patient inside
/// <c>Promise.allSettled</c>; this handler issues three batched port calls for the whole set. The
/// facts that survive that transformation:</para>
/// <list type="bullet">
/// <item><c>lastVisitDate</c>, <c>lastComplaint</c> and <c>visitCount</c> have NO status filter —
/// an IN_PROGRESS or CANCELLED visit can be the "last" one and is counted.</item>
/// <item><c>lastComplaint</c> is <c>lastVisit?.chiefComplaint || null</c>, so an EMPTY STRING
/// becomes null. <c>ConnJs.Truthy</c> is that <c>||</c>.</item>
/// <item><c>conditionsCount</c> and <c>medsCount</c> reach their rows THROUGH A DEPENDANT only,
/// and neither filters <c>isActive</c> — a resolved condition and a discontinued medication both
/// count.</item>
/// <item><c>familyCount</c> is the SIZE OF A UNION (:1089): ACCEPTED edges carrying a non-null
/// <c>subprofileId</c>, plus DISTINCT dependants this physician has actually visited. It excludes
/// SELF for free, because a SELF visit carries a null <c>subprofileId</c>. Summing the two sets
/// instead of unioning them would double-count every dependant who is both assigned and seen,
/// which is the common case.</item>
/// <item><c>hasOverdueFollowUp</c> is <c>!!overdueFollowUp</c> — the existence of the ROW, not a
/// date comparison — and only that query filters status (COMPLETED). Its cutoff is Node's LOCAL
/// midnight (<c>new Date(); today.setHours(0,0,0,0)</c>, :1055-1056), so this handler passes
/// <c>DateTime.Now.Date</c>, matching the precedent at
/// AppointmentAssistantUseCases.cs:385. <c>DateTime.UtcNow.Date</c> would shift the boundary by
/// the server's offset and move follow-ups in and out of "overdue" for hours at a time.</item>
/// </list>
///
/// <para><b>DIVERGENCE 1 — <c>unreadMessages</c> is always 0.</b> Node counts
/// <c>{ senderUserId: pid, receiverUserId: userId, isRead: false }</c> on the <c>messages</c>
/// table (:1100-1106). There is no Messages module in this port and no directory port onto that
/// table, so the key is emitted with a zero rather than dropped — dropping it would break the
/// client's badge arithmetic, and inventing a port would reach into a module that does not exist.
/// Recorded, not papered over.</para>
///
/// <para><b>DIVERGENCE 2 — <c>allSettled</c> semantics cannot be reproduced.</b> In Node a failure
/// for ONE patient drops that patient's key and still answers 200 (:1122-1127). A batched query
/// cannot fail per patient, so this handler either returns every patient or throws — and a throw
/// becomes <c>500 {"error":"Failed to load summaries"}</c>. Both are the honest outcomes of
/// batching; the alternative is the N+1 Node explicitly pays for.</para>
/// </summary>
public sealed class ConnLinkPatientSummariesHandler(
    ICurrentUser currentUser,
    IDoctorPatientConnectionStore connections,
    IIdentityDirectory identity,
    IPatientDirectory patients,
    IVisitDirectory visits,
    IAppLogger<ConnLinkPatientSummariesHandler> logger)
    : IRequestHandler<ConnLinkPatientSummariesQuery, IReadOnlyDictionary<string, ConnLinkPatientSummary>>
{
    private const string LogMessage = "[Connections] Patient summaries error";
    private const string FailureError = "Failed to load summaries";

    /// <summary>
    /// The value of <c>unreadMessages</c> until a Messages module exists. See DIVERGENCE 1.
    /// </summary>
    private const int UnreadMessagesUnavailable = 0;

    public Task<IReadOnlyDictionary<string, ConnLinkPatientSummary>> Handle(
        ConnLinkPatientSummariesQuery request, CancellationToken cancellationToken = default)
        => ConnLinkPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(cancellationToken));

    private async Task<IReadOnlyDictionary<string, ConnLinkPatientSummary>> HandleCore(
        CancellationToken cancellationToken)
    {
        var userId = ConnLinkCommon.RequireUserId(currentUser);

        // `if (!physician) return res.json({})` (:1045-1046) — 200 with an empty OBJECT.
        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);
        if (physician is null) return new Dictionary<string, ConnLinkPatientSummary>(0);

        var edges = await connections.ListAsync(
            new ConnectionFilter(
                PhysicianUserId: userId,
                Status: ConnectionStatus.ACCEPTED),
            cancellationToken);

        string[] patientIds = [.. edges.Select(e => e.PatientUserId).Distinct()];
        if (patientIds.Length == 0) return new Dictionary<string, ConnLinkPatientSummary>(0);

        // Node's LOCAL midnight, not UtcNow.Date. See the class comment.
        var today = DateTime.Now.Date;

        // `physician.id` here is the PROFILE id — visits.physician_id, not a user id.
        var visitSummaries = await visits.GetPatientVisitSummariesAsync(
            physician.Id, patientIds, today, cancellationToken);

        var clinicalCounts = await patients.CountSubprofileClinicalItemsAsync(
            patientIds, cancellationToken);

        var assignedDependants = await connections.ListAcceptedSubprofileIdsAsync(
            userId, patientIds, cancellationToken);

        var summaries = new Dictionary<string, ConnLinkPatientSummary>(patientIds.Length);

        foreach (var patientId in patientIds)
        {
            // Absent means "no visits at all" — the key is still emitted, with zeros and nulls.
            var visitSummary = visitSummaries.GetValueOrDefault(patientId);
            var counts = clinicalCounts.GetValueOrDefault(patientId);

            // `new Set([...subConns, ...visitedSubs]).size` (:1089).
            var family = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in assignedDependants.GetValueOrDefault(patientId)
                               ?? (IReadOnlyList<string>)Array.Empty<string>())
            {
                family.Add(id);
            }

            foreach (var id in visitSummary?.VisitedSubprofileIds
                               ?? (IReadOnlyList<string>)Array.Empty<string>())
            {
                family.Add(id);
            }

            summaries[patientId] = new ConnLinkPatientSummary(
                visitSummary?.LastVisitDate,
                ConnJs.Truthy(visitSummary?.LastComplaint),
                visitSummary?.VisitCount ?? 0,
                counts.ConditionsCount,
                counts.MedicationsCount,
                family.Count,
                visitSummary?.OverdueFollowUpDate is not null,
                visitSummary?.OverdueFollowUpDate,
                UnreadMessagesUnavailable);
        }

        return summaries;
    }
}
