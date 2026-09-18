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
//  THE DISCOVERY GROUP of server/src/routes/connections.js — how a physician or a patient finds
//  the other, creates them when they are not there yet, and invites them when they are not on the
//  platform at all:
//
//      GET  /api/connections/search             (connections.js:443-527)
//      POST /api/connections/add-by-phone       (connections.js:529-615)
//      POST /api/connections/create-patient     (connections.js:617-735)
//      POST /api/connections/invite-email       (connections.js:969-1009)
//      GET  /api/connections/invite-link        (connections.js:1011-1040)
//      GET  /api/connections/search-physicians  — NO NODE ROUTE. See "THE MISSING ROUTE" below.
//
//  Facts that hold across the group, stated once here rather than six times:
//
//  * There is NO extracted contract for this module. docs/port-contracts/ covers Visits,
//    Appointments and Prescriptions only, so connections.js itself is the sole authority and every
//    line number above and below points there.
//  * READ endpoints (search, search-physicians, invite-link) inject ICurrentUser; WRITE endpoints
//    (add-by-phone, create-patient, invite-email) take the caller's identity explicitly on the
//    command. That is the module house rule, matching Visits, Appointments and Prescriptions.
//  * ICurrentUser.UserType is compared as a STRING and never parsed into the UserType enum — the
//    claim can carry a value the enum does not have, and every gate in this file is
//    `!== 'PHYSICIAN'`, so RECEPTIONIST, STAFF, PATIENT and an unknown value all take the same
//    branch.
//  * Each route has its OWN named 500 literal, so each handler is wrapped end to end by
//    ConnDiscoverPersistence.RunAsync: Handle delegates to HandleCore, a deliberate 4xx passes
//    through untouched, and anything else is logged the way Node logs it and rethrown as that
//    route's own 500 body.
//  * Every error body in all 1,818 lines of connections.js is a bare { "error": "…" }. Hence
//    ConnErrors everywhere and never ValidationException/ConflictException, both of which prepend
//    a generic label and push the real text into a second key the client does not read.
//  * Bodies bind as NULLABLE records of JsonElement members, so an absent, null or wrongly-typed
//    field reaches this file's own gates rather than the model binder's
//    400 {"error":"Validation failed"} — a body no Connections route ever produces.
//
//  THE MISSING ROUTE. `GET /api/connections/search-physicians` has no route registration in
//  connections.js, so live Node answers the global 404. The client calls it anyway —
//  client/src/components/PatientOnboardingWizard.jsx:68 — inside `catch { setSearchResults([]) }`,
//  which is why nobody has noticed: the onboarding wizard's "connect with your doctor" step
//  silently returns no results for every patient who has ever seen it. The decision recorded in
//  docs/connections-surface.md §11.4 is to IMPLEMENT it rather than reproduce the 404, following
//  the precedent already set by GET /api/visits/inbox: the call site already handles the correct
//  shape, so a working endpoint cannot break it, while a reproduced 404 keeps a live feature dead.
//  Its sibling `POST /api/connections` — same component, :76 — is ALSO unimplemented in Node and
//  is NOT in this file: it is POST /request semantics keyed by physicianUserId and belongs with
//  the link-lifecycle group. See the report note; it must not be left to fall between the two.
//
//  ROUTE ORDER, for whoever writes the controller: `search`, `search-physicians`, `add-by-phone`,
//  `create-patient`, `invite-email` and `invite-link` are all single-segment LITERALS and must be
//  registered before `GET /{id}`-shaped routes, or they bind as ids. Do not introduce a
//  `POST /{id}` or `/{id}/{action}` catch-all in this controller: `PUT /{id}/accept` and
//  `PUT /{id}/reject` already share that shape with nothing, and a catch-all would swallow
//  `assign-subprofile` and `clinic-patients` alike.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The route-level guard for this file, mapping any unexpected failure onto the route's OWN 500
/// body.
///
/// <para>Each Node handler in this group is wrapped in a try/catch whose only outcome is a named
/// literal — <c>{"error":"Search failed"}</c>, <c>"Failed to add patient"</c>,
/// <c>"Failed to create patient"</c>, <c>"Failed to send invitation"</c>,
/// <c>"Failed to generate link"</c>. Letting an EF or port exception escape would emit
/// <c>ExceptionHandlingMiddleware</c>'s generic <c>{"error":"Internal Server Error"}</c> instead,
/// and the React client branches on <c>err.response.data.error</c>.</para>
///
/// <para>The Node try covers the WHOLE handler, so a failure in the staff-context lookup, in a
/// cross-module port or in the response projection all answer the same named 500 as a failure in
/// the query itself. That is why this wraps <c>HandleCore</c> end to end rather than one
/// statement.</para>
///
/// <para><b>It also swallows a 500-status <see cref="AppException"/>, which the sibling guards in
/// Prescriptions do not.</b> This group is the only one that calls
/// <see cref="IPatientProfileProvisioner"/>, whose implementation throws
/// <c>BusinessException("Invalid gender", …, 500)</c> for a <c>gender</c> value Prisma would
/// reject. In Node that rejection is a caught Prisma error and the route answers
/// <c>500 {"error":"Failed to create patient"}</c>; rethrowing the provisioner's exception
/// untouched would emit <c>{"error":"Invalid gender","message":…}</c> instead, which is a body
/// connections.js cannot produce. Deliberate 400/403/404/409 bodies still pass through, because
/// they carry their own status.</para>
///
/// <para>Declared internal to this file with the group's prefix, because the five Connections
/// handler files share one namespace and each needs its own log message and failure literal —
/// docs/connections-surface.md §6.</para>
/// </summary>
internal static class ConnDiscoverPersistence
{
    /// <summary>
    /// Runs <paramref name="body"/> under the route's catch-all.
    /// </summary>
    /// <typeparam name="THandler">The calling handler, for the logger category.</typeparam>
    /// <typeparam name="TResponse">The route's response type.</typeparam>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="logMessage">The Node log line, verbatim (e.g. "[Connections] Search error").</param>
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
        catch (AppException appException)
            when (appException.StatusCode >= 500 && !cancellationToken.IsCancellationRequested)
        {
            // A cross-module port's own 500 — see the class summary. Node would have caught the
            // underlying Prisma error and answered its own literal, so this does the same.
            logger.Error(logMessage, appException);
            throw ConnErrors.ServerError(failureError);
        }
        catch (Exception exception)
            when (exception is not AppException && !cancellationToken.IsCancellationRequested)
        {
            logger.Error(logMessage, exception);
            throw ConnErrors.ServerError(failureError);
        }
    }
}

/// <summary>The caller and request-body checks the six endpoints in this file share.</summary>
internal static class ConnDiscoverCaller
{
    /// <summary>
    /// <c>401 {"error":"No token provided"}</c> — the literal <c>authCheck</c> body
    /// (<c>server/src/middleware/auth.js</c>), which is why this is a
    /// <see cref="BusinessException"/> and not <see cref="UnauthorizedException"/>; that one emits
    /// <c>{"error":"Unauthorized","message":…}</c>, a shape no connections route produces.
    ///
    /// <para>Unreachable behind <c>[Authorize]</c>, and kept so a handler cannot silently read a
    /// null user id as a filter value and answer 200 scoped to nobody.</para>
    /// </summary>
    /// <param name="currentUser">The ambient caller.</param>
    /// <returns>The caller's user id.</returns>
    public static string RequireUserId(ICurrentUser currentUser)
        => string.IsNullOrEmpty(currentUser.UserId)
            ? throw ConnErrors.Node("No token provided", 401)
            : currentUser.UserId;

    /// <summary>
    /// <c>value || null</c> for a JSON body field that Node then hands STRAIGHT to Prisma or to a
    /// string method.
    ///
    /// <para>Three outcomes, and each reproduces a different Node behaviour:</para>
    /// <list type="bullet">
    /// <item>falsy (absent, <c>null</c>, <c>false</c>, <c>0</c>, <c>""</c>) → <c>null</c>, which is
    /// what <c>x || null</c> stores;</item>
    /// <item>a JSON string → that string, untrimmed — none of these routes trims;</item>
    /// <item>truthy but not a string (a number, an object, an array) → an
    /// <see cref="InvalidOperationException"/>, because Node would either call <c>.replace()</c> on
    /// it (a TypeError) or pass it to Prisma as the wrong column type (a validation error). Both
    /// land in the route's own catch, which is exactly where
    /// <c>ConnDiscoverPersistence.RunAsync</c> sends this.</item>
    /// </list>
    /// </summary>
    /// <param name="value">The bound JSON value. <see cref="JsonValueKind.Undefined"/> is an absent key.</param>
    /// <param name="field">The body key name, for the log line only.</param>
    /// <returns>The string, or null.</returns>
    public static string? StringOrNull(JsonElement value, string field)
    {
        if (!ConnJs.IsTruthy(value)) return null;

        return ConnJs.AsString(value)
            ?? throw new InvalidOperationException(
                $"Body field '{field}' is a JSON {value.ValueKind}; connections.js hands this " +
                "value to a string method or to Prisma unchanged, which fails there too.");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/connections/search — connections.js:443-527
// ═════════════════════════════════════════════════════════════════════════════

/// <param name="Q">
/// <c>?q=</c>. The gate is <c>if (!q || q.length &lt; 2) return res.json([])</c> (:446-448) — a
/// short or missing term is <b>200 with an empty array</b>, never a 400. Length is counted in
/// UTF-16 code units on both runtimes, so a two-character Arabic term passes on both and a single
/// astral emoji (length 2) passes on both.
/// </param>
/// <param name="ClinicId">
/// <c>?clinicId=</c>, the FIRST half of this route's staff-context precedence (:455).
/// </param>
/// <param name="ClinicIdHeader">
/// The <c>X-Clinic-Id</c> header, the SECOND half. ⚠ The precedence is
/// <c>query.clinicId || header</c> here — <b>query first</b>. Only <c>GET /</c> reads the header
/// first (:37), and the axios interceptor sends the header on every request once a clinic is
/// active (client/src/services/api.js:31), so the difference is reachable whenever a caller also
/// passes <c>?clinicId=</c>. Do not substitute <c>IClinicContext</c>, which resolves header-first.
/// </param>
public sealed record ConnDiscoverSearchQuery(
    string? Q,
    string? ClinicId,
    string? ClinicIdHeader) : IRequest<IReadOnlyList<ConnDiscoverSearchItem>>;

/// <summary>
/// Port of <c>GET /api/connections/search</c> (connections.js:443-527) — "find someone to connect
/// to". A BARE JSON ARRAY, never an object and never null.
///
/// <para><b>What it searches.</b> <c>users</c>, and only <c>users</c> — never
/// <c>physician_profiles</c> and never <c>clinic_patients</c>. One type at a time: the caller's
/// own <c>userType</c> is inverted into <c>targetType</c> (:472), so a physician sees PATIENTs and
/// <i>everyone else</i> — patient, receptionist, staff, unknown claim — sees PHYSICIANs. A
/// receptionist with no clinic context therefore searches for doctors, which looks wrong on the
/// "Add patient" screen and is the contract.</para>
///
/// <para><b>How it matches.</b> A case-insensitive CONTAINS (Prisma
/// <c>{ contains: q, mode: 'insensitive' }</c>) over THREE columns OR'd together — <c>displayName</c>,
/// <c>email</c> and <c>phone</c> (:477-481). Substring, not prefix: "ali" matches "Khalid". The
/// phone arm is matched RAW, with no normalization on either side, so a stored "+20 100 1234567"
/// is not found by "1001234567".</para>
///
/// <para><b>What it excludes.</b> Only two things: the other user type, and <c>active: false</c>
/// (:475-476). It does <b>not</b> exclude the caller (the type filter does that incidentally, and
/// fails to for a dual-type account), does <b>not</b> exclude users already connected, and does
/// <b>not</b> exclude pending or rejected edges. Already-connected users come back carrying a
/// non-null <c>connectionStatus</c>, which is what the client renders a badge from.</para>
///
/// <para><b>The cap is a hard-coded <c>take: 10</c></b> (:492) with no <c>orderBy</c>, so Postgres
/// decides WHICH ten. The port orders by <c>created_at</c> then <c>id</c> — a recorded divergence
/// (docs/connections-surface.md §11.7), because an arbitrary ten is not reproducible.</para>
///
/// <para><b>The staff context can only ever widen, never fail the request</b> (:454-470). If the
/// caller is not a physician and a clinic id is present, an active staff row plus a resolvable
/// clinic physician flips <c>userType</c> to PHYSICIAN and points the enrichment sweep at the
/// DOCTOR's edges, so a receptionist sees the doctor's badges. Every failure along that path falls
/// through silently and the caller keeps their own type — which means a receptionist whose staff
/// row is inactive searches for physicians instead of patients, with no error.</para>
/// </summary>
public sealed class ConnDiscoverSearchHandler(
    ICurrentUser currentUser,
    IIdentityDirectory identity,
    IDoctorPatientConnectionStore connections,
    ConnStaffResolver staff,
    IAppLogger<ConnDiscoverSearchHandler> logger)
    : IRequestHandler<ConnDiscoverSearchQuery, IReadOnlyList<ConnDiscoverSearchItem>>
{
    /// <summary>The <c>take: 10</c> at connections.js:492.</summary>
    private const int ResultCap = 10;

    /// <summary>The <c>q.length &lt; 2</c> gate at connections.js:446.</summary>
    private const int MinimumQueryLength = 2;

    public Task<IReadOnlyList<ConnDiscoverSearchItem>> Handle(
        ConnDiscoverSearchQuery request, CancellationToken cancellationToken = default)
        => ConnDiscoverPersistence.RunAsync(
            logger, "[Connections] Search error", "Search failed", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<ConnDiscoverSearchItem>> HandleCore(
        ConnDiscoverSearchQuery request, CancellationToken cancellationToken)
    {
        // `if (!q || q.length < 2) return res.json([])` — 200 [], not 400 (:446-448). The empty
        // check runs before the caller is even read, so an unauthenticated-but-authorized caller
        // with a one-character term never reaches the user lookup.
        if (ConnJs.Truthy(request.Q) is not { } query || query.Length < MinimumQueryLength)
        {
            return [];
        }

        var userId = ConnDiscoverCaller.RequireUserId(currentUser);
        var userType = currentUser.UserType;
        string? actingAsPhysicianId = null;

        // `const staffClinicId = clinicId || req.headers['x-clinic-id']` (:455) — QUERY FIRST.
        var staffClinicId = ConnJs.Truthy(request.ClinicId) ?? ConnJs.Truthy(request.ClinicIdHeader);

        if (userType != "PHYSICIAN" && staffClinicId is not null)
        {
            // Every failure here FALLS THROUGH (:456-470). The resolver reports rather than
            // throws precisely so this route can ignore the reason.
            var resolution = await staff.ResolveAsync(userId, staffClinicId, cancellationToken);
            if (resolution.IsResolved)
            {
                actingAsPhysicianId = resolution.PhysicianUserId;
                userType = "PHYSICIAN"; // "act as physician for search" (:466)
            }
        }

        // `const targetType = userType === 'PHYSICIAN' ? 'PATIENT' : 'PHYSICIAN'` (:472).
        var targetType = userType == "PHYSICIAN" ? "PATIENT" : "PHYSICIAN";

        var users = await identity.SearchUsersAsync(
            new UserSearchFilter(
                query,
                MatchDisplayName: true,
                MatchEmail: true,
                MatchPhone: true,
                UserType: targetType,
                ActiveOnly: true,
                Take: ResultCap),
            cancellationToken);

        if (users.Count == 0) return [];

        // `const lookupId = actingAsPhysicianId || userId` (:496) — the PHYSICIAN's id on the
        // staff path, so a receptionist sees the doctor's connection badges and not their own.
        var lookupId = actingAsPhysicianId ?? userId;

        var edges = await connections.ListEdgesWithAsync(
            lookupId,
            [.. users.Select(u => u.Id)],
            cancellationToken);

        return
        [
            .. users.Select(u =>
            {
                // `existingConns.find(c => c.physicianUserId === u.id || c.patientUserId === u.id)`
                // (:508). ⚠ The FIRST edge whose EITHER end is this user, with no direction check
                // and no ordering — so a dual-role account that is both this caller's patient and
                // a physician connected to them can report the wrong edge's status. The store
                // returns both ends specifically so this quirk stays visible here.
                var edge = edges.FirstOrDefault(
                    c => c.PhysicianUserId == u.Id || c.PatientUserId == u.Id);

                return new ConnDiscoverSearchItem(
                    u.Id,
                    u.DisplayName,
                    u.Email,
                    u.Phone,
                    u.UserType,
                    u.CreatedAt,
                    edge is null ? null : edge.Status.ToString());
            })
        ];
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/connections/search-physicians — NO NODE ROUTE
// ═════════════════════════════════════════════════════════════════════════════

/// <param name="Q">
/// <c>?q=</c>. Same gate as the sibling route: shorter than two characters is <c>200 []</c>.
/// </param>
public sealed record ConnDiscoverSearchPhysiciansQuery(string? Q)
    : IRequest<IReadOnlyList<ConnDiscoverPhysicianSearchItem>>;

/// <summary>
/// <c>GET /api/connections/search-physicians</c> — <b>an endpoint connections.js does not
/// implement</b>, written deliberately rather than reproduced as the global 404.
///
/// <para><b>The evidence.</b> <c>grep -n "search-physicians" server/src/routes/connections.js</c>
/// returns nothing; there are 25 route registrations in that file and this is not one of them. The
/// only caller is <c>client/src/components/PatientOnboardingWizard.jsx:68</c>, whose
/// <c>catch { setSearchResults([]) }</c> turns Node's 404 into an empty result list, so the
/// wizard's doctor-search step has never returned a single row in production.</para>
///
/// <para><b>The decision</b> (docs/connections-surface.md §11.4, and the precedent of
/// <c>GET /api/visits/inbox</c>): implement it. The client already handles this shape, so a
/// working endpoint cannot break it, whereas reproducing the 404 would preserve a dead feature for
/// the sake of symmetry. It is a divergence and belongs in <c>PORT-STATUS.md</c>'s "Deliberate
/// divergences" table.</para>
///
/// <para><b>The shape</b> is <c>GET /search</c>'s physician branch — same <c>q.length &gt;= 2</c>
/// gate, same <c>take: 10</c>, same <c>active: true</c>, same case-insensitive CONTAINS over
/// display name, email and phone, same <c>connectionStatus</c> enrichment — plus the
/// <c>specialty</c> and <c>verified</c> the wizard renders, and an <c>id</c>/<c>userId</c> pair
/// that both carry the physician's USER id so both of the call site's fallbacks resolve.</para>
///
/// <para><b>No staff context.</b> The caller is a patient in the onboarding wizard, and the
/// endpoint's whole purpose is "find me a doctor", so unlike its sibling it does not invert the
/// caller's type and never acts as anybody else. A physician calling it searches for physicians.</para>
///
/// <para>The profile lookup is one call per hit rather than a batch, because
/// <see cref="IIdentityDirectory"/> exposes <c>GetPhysiciansAsync</c> keyed by physician PROFILE id
/// and this route only ever holds USER ids. It is bounded by the same hard cap of ten.</para>
/// </summary>
public sealed class ConnDiscoverSearchPhysiciansHandler(
    ICurrentUser currentUser,
    IIdentityDirectory identity,
    IDoctorPatientConnectionStore connections,
    IAppLogger<ConnDiscoverSearchPhysiciansHandler> logger)
    : IRequestHandler<ConnDiscoverSearchPhysiciansQuery, IReadOnlyList<ConnDiscoverPhysicianSearchItem>>
{
    /// <summary>Mirrors <c>GET /search</c>'s hard-coded <c>take: 10</c>.</summary>
    private const int ResultCap = 10;

    /// <summary>Mirrors <c>GET /search</c>'s <c>q.length &lt; 2</c> gate.</summary>
    private const int MinimumQueryLength = 2;

    public Task<IReadOnlyList<ConnDiscoverPhysicianSearchItem>> Handle(
        ConnDiscoverSearchPhysiciansQuery request, CancellationToken cancellationToken = default)
        // Reuses the sibling route's named 500, because this is its physician branch.
        => ConnDiscoverPersistence.RunAsync(
            logger, "[Connections] Search error", "Search failed", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<ConnDiscoverPhysicianSearchItem>> HandleCore(
        ConnDiscoverSearchPhysiciansQuery request, CancellationToken cancellationToken)
    {
        if (ConnJs.Truthy(request.Q) is not { } query || query.Length < MinimumQueryLength)
        {
            return [];
        }

        var userId = ConnDiscoverCaller.RequireUserId(currentUser);

        var users = await identity.SearchUsersAsync(
            new UserSearchFilter(
                query,
                MatchDisplayName: true,
                MatchEmail: true,
                MatchPhone: true,
                UserType: "PHYSICIAN",
                ActiveOnly: true,
                Take: ResultCap),
            cancellationToken);

        if (users.Count == 0) return [];

        var edges = await connections.ListEdgesWithAsync(
            userId,
            [.. users.Select(u => u.Id)],
            cancellationToken);

        var results = new List<ConnDiscoverPhysicianSearchItem>(users.Count);

        foreach (var user in users)
        {
            // A user whose userType is PHYSICIAN but who has no physician_profiles row is a real
            // state — the profile is created separately at registration — so this is null-tolerant
            // rather than a filter. The wizard renders `doc.specialty || 'General Practice'`.
            var profile = await identity.GetPhysicianByUserIdAsync(user.Id, cancellationToken);

            var edge = edges.FirstOrDefault(
                c => c.PhysicianUserId == user.Id || c.PatientUserId == user.Id);

            results.Add(new ConnDiscoverPhysicianSearchItem(
                user.Id,
                user.Id,
                user.DisplayName,
                user.Email,
                user.Phone,
                user.UserType,
                user.CreatedAt,
                profile?.Specialty,
                profile?.Verified ?? false,
                edge is null ? null : edge.Status.ToString()));
        }

        return results;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/connections/add-by-phone — connections.js:529-615
// ═════════════════════════════════════════════════════════════════════════════

/// <param name="Phone">
/// <c>body.phone</c>. Bound as a raw <see cref="JsonElement"/> because Node applies JavaScript
/// truthiness (<c>if (!phone)</c>, :537) and then a string method (<c>.replace</c>, :539): an
/// absent key and an empty string are both the 400, while a numeric <c>phone</c> passes the gate
/// and is a TypeError inside the route's own catch.
/// </param>
public sealed record ConnDiscoverAddByPhoneBody(JsonElement Phone);

/// <param name="CallerUserId">The authenticated caller — the physician, on the only path that gets past the gate.</param>
/// <param name="CallerUserType">
/// <c>req.user.userType</c>, compared as a string. Anything other than the exact value
/// <c>"PHYSICIAN"</c> is the 403, including a null claim.
/// </param>
/// <param name="Body">
/// The request body, or null for an empty one. The controller must bind it with
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>: Express hands an empty request
/// <c>{}</c> and this route then answers its own 400, whereas a plain <c>[FromBody] X?</c> answers
/// the binder's <c>{"error":"Validation failed"}</c>.
/// </param>
public sealed record ConnDiscoverAddByPhoneCommand(
    string CallerUserId,
    string? CallerUserType,
    ConnDiscoverAddByPhoneBody? Body) : IRequest<object>;

/// <summary>
/// Port of <c>POST /api/connections/add-by-phone</c> (connections.js:529-615) — the physician
/// types a phone number and is either connected instantly or sent to a create form.
///
/// <para><b>Three success shapes, all 200</b>, which is why the response type is <c>object</c>:
/// <see cref="ConnDiscoverAddByPhoneNotFoundResponse"/> has no <c>patient</c> key,
/// <see cref="ConnDiscoverAddByPhoneExistingResponse"/> has <c>connectionStatus</c> and no
/// <c>connection</c>, and <see cref="ConnDiscoverAddByPhoneCreatedResponse"/> has <c>connection</c>
/// and no <c>connectionStatus</c>. Folding them into one nullable DTO would put null keys on the
/// wire that Node never sends.</para>
///
/// <para><b>The error order is gate-then-body</b> (:533-538): a non-physician gets
/// <c>403 "Only physicians can add patients"</c> even with an empty body, and only then does a
/// missing phone become <c>400 "Phone number is required"</c>. There is no staff fallback on this
/// route at all — a receptionist is refused outright, unlike <c>create-patient</c> two handlers
/// below.</para>
///
/// <para><b>Phone matching is the trap.</b> <c>phone.replace(/[\s\-]/g, '')</c> (:539) strips
/// whitespace and ASCII hyphens and NOTHING else, then the result is compared to the stored column
/// for exact equality (:542-549). A leading <c>+</c>, parentheses and dots all survive on the
/// request side and are not stripped on the stored side, so <c>"+20 (10) 123-4567"</c> becomes
/// <c>"+20(10)1234567"</c> and does not match a stored <c>"+201012345 67"</c> either. There is no
/// country-code handling and no libphonenumber anywhere in the Node backend. Reproduced exactly:
/// "improving" it would silently connect physicians to patients Node leaves unmatched. The
/// normalized value is also echoed on the <c>found: false</c> branch, so it is on the wire.</para>
///
/// <para><b>Three outcomes of the lookup.</b> No match — including a match that is the wrong
/// <c>userType</c> or is <c>active: false</c> — is <c>{ found: false, phone }</c>, never a 404.
/// A match that is already connected returns the existing edge's status and writes nothing. A
/// match that is not connected gets an ACCEPTED edge with no handshake: this route grants a
/// physician access to a patient's chart without the patient's consent, by design, because it is
/// the in-consultation "add the person in front of me" flow.</para>
///
/// <para><b>The caller CAN match themselves.</b> There is no self-check anywhere in the route, so
/// a PATIENT-type account is required for a match and a physician's own row cannot qualify — but a
/// dual account whose <c>userType</c> is PATIENT and whose id is the caller's would create a
/// self-edge. Node has no guard and neither does this.</para>
///
/// <para><b>ONLY the notification is best-effort</b>, and the boundary is one line narrower than it
/// looks. <c>.catch(() => {})</c> at connections.js:593 is attached to the
/// <c>createNotification(...)</c> promise and to nothing else. The physician display-name read at
/// :583-586 is a SEPARATE, PRECEDING statement with no catch of its own, so a failure there reaches
/// the route's catch at :602-604 and answers <c>500 {"error":"Failed to add patient"}</c> — AFTER
/// the ACCEPTED edge at :571-579 has already committed. The read therefore runs under the file
/// guard here and only <c>PublishAsync</c> sits in the nested try/catch; folding the read in too
/// would return 200 where Node returns 500 (docs/connections-surface.md §10.9).</para>
/// </summary>
public sealed class ConnDiscoverAddByPhoneHandler(
    IConnectionsDbContext database,
    IDoctorPatientConnectionStore connections,
    IIdentityDirectory identity,
    INotificationPublisher notifications,
    IAppLogger<ConnDiscoverAddByPhoneHandler> logger)
    : IRequestHandler<ConnDiscoverAddByPhoneCommand, object>
{
    public Task<object> Handle(
        ConnDiscoverAddByPhoneCommand request, CancellationToken cancellationToken = default)
        => ConnDiscoverPersistence.RunAsync(
            logger, "[Connections] Add by phone error", "Failed to add patient", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<object> HandleCore(
        ConnDiscoverAddByPhoneCommand request, CancellationToken cancellationToken)
    {
        // :534-536 — the role gate runs before the body is read at all.
        if (request.CallerUserType != "PHYSICIAN")
        {
            throw ConnErrors.Forbidden("Only physicians can add patients");
        }

        var phoneValue = request.Body?.Phone ?? default;

        // `if (!phone) return res.status(400)` (:538). JavaScript truthiness, so "" is the 400 too.
        if (!ConnJs.IsTruthy(phoneValue))
        {
            throw ConnErrors.BadRequest("Phone number is required");
        }

        var rawPhone = ConnDiscoverCaller.StringOrNull(phoneValue, "phone")!;
        var normalizedPhone = ConnJs.NormalizePhone(rawPhone);

        // `findFirst({ phone: normalizedPhone, userType: 'PATIENT', active: true })` (:542-549).
        var patient = await identity.GetUserByPhoneAsync(
            normalizedPhone, userType: "PATIENT", activeOnly: true, ct: cancellationToken);

        // :552-554 — no patient is a 200 with a two-key body, not a 404. The client opens its
        // create-patient form prefilled with the NORMALIZED number it gets back here.
        if (patient is null)
        {
            return new ConnDiscoverAddByPhoneNotFoundResponse(false, normalizedPhone);
        }

        var account = new ConnDiscoverPatientAccount(
            patient.Id,
            patient.DisplayName,
            patient.Email,
            patient.Phone,
            patient.ProfilePictureUrl);

        // ⚠ `findFirst({ where: { physicianUserId, patientUserId } })` (:555-557) — NO
        // subprofileId key, so this matches the parent edge OR any dependant edge. A physician
        // connected only to this patient's child is reported "already connected" and never gets a
        // parent edge. POST /request and connect-by-pin pass subprofileId explicitly and behave
        // differently; each route's own choice is the contract (surface §10.6).
        var existing = await connections.FindAnyPairAsync(
            request.CallerUserId, patient.Id, cancellationToken);

        if (existing is not null)
        {
            return new ConnDiscoverAddByPhoneExistingResponse(
                true,
                true,
                account,
                existing.Status.ToString(),
                $"{patient.DisplayName} is already in your patient list");
        }

        // :567-575 — ACCEPTED directly, with connectedAt stamped now. No PENDING state, no
        // patient consent, and initiatedBy is the physician.
        var connection = DoctorPatientConnection.CreateAccepted(
            request.CallerUserId, patient.Id, request.CallerUserId);

        connections.Add(connection);
        await database.SaveChangesAsync(cancellationToken);

        // :583-586, OUTSIDE the `.catch(() => {})` that covers only createNotification. A failure
        // here is the route's own 500 even though the edge above is already committed, so it runs
        // under the file guard rather than inside the best-effort block.
        var physician = await identity.GetUserAsync(request.CallerUserId, cancellationToken);
        var physicianName = ConnJs.Truthy(physician?.DisplayName) ?? "Your Doctor";

        await NotifyBestEffortAsync(
            request.CallerUserId, patient.Id, physicianName, cancellationToken);

        return new ConnDiscoverAddByPhoneCreatedResponse(
            true,
            false,
            account,
            new ConnDiscoverAddedConnection(connection.Id, connection.Status.ToString()),
            $"{patient.DisplayName} added to your patient list");
    }

    /// <summary>
    /// The <c>createNotification(...).catch(() => {})</c> at connections.js:587-593 — and ONLY
    /// that call. The <c>.catch</c> is attached to this one promise, so nothing inside here can
    /// change the committed 200.
    ///
    /// <para>The physician display-name read (:583-586) is deliberately NOT in here: it is a
    /// separate statement in Node with no catch, so it belongs under the file guard. Its result is
    /// passed in already reduced to <c>physician?.displayName || 'Your Doctor'</c>, which is why an
    /// unresolvable physician still sends a notification, with the generic name.</para>
    /// </summary>
    /// <param name="physicianUserId">The acting physician's user id, for the payload.</param>
    /// <param name="patientUserId">The notified patient's user id.</param>
    /// <param name="physicianName">
    /// The resolved <c>physician?.displayName || 'Your Doctor'</c>, read by the caller.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task NotifyBestEffortAsync(
        string physicianUserId,
        string patientUserId,
        string physicianName,
        CancellationToken cancellationToken)
    {
        try
        {
            await notifications.PublishAsync(
                new NotificationRequest(
                    patientUserId,
                    "CONNECTION_ACCEPTED",
                    "New Doctor Connection",
                    $"Dr. {physicianName} has added you as a patient.",
                    JsonSerializer.Serialize(
                        new Dictionary<string, string> { ["physicianUserId"] = physicianUserId }),
                    SendEmail: false),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Node discards this outcome entirely. Logged rather than silent, because a
            // notification pipeline that is down should be visible somewhere.
            logger.Warning("[Connections] Add by phone notification failed", exception);
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/connections/create-patient — connections.js:617-735
// ═════════════════════════════════════════════════════════════════════════════

/// <param name="Name">
/// <c>body.name</c>. The gate is bare truthiness — <c>if (!name)</c> (:642) — with <b>no
/// <c>.trim()</c></b>, unlike <c>POST /clinic-patients</c> (:751), so a name of <c>"   "</c> is
/// accepted here and refused there. Two adjacent create routes, two different validations.
/// </param>
/// <param name="Phone">
/// <c>body.phone</c>. Stored RAW (<c>phone || null</c>, :673) — this route never applies
/// <c>add-by-phone</c>'s normalization, so a number typed with spaces is stored with them and will
/// not be found by the phone lookup afterwards.
/// </param>
/// <param name="Email">
/// <c>body.email</c>. When falsy, the route synthesizes
/// <c>patient-&lt;digits-of-phone&gt;@tebrazi.local</c> (:646) and that becomes the account's
/// permanent login identifier.
/// </param>
/// <param name="DateOfBirth">
/// <c>body.dateOfBirth</c>, written to the SELF subprofile and NOT to the profile (:695).
/// </param>
/// <param name="Gender">
/// <c>body.gender</c>, also on the SELF subprofile (:696), passed to a Prisma enum column RAW —
/// an unrecognised value is a database error and therefore this route's 500.
/// </param>
/// <param name="Address">
/// <c>body.address</c>, written to the patient PROFILE (:687).
/// </param>
/// <param name="WhatsappNumber">
/// <c>body.whatsappNumber</c>, written to the patient PROFILE (:688).
/// </param>
/// <param name="ClinicId">
/// <c>body.clinicId</c>, read ONLY on the staff path (:627) and ignored entirely when the caller
/// is a physician.
/// </param>
public sealed record ConnDiscoverCreatePatientBody(
    JsonElement Name,
    JsonElement Phone,
    JsonElement Email,
    JsonElement DateOfBirth,
    JsonElement Gender,
    JsonElement Address,
    JsonElement WhatsappNumber,
    JsonElement ClinicId);

/// <param name="CallerUserId">The authenticated caller — the physician, or the staff member acting for one.</param>
/// <param name="CallerUserType">
/// <c>req.user.userType</c> as a string. Exactly <c>"PHYSICIAN"</c> takes the direct path;
/// everything else takes the staff fallback.
/// </param>
/// <param name="ClinicIdHeader">
/// The <c>X-Clinic-Id</c> header, the SECOND half of this route's precedence:
/// <c>req.body.clinicId || req.headers['x-clinic-id']</c> (:627) — <b>body first</b>. Do not
/// substitute <c>IClinicContext</c>, which resolves header-first.
/// </param>
/// <param name="Body">
/// The request body, or null for an empty one. Needs
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c> — a physician posting nothing
/// must reach this route's <c>400 "Patient name is required"</c>, not the binder's generic body.
/// </param>
public sealed record ConnDiscoverCreatePatientCommand(
    string CallerUserId,
    string? CallerUserType,
    string? ClinicIdHeader,
    ConnDiscoverCreatePatientBody? Body) : IRequest<ConnDiscoverCreatePatientResponse>;

/// <summary>
/// Port of <c>POST /api/connections/create-patient</c> (connections.js:617-735) — a physician or
/// receptionist creates a whole PATIENT ACCOUNT for somebody who has never used the app, and is
/// connected to it immediately.
///
/// <para><b>This route writes to four tables across three modules</b>, and only one of them is
/// Connections':</para>
/// <list type="number">
/// <item><c>users</c> — through <see cref="IPatientAccountProvisioner"/> (Identity owns it). The
/// row gets <c>role = USER</c>, <c>userType = PATIENT</c> and a 16-byte random hex password,
/// bcrypt-hashed at cost 10 and then DISCARDED: it is never returned, logged or emailed, and the
/// patient claims the account through password reset (:664-678). <c>active</c> is left at the
/// column default — the route never sets it.</item>
/// <item><c>patient_profiles</c> — through <see cref="IPatientProfileProvisioner"/> (Patients owns
/// it), carrying ONLY <c>address</c> and <c>whatsappNumber</c> (:681-689).</item>
/// <item><c>family_subprofiles</c> — the SELF dependant, in the same provisioner call. Its
/// <c>name</c> is the SAME <c>name</c> the account was created with, and <c>dateOfBirth</c> and
/// <c>gender</c> land HERE rather than on the profile (:690-698).</item>
/// <item><c>doctor_patient_connections</c> — an ACCEPTED edge, this module's own write
/// (:700-709).</item>
/// </list>
///
/// <para><b>Node wraps none of that in a transaction</b> — four sequential creates, and a failure
/// at step three leaves a user with no profile. The port reproduces the ordering rather than
/// inventing atomicity Node does not have; each provisioner commits through its own module's unit
/// of work, so this handler produces three commits where Node produces three statements.</para>
///
/// <para><b><c>initiatedBy</c> is the PHYSICIAN, not the caller</b> (:706). On the staff path the
/// receptionist's id appears nowhere in the resulting row — unlike <c>POST /request</c>, which
/// records the staff member. Do not "fix" the asymmetry.</para>
///
/// <para><b>The error order is exact and testable</b> (:621-654): staff resolution runs FIRST, so
/// a receptionist with no clinic id gets <c>400 "clinicId is required for staff"</c> even with a
/// completely empty body; then <c>400 "Patient name is required"</c>; then
/// <c>400 "Phone or email is required"</c>; then the duplicate probe's
/// <c>409 "A patient with this email or phone already exists. Search for them instead."</c></para>
///
/// <para><b>The duplicate probe has no <c>userType</c> and no <c>active</c> filter</b> (:649-651):
/// a physician account or a deactivated account holding that email or phone is a collision, and
/// the 409 tells the caller to "search for them instead" — which the search endpoint will not find,
/// because it filters on both. That dead end is the contract.</para>
///
/// <para><b>The notification is NOT best-effort</b> (:713-719) — it is <c>await</c>ed with no
/// <c>.catch</c>, so in Node a notification failure really is
/// <c>500 "Failed to create patient"</c> AFTER all four rows are written. <c>INotificationPublisher</c>
/// never throws by design, so this port answers 200 where Node can answer 500. That is a recorded
/// divergence (docs/connections-surface.md §11.7) and the reason this call is NOT wrapped in a
/// nested try/catch: wrapping it would make the divergence deliberate at the wrong layer.</para>
///
/// <para><b>200, not 201</b>, and the body carries four user columns only.</para>
/// </summary>
public sealed class ConnDiscoverCreatePatientHandler(
    IConnectionsDbContext database,
    IDoctorPatientConnectionStore connections,
    IIdentityDirectory identity,
    IPatientAccountProvisioner accounts,
    IPatientProfileProvisioner profiles,
    INotificationPublisher notifications,
    ConnStaffResolver staff,
    IAppLogger<ConnDiscoverCreatePatientHandler> logger)
    : IRequestHandler<ConnDiscoverCreatePatientCommand, ConnDiscoverCreatePatientResponse>
{
    public Task<ConnDiscoverCreatePatientResponse> Handle(
        ConnDiscoverCreatePatientCommand request, CancellationToken cancellationToken = default)
        => ConnDiscoverPersistence.RunAsync(
            logger, "[Connections] Create patient error", "Failed to create patient",
            cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnDiscoverCreatePatientResponse> HandleCore(
        ConnDiscoverCreatePatientCommand request, CancellationToken cancellationToken)
    {
        var body = request.Body;

        // ── 1. Who is the physician? (:621-639) ──────────────────────────────
        // This runs BEFORE any body validation, so a staff caller with no clinic id is a 400
        // even when the body is completely empty.
        var physicianUserId = await ResolvePhysicianAsync(request, cancellationToken);

        // ── 2. Body gates, in Node's order (:641-644) ────────────────────────
        var nameValue = body?.Name ?? default;
        if (!ConnJs.IsTruthy(nameValue))
        {
            throw ConnErrors.BadRequest("Patient name is required");
        }

        var phoneValue = body?.Phone ?? default;
        var emailValue = body?.Email ?? default;

        if (!ConnJs.IsTruthy(phoneValue) && !ConnJs.IsTruthy(emailValue))
        {
            throw ConnErrors.BadRequest("Phone or email is required");
        }

        var name = ConnDiscoverCaller.StringOrNull(nameValue, "name")!;
        var phone = ConnDiscoverCaller.StringOrNull(phoneValue, "phone");
        var email = ConnDiscoverCaller.StringOrNull(emailValue, "email");

        // `email || `patient-${phone.replace(/[^0-9]/g, '')}@tebrazi.local`` (:646). ConnJs
        // .DigitsOnly, NOT NormalizePhone — the two strip different characters and confusing them
        // changes the generated address, which is the patient's permanent login. `phone` cannot be
        // null here: the gate above guarantees one of the two is truthy.
        var patientEmail = email ?? $"patient-{ConnJs.DigitsOnly(phone!)}@tebrazi.local";

        // ── 3. Duplicate probe (:648-653) ────────────────────────────────────
        // No userType filter and no active filter; a null/empty phone drops that arm.
        if (await identity.ExistsByEmailOrPhoneAsync(patientEmail, phone, cancellationToken))
        {
            throw ConnErrors.Conflict(
                "A patient with this email or phone already exists. Search for them instead.");
        }

        // ── 4. The physician's row, for the tenant and the notification name (:663-666) ──
        // Node selects BOTH columns off the PHYSICIAN in one findUnique:
        //   `select: { currentOrganizationId: true, displayName: true }`
        // and then writes `currentOrganizationId: physician?.currentOrganizationId || null` (:676).
        //
        // ⚠ It must be the PHYSICIAN's `users.current_organization_id`, and NOT the caller's JWT
        // `organizationId` claim, which is a DIFFERENT column: the claim is
        // `memberships[0].organization.id` frozen at login (auth.js:140-147; LoginHandler.cs
        // computes `primaryOrganization` identically). The two differ for any physician who belongs
        // to more than one organization, whose current organization is not their first membership,
        // or who switched tenants after the token was minted — and on the staff path the claim is
        // the receptionist's tenant, not the doctor's. Getting this wrong files a clinical account
        // in the wrong tenant silently, with the route still answering 200.
        //
        // Two reads where Node does one: UserSummary does not carry the column, and widening that
        // record would touch every construction site in eight modules.
        var physician = await identity.GetUserAsync(physicianUserId, cancellationToken);
        var physicianOrganizationId = await identity.GetCurrentOrganizationIdAsync(
            physicianUserId, cancellationToken);

        // ── 5. The four writes, in Node's order (:656-709) ───────────────────
        var newPatient = await accounts.CreatePatientAccountAsync(
            name,
            patientEmail,
            phone,
            // `physician?.currentOrganizationId || null` (:676) — `||`, so "" becomes null too.
            ConnJs.Truthy(physicianOrganizationId),
            cancellationToken);

        // `dateOfBirth ? new Date(dateOfBirth) : null` (:695). Node does not degrade an
        // unparseable value to null — it produces an Invalid Date that Prisma rejects — so a
        // non-empty value that will not parse must become this route's 500, not a silent null.
        var dateOfBirthRaw = ConnDiscoverCaller.StringOrNull(body?.DateOfBirth ?? default, "dateOfBirth");
        if (!ConnDates.TryParse(dateOfBirthRaw, out var dateOfBirth))
        {
            throw new InvalidOperationException(
                $"dateOfBirth '{dateOfBirthRaw}' is not a date; connections.js:695 hands the " +
                "resulting Invalid Date to Prisma, which rejects it into this route's own 500.");
        }

        await profiles.CreateProfileWithSelfSubprofileAsync(
            newPatient.Id,
            ConnDiscoverCaller.StringOrNull(body?.Address ?? default, "address"),
            ConnDiscoverCaller.StringOrNull(body?.WhatsappNumber ?? default, "whatsappNumber"),
            name,
            dateOfBirth,
            // `gender || null` written RAW into a Prisma enum column (:696). Not pre-sanitized:
            // an unrecognised value must fail, as it does in Node.
            ConnDiscoverCaller.StringOrNull(body?.Gender ?? default, "gender"),
            cancellationToken);

        // initiatedBy is the PHYSICIAN even on the staff path (:706).
        connections.Add(DoctorPatientConnection.CreateAccepted(
            physicianUserId, newPatient.Id, physicianUserId));

        await database.SaveChangesAsync(cancellationToken);

        // ── 6. The notification (:712-719) ───────────────────────────────────
        // Deliberately NOT in a nested try/catch: Node awaits this with no .catch, so a failure
        // is genuinely the route's 500 there. INotificationPublisher never throws, which is the
        // recorded divergence rather than a swallowed error here.
        var physicianName = ConnJs.Truthy(physician?.DisplayName) ?? "Your Doctor";

        await notifications.PublishAsync(
            new NotificationRequest(
                newPatient.Id,
                "CONNECTION_ACCEPTED",
                "Welcome to Tebrazi",
                $"Dr. {physicianName} has added you as a patient.",
                JsonSerializer.Serialize(
                    new Dictionary<string, string> { ["physicianUserId"] = physicianUserId }),
                SendEmail: false),
            cancellationToken);

        return new ConnDiscoverCreatePatientResponse(
            true,
            new ConnDiscoverCreatedPatient(
                newPatient.Id,
                newPatient.DisplayName,
                newPatient.Email,
                newPatient.Phone),
            "Patient created and connected successfully");
    }

    /// <summary>
    /// <c>connections.js:621-639</c> — the caller IS the physician, or a staff member acting for
    /// one.
    ///
    /// <para>This route's failure mapping is its own: <c>NoClinicId</c> is
    /// <c>400 "clinicId is required for staff"</c>, <c>NotStaff</c> is <c>403 "Not authorized"</c>,
    /// and BOTH a missing clinic and a missing clinic physician collapse into
    /// <c>404 "Clinic physician not found"</c> — because Node tests
    /// <c>if (!clinic?.physician?.userId)</c> in one expression (:638). Note the literal is "Clinic
    /// physician not found", not <c>POST /clinic-patients</c>' "Clinic not found"; two adjacent
    /// routes, two different 404 bodies.</para>
    /// </summary>
    /// <param name="request">The command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The physician's USER id.</returns>
    private async Task<string> ResolvePhysicianAsync(
        ConnDiscoverCreatePatientCommand request, CancellationToken cancellationToken)
    {
        if (request.CallerUserType == "PHYSICIAN")
        {
            return request.CallerUserId;
        }

        // `req.body.clinicId || req.headers['x-clinic-id']` (:627) — BODY first on this route.
        var clinicId =
            ConnDiscoverCaller.StringOrNull(request.Body?.ClinicId ?? default, "clinicId")
            ?? ConnJs.Truthy(request.ClinicIdHeader);

        var resolution = await staff.ResolveAsync(request.CallerUserId, clinicId, cancellationToken);

        return resolution.Outcome switch
        {
            ConnStaffOutcome.Resolved => resolution.PhysicianUserId!,
            ConnStaffOutcome.NoClinicId => throw ConnErrors.BadRequest("clinicId is required for staff"),
            ConnStaffOutcome.NotStaff => throw ConnErrors.Forbidden("Not authorized"),
            _ => throw ConnErrors.NotFound("Clinic physician not found")
        };
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/connections/invite-email — connections.js:969-1009
// ═════════════════════════════════════════════════════════════════════════════

/// <param name="Email">
/// <c>body.email</c>, the recipient. <b>Never validated as an address</b> — Node applies bare
/// truthiness (:972) and passes the value straight to the mail transport, so an invalid address
/// either bounces silently or surfaces as this route's 500, depending on the transport.
/// </param>
public sealed record ConnDiscoverInviteEmailBody(JsonElement Email);

/// <param name="CallerUserId">
/// The authenticated caller. <b>The route never checks that this is a physician</b> — any
/// authenticated user can send a "Dr. {my name} invited you" email with their own id embedded in
/// the signup link. Reproduced, not gated.
/// </param>
/// <param name="Body">
/// The request body, or null. Needs
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c> so an empty request reaches this
/// route's own <c>400 "Email is required"</c>.
/// </param>
public sealed record ConnDiscoverInviteEmailCommand(
    string CallerUserId,
    ConnDiscoverInviteEmailBody? Body) : IRequest<ConnDiscoverInviteEmailResponse>;

/// <summary>
/// Port of <c>POST /api/connections/invite-email</c> (connections.js:969-1009) — mail a signup
/// link to somebody who is not on the platform.
///
/// <para><b>It writes nothing.</b> No invitation row, no token record, no connection: the entire
/// product of this endpoint is one email. The "token" it embeds is
/// <c>sha256("physician-invite-" + userId)</c> truncated to 16 hex characters — a deterministic
/// function of the caller's own id with no secret, no salt and no expiry, and the same URL carries
/// that id in clear beside it. It is an attribution marker, not a credential, and hardening it
/// would invalidate every link already sent.</para>
///
/// <para><b>The base URL comes from configuration, not from a literal.</b> Node reads
/// <c>process.env.CLIENT_URL</c> and falls back to <c>http://localhost:5174</c> (:982); the port
/// reads <c>Client:Url</c> (environment variable <c>Client__Url</c>) through
/// <see cref="ConnClientUrls"/>, which carries the same fallback. ⚠ Note the QR route falls back to
/// port <b>5173</b> instead — a difference in the Node source, not a typo, and both are
/// reproduced.</para>
///
/// <para><b>Two different "unknown doctor" fallbacks in one file.</b> The subject and the HTML body
/// use <c>physician?.displayName || 'Your Doctor'</c>; the sibling <c>GET /invite-link</c> uses
/// <c>|| 'Doctor'</c>. And the plain-text alternative (:997) has <b>no fallback at all</b>, so Node
/// interpolates the JavaScript string <c>"undefined"</c> when the caller's user row cannot be
/// read. That is reproduced below rather than quietly improved.</para>
///
/// <para><b>The HTML is not escaped anywhere in Node</b> (:991) — the display name goes straight
/// into the markup. Escaping it here would change the bytes of every invite for a name containing
/// an apostrophe, so the template interpolates raw. The indentation and the leading and trailing
/// whitespace of the template literal are reproduced exactly, which is why the body is built from
/// explicit <c>\n</c>-terminated lines rather than from a verbatim string: a verbatim string would
/// embed this file's CRLF line endings where Node embeds LF.</para>
///
/// <para><b>⚠ A TRANSPORT FAILURE IS A 200, NOT A 500.</b> Node's <c>await sendEmail(...)</c>
/// (:985-998) is un-caught, but <c>sendEmail</c> <b>cannot reject</b>:
/// <c>server/src/services/emailService.js:92-96</c> opens with <c>await getTransport()</c> — whose
/// every throwing step (<c>require('resend')</c>, <c>new Resend</c>, <c>require('nodemailer')</c>,
/// <c>createTransport</c>) is individually try/caught at :26-64 and which always ends at the console
/// fallback at :60-65 — and then wraps the whole remaining body in a <c>try</c> that closes at
/// :140-143 with <c>return { success: false, error: … }</c>. The Resend branch does not even reach
/// that catch: it returns <c>{ success: false }</c> directly at :109. So a rejected Resend send and
/// a thrown SMTP send both answer <c>200 {"success":true,"message":"Invitation sent"}</c>, and the
/// route's own catch at :1001-1004 is reachable only through the <c>findUnique</c> above.</para>
///
/// <para><see cref="IEmailSender"/> DOES throw, so the send is wrapped in a swallow-all block below
/// rather than left under the file guard. Leaving it unguarded reads as fidelity and is not: it
/// answers 200 today only because <c>ConsoleEmailSender</c> never throws, and would start answering
/// 500 the moment a real transport is registered — the one deployment where it matters.</para>
/// </summary>
public sealed class ConnDiscoverInviteEmailHandler(
    IIdentityDirectory identity,
    IEmailSender email,
    ConnClientUrls clientUrls,
    IAppLogger<ConnDiscoverInviteEmailHandler> logger)
    : IRequestHandler<ConnDiscoverInviteEmailCommand, ConnDiscoverInviteEmailResponse>
{
    public Task<ConnDiscoverInviteEmailResponse> Handle(
        ConnDiscoverInviteEmailCommand request, CancellationToken cancellationToken = default)
        => ConnDiscoverPersistence.RunAsync(
            logger, "[Connections] Invite email error", "Failed to send invitation",
            cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnDiscoverInviteEmailResponse> HandleCore(
        ConnDiscoverInviteEmailCommand request, CancellationToken cancellationToken)
    {
        var emailValue = request.Body?.Email ?? default;

        // `if (!email) return res.status(400)` (:972).
        if (!ConnJs.IsTruthy(emailValue))
        {
            throw ConnErrors.BadRequest("Email is required");
        }

        var recipient = ConnDiscoverCaller.StringOrNull(emailValue, "email")!;

        // :974-977 — the caller's own row. A miss is not an error; it degrades the name.
        var physician = await identity.GetUserAsync(request.CallerUserId, cancellationToken);
        var physicianName = ConnJs.Truthy(physician?.DisplayName) ?? "Your Doctor";

        var signupUrl = clientUrls.SignupUrl(request.CallerUserId);

        // Node's sendEmail always RESOLVES — see the class summary. Every send outcome, including
        // a rejected Resend call and a thrown SMTP call, answers this route's 200. IEmailSender
        // throws, so the swallow-all is what reproduces the contract; without it a live transport
        // outage would answer 500 where Node answers 200.
        try
        {
            await email.SendAsync(
                new EmailMessage(
                    recipient,
                    $"Dr. {physicianName} invited you to Tebrazi",
                    InviteHtml(physicianName, signupUrl),
                    // ⚠ `Dr. ${physician?.displayName} invited you…` (:997) — NO `|| 'Your Doctor'`
                    // on this line, unlike the subject and the HTML two lines above. When the
                    // caller's row cannot be read, JavaScript interpolates the literal "undefined"
                    // and the port does the same. Not on the wire, and not silently corrected.
                    physician is null
                        ? $"Dr. undefined invited you to Tebrazi. Sign up at: {signupUrl}"
                        : $"Dr. {physician.DisplayName} invited you to Tebrazi. Sign up at: {signupUrl}"),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Node discards the result object entirely. Logged rather than silent, because a mail
            // transport that is down should be visible somewhere — but never on the wire.
            logger.Warning("[Connections] Invite email transport failed", exception);
        }

        return new ConnDiscoverInviteEmailResponse(true, "Invitation sent");
    }

    /// <summary>
    /// The template literal at connections.js:988-996, byte for byte: a leading newline, the outer
    /// <c>div</c> at 16 spaces, its five children at 20, the closing tag at 16, and a trailing
    /// newline plus 12 spaces before the closing backtick.
    ///
    /// <para>Built from explicit <c>"\n"</c>-terminated segments rather than a verbatim or raw
    /// string literal so the line endings are LF regardless of how this source file is stored on
    /// disk. Neither interpolated value is HTML-escaped, matching Node.</para>
    /// </summary>
    /// <param name="physicianName">The inviting physician's display name, or "Your Doctor".</param>
    /// <param name="signupUrl">The signup link, already built.</param>
    /// <returns>The HTML body.</returns>
    private static string InviteHtml(string physicianName, string signupUrl)
        => "\n"
         + "                <div style=\"font-family:Arial,sans-serif;max-width:500px;margin:0 auto;padding:24px\">\n"
         + "                    <h2 style=\"color:#E0AA3E\">You've been invited to Tebrazi</h2>\n"
         + "                    <p>Dr. <strong>" + physicianName + "</strong> has invited you to join Tebrazi — your secure digital health platform.</p>\n"
         + "                    <p>Create your account to connect with your doctor, access your medical records, prescriptions, and more.</p>\n"
         + "                    <a href=\"" + signupUrl + "\" style=\"display:inline-block;padding:12px 28px;background:#E0AA3E;color:#000;text-decoration:none;border-radius:8px;font-weight:bold;margin:16px 0\">Create Account</a>\n"
         + "                    <p style=\"color:#888;font-size:12px\">If you didn't expect this invitation, you can safely ignore this email.</p>\n"
         + "                </div>\n"
         + "            ";
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/connections/invite-link — connections.js:1011-1040
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// <c>GET /api/connections/invite-link</c> — no parameters at all. The route reads nothing but
/// <c>req.user.id</c>: there is no query string, no body and no header input, so the response is a
/// pure function of the caller's identity and the configured client URL.
/// </summary>
public sealed record ConnDiscoverInviteLinkQuery : IRequest<ConnDiscoverInviteLinkResponse>;

/// <summary>
/// Port of <c>GET /api/connections/invite-link</c> (connections.js:1011-1040) — the shareable
/// version of <c>invite-email</c>, for a physician who would rather paste the link into WhatsApp.
///
/// <para>Same token and same URL as <see cref="ConnDiscoverInviteEmailHandler"/>, produced by the
/// same <see cref="ConnClientUrls"/> so the two cannot drift. Writes nothing, and is idempotent
/// forever: the token is <c>sha256("physician-invite-" + userId)</c> truncated to 16 hex
/// characters, with no expiry and no stored record, so two calls a year apart return identical
/// bytes.</para>
///
/// <para><b>The fallback name is "Doctor", NOT "Your Doctor"</b> (:1024) — the adjacent
/// <c>invite-email</c> route uses the longer literal in its subject line. Two handlers, twenty
/// lines apart, two different strings for the same idea; both are reproduced verbatim.</para>
///
/// <para><b>No physician gate.</b> Any authenticated caller gets a link with their own id in it,
/// exactly as in Node.</para>
///
/// <para>Node computes the token BEFORE the user lookup (:1013-1018), which matters only because a
/// lookup failure still leaves the token computed; the ordering is reproduced for readability and
/// has no wire effect.</para>
/// </summary>
public sealed class ConnDiscoverInviteLinkHandler(
    ICurrentUser currentUser,
    IIdentityDirectory identity,
    ConnClientUrls clientUrls,
    IAppLogger<ConnDiscoverInviteLinkHandler> logger)
    : IRequestHandler<ConnDiscoverInviteLinkQuery, ConnDiscoverInviteLinkResponse>
{
    public Task<ConnDiscoverInviteLinkResponse> Handle(
        ConnDiscoverInviteLinkQuery request, CancellationToken cancellationToken = default)
        => ConnDiscoverPersistence.RunAsync(
            logger, "[Connections] Invite link error", "Failed to generate link", cancellationToken,
            () => HandleCore(cancellationToken));

    private async Task<ConnDiscoverInviteLinkResponse> HandleCore(CancellationToken cancellationToken)
    {
        var userId = ConnDiscoverCaller.RequireUserId(currentUser);

        var token = ConnClientUrls.PrefixedInviteToken(userId);
        var physician = await identity.GetUserAsync(userId, cancellationToken);

        return new ConnDiscoverInviteLinkResponse(
            clientUrls.SignupUrl(userId),
            // `physician?.displayName || 'Doctor'` (:1024) — the SHORT fallback.
            ConnJs.Truthy(physician?.DisplayName) ?? "Doctor",
            token);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/connections — NO NODE ROUTE
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The body of <c>POST /api/connections</c>. One key.
/// </summary>
/// <param name="PhysicianUserId">
/// The physician to connect to, by USER id — the value
/// <c>client/src/components/PatientOnboardingWizard.jsx:76</c> posts, taken from
/// <c>doc.userId || doc.id</c> on a <c>GET /search-physicians</c> hit, which sets both to the same
/// user id. Gated on JavaScript truthiness, so <c>""</c>, <c>0</c>, <c>false</c>, <c>null</c> and an
/// absent key all take the 400. A truthy NON-string is the route's 500, matching how every sibling
/// route in the file behaves when a body value reaches Prisma with the wrong type.
/// </param>
public sealed record ConnDiscoverConnectBody(JsonElement PhysicianUserId);

/// <summary>
/// The command behind <c>POST /api/connections</c>.
/// </summary>
/// <param name="CallerUserId">
/// <c>req.user.id</c>. Always the PATIENT end of the new edge and always <c>initiatedBy</c>.
/// </param>
/// <param name="Body">
/// The request body, or null for an absent one. Needs
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c> so an empty POST reaches the
/// handler's own 400 rather than the binder's <c>{"error":"Validation failed"}</c>.
/// </param>
public sealed record ConnDiscoverConnectCommand(
    string CallerUserId,
    ConnDiscoverConnectBody? Body) : IRequest<ConnDiscoverConnectResponse>;

/// <summary>
/// <c>POST /api/connections</c> — <b>an endpoint connections.js does not implement</b>, written
/// deliberately rather than reproduced as the global 404. Sibling of
/// <see cref="ConnDiscoverSearchPhysiciansHandler"/>; the two complete one screen.
///
/// <para><b>The evidence.</b> There are 25 route registrations in connections.js and none of them
/// is a bare <c>router.post('/')</c>, so Express falls through to the global handler. The only
/// caller is <c>client/src/components/PatientOnboardingWizard.jsx:76</c> —
/// <c>await api.post('/connections', { physicianUserId: docId })</c> inside a bare
/// <c>try/catch</c> whose success branch only pushes the id into local component state.
/// <b>The response body is never read.</b> So the onboarding wizard's "Connect" button has never
/// created an edge in production, and it also cannot be broken by one that works.</para>
///
/// <para><b>The decision</b> (docs/connections-surface.md §11.4, and the precedent of
/// <c>GET /api/visits/inbox</c>): implement it, with <c>POST /request</c>'s semantics keyed by
/// <c>physicianUserId</c> instead of <c>targetEmail</c>. It is a divergence and is recorded in
/// PORT-STATUS.md's "Deliberate divergences" table.</para>
///
/// <para><b>⚠ The judgment call: PENDING, not ACCEPTED.</b> The wizard's own UX marks the doctor
/// connected the instant the call returns, which argues for ACCEPTED — but an ACCEPTED edge would
/// let any patient grant themselves a physician's chart access with no consent from the physician,
/// which no route in connections.js does on a patient-initiated path (<c>add-by-phone</c>,
/// <c>create-patient</c> and <c>connect-by-pin</c> all create ACCEPTED edges, and all three are
/// initiated BY the physician or against a code the physician just issued). PENDING also matches
/// <c>POST /request</c>, the route a patient reaches from every other screen. <b>One line changes
/// it</b> — <c>CreatePending</c> to <c>CreateAccepted</c> — if the decision goes the other way.</para>
///
/// <para><b>The gate order</b> mirrors <c>POST /request</c>: the 400 for a missing id, then the 404
/// when the id does not resolve to a PHYSICIAN, then the two 409s, then the insert. The target's
/// <c>userType</c> is checked because this route hard-codes the caller as the patient side: an id
/// naming a patient would otherwise create a patient-to-patient edge, which the pairing test at
/// connections.js:182-186 exists to prevent.</para>
///
/// <para>There is no staff fallback and no <c>X-Clinic-Id</c>: the caller is a patient in their own
/// onboarding wizard, so there is nobody to act on behalf of. There is no <c>subprofileId</c>
/// either — the edge is always the account holder's own.</para>
/// </summary>
public sealed class ConnDiscoverConnectHandler(
    IConnectionsDbContext database,
    IDoctorPatientConnectionStore connections,
    IIdentityDirectory identity,
    IAppLogger<ConnDiscoverConnectHandler> logger)
    : IRequestHandler<ConnDiscoverConnectCommand, ConnDiscoverConnectResponse>
{
    /// <summary>
    /// Its own 500 literal, not <c>POST /request</c>'s. The two routes are separate registrations
    /// and a shared literal would make the logs lie about which one failed.
    /// </summary>
    private const string FailureError = "Failed to create connection";

    /// <summary>The log line, in this file's house format.</summary>
    private const string LogMessage = "[Connections] Create connection error";

    public Task<ConnDiscoverConnectResponse> Handle(
        ConnDiscoverConnectCommand request, CancellationToken cancellationToken = default)
        => ConnDiscoverPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnDiscoverConnectResponse> HandleCore(
        ConnDiscoverConnectCommand request, CancellationToken cancellationToken)
    {
        var physicianValue = request.Body?.PhysicianUserId ?? default;

        if (!ConnJs.IsTruthy(physicianValue))
        {
            throw ConnErrors.BadRequest("Physician is required");
        }

        var physicianUserId = ConnDiscoverCaller.StringOrNull(physicianValue, "physicianUserId")!;

        // The same literal POST /request answers when its target does not resolve, because the
        // client shows one "could not connect" state for both entry points.
        var physician = await identity.GetUserAsync(physicianUserId, cancellationToken);
        if (physician is null || physician.UserType != "PHYSICIAN")
        {
            throw ConnErrors.NotFound("User not found. They must register on Tebrazi first.");
        }

        // POST /request's 409 pair, on the FULL triple with a null subprofileId — the parent edge
        // only, so a patient already linked for a CHILD can still link for themselves. Tracked,
        // because the REJECTED branch below mutates the row it finds.
        var existing = await connections.FindPairAsync(
            physicianUserId, request.CallerUserId, subprofileId: null,
            tracked: true, ct: cancellationToken);

        DoctorPatientConnection connection;

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

            // A REJECTED edge is revived rather than duplicated, matching connections.js:204-209.
            // Unlike that route this one answers 201 on both paths: its only caller reads the
            // status code and never the body, so two shapes for one route would be an invention.
            existing.Reopen(request.CallerUserId);
            connection = existing;
        }
        else
        {
            connection = DoctorPatientConnection.CreatePending(
                physicianUserId, request.CallerUserId, request.CallerUserId);

            connections.Add(connection);
        }

        await database.SaveChangesAsync(cancellationToken);

        // Shaped like POST /request's `include` (connections.js:220-223): two party objects of
        // displayName and email, with NO `id` on either.
        var accounts = await identity.GetUsersAsync(
            [physicianUserId, connection.PatientUserId], cancellationToken);

        var physicianAccount = ConnLinkCommon.RequireAccount(accounts, physicianUserId);
        var patientAccount = ConnLinkCommon.RequireAccount(accounts, connection.PatientUserId);

        return new ConnDiscoverConnectResponse(
            connection.Id,
            connection.PhysicianUserId,
            connection.PatientUserId,
            connection.SubprofileId,
            connection.Status.ToString(),
            connection.InitiatedBy,
            connection.ConnectedAt,
            connection.CreatedAt,
            connection.UpdatedAt,
            new ConnDiscoverConnectParty(physicianAccount.DisplayName, physicianAccount.Email),
            new ConnDiscoverConnectParty(patientAccount.DisplayName, patientAccount.Email));
    }
}
