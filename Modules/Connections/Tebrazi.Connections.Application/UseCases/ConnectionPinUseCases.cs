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
//  PAIRING IN THE ROOM — the physician reads a four-digit code out loud (or holds up a QR
//  poster), the patient types it, and an ACCEPTED edge exists with no handshake. Ported from
//  server/src/routes/connections.js:
//
//      POST /api/connections/generate-pin      (L1515-L1596) -> 200
//      POST /api/connections/connect-by-pin    (L1608-L1725) -> 200
//      GET  /api/connections/qr-code           (L1793-L1816) -> 200
//
//  ⚠ THIS IS NOT THE CLINIC STAFF PIN. `staff_pins` belongs to the Clinics module
//  (Modules/Clinics/.../UseCases/Commands/StaffPinCommands.cs) and unlocks a shared reception
//  terminal. Different table, different lifetime, different meaning. Nothing here imports from it
//  and the two vocabularies must not mix: a "PIN" in this file is always a
//  Tebrazi.Connections.Domain.Entities.ConnectionPin.
//
//  THE MECHANISM, read off the source rather than assumed:
//
//  * STORAGE is its own table, `connection_pins` (schema.prisma:548-563) — id, physicianUserId,
//    clinicId?, pin, expiresAt, usedAt?, usedByUserId?, createdAt. No `updatedAt` (hence
//    ImmutableEntity), and NO unique constraint on `pin`: the column is only indexed, because a
//    spent pin must be allowed to keep its value forever.
//  * ALPHABET AND LENGTH are four DECIMAL digits, drawn as
//    `String(Math.floor(1000 + Math.random() * 9000))` — uniform over 1000-9999, so a leading
//    zero is unreachable and `"0042"` can never match a generated code.
//  * TTL is FIVE MINUTES, written as an absolute `expiresAt` at creation (L1571-1572). The
//    response's `expiresInSeconds` is the hard-coded literal 300, not a recomputation.
//  * GENERATING TWICE INVALIDATES THE FIRST: L1546-1553 force-expires every live pin the
//    physician holds (`expiresAt = now`) before drawing a new one, and "redeemable" is
//    `expiresAt > now` STRICTLY, so an expiry equal to now is already dead. One live code per
//    physician at a time, which is what lets a bare four-digit string resolve to one doctor.
//  * A WRONG OR EXPIRED PIN is a single 404 with one literal — "Invalid or expired PIN. Ask your
//    doctor for a new code." There is no attempt counter, no lockout and no distinction between
//    "never existed", "already used" and "timed out". A pin is also burned when it changes
//    nothing: the already-connected branch marks it used before it looks at the status.
//  * UNIQUENESS among live pins is enforced in application code by a 20-iteration loop, GLOBALLY
//    and not per physician. Twenty consecutive collisions produce an in-band
//    500 {"error":"Could not generate unique PIN, try again"} — the one 500 in this group that
//    does not come from the catch-all.
//
//  WHAT `GET /qr-code` ACTUALLY RETURNS: application/json, not an image and not a redirect. The
//  body is `{ qrCode, connectUrl }` where qrCode is a `data:image/png;base64,…` string. The route
//  name is as misleading as `GET /api/prescriptions/{id}/pdf`, which returns text/html. Node
//  builds it with the `qrcode` npm package; this port uses QRCoder's PngByteQRCode through
//  ConnQrCodeRenderer, which the Foundation pass added along with the package reference. No
//  encoding was invented here.
//
//  CROSS-GROUP AGREEMENT: `POST /connect-by-pin` is the only endpoint in this file that creates a
//  connection, and it must agree with the lifecycle group. It does, on the row: both insert the
//  same nine scalars and this one inserts ACCEPTED + connectedAt directly, the way `PUT /{id}/
//  accept` leaves a row after it runs. The WIRE shapes differ on purpose and that difference is
//  Node's — `POST /request` 201s with the row at the TOP LEVEL plus `physicianUser` /
//  `patientUser` includes, `/accept` 200s with the row at the top level plus a `message`, and
//  this route 200s with the row NESTED under `connection` and no includes at all.
//
//  FOR THE CONTROLLER AUTHOR: all three are literal single-segment routes and must be registered
//  BEFORE any `{id}` route on ConnectionsController. `POST /generate-pin` and
//  `POST /connect-by-pin` both need
//  [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] — the client calls generate-pin with
//  `{ clinicId: undefined }`, which axios serializes to `{}` (ConnectionsPage.jsx:315). The
//  clinic id for generate-pin must be bound with [FromHeader(Name = "X-Clinic-Id")] and passed on
//  the command; do NOT inject IClinicContext for it — see ConnPinGenerateCommand.ClinicIdHeader.
//  `GET /qr-code` takes no input at all beyond the bearer token.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The route-level guard for this file, mapping any unexpected failure onto the route's OWN 500
/// body — "Failed to generate PIN", "Failed to connect", "Failed to generate QR code". Letting an
/// EF, port or encoder exception escape would emit <c>ExceptionHandlingMiddleware</c>'s generic
/// <c>{"error":"Internal Server Error","message":…}</c> instead, and the client branches on
/// <c>err.response.data.error</c> (ConnectionsPage.jsx:318, :356).
///
/// <para>Each Node <c>try</c> covers the WHOLE handler, so this wraps <c>HandleCore</c> end to end
/// rather than one statement. <see cref="AppException"/> passes through untouched, which is what
/// keeps the deliberate 400/403/404 bodies — including <c>generate-pin</c>'s second, in-band 500
/// for twenty consecutive collisions, which is raised as a <see cref="BusinessException"/> and
/// must NOT be relabelled by the catch it flies through.</para>
///
/// <para>Declared internal to this file with the group's prefix, because the four sibling handler
/// files share the namespace and each carries its own log line and failure literal.</para>
/// </summary>
internal static class ConnPinPersistence
{
    /// <summary>Runs the handler body under its route's catch-all.</summary>
    /// <typeparam name="THandler">The calling handler, for the logger category.</typeparam>
    /// <typeparam name="TResponse">The route's response type.</typeparam>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="logMessage">The Node log line, verbatim.</param>
    /// <param name="failureError">The route's own 500 literal, verbatim.</param>
    /// <param name="cancellationToken">Cancellation, checked so a disconnect is not logged.</param>
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

/// <summary>The caller check the one READ endpoint in this group needs.</summary>
internal static class ConnPinCaller
{
    /// <summary>
    /// <c>401 {"error":"No token provided"}</c> — the literal body of <c>authCheck</c>
    /// (server/src/middleware/auth.js:12), which is why this is a
    /// <see cref="BusinessException"/> and not <see cref="UnauthorizedException"/>: that one emits
    /// <c>{"error":"Unauthorized","message":…}</c>, a shape no Connections route produces.
    ///
    /// <para>Unreachable behind <c>[Authorize]</c>, and kept so a handler cannot silently build a
    /// connect URL for an empty user id and answer 200 with a QR code that pairs nobody.</para>
    /// </summary>
    /// <param name="currentUser">The ambient caller.</param>
    /// <returns>The caller's user id.</returns>
    public static string RequireUserId(ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        return string.IsNullOrEmpty(currentUser.UserId)
            ? throw ConnErrors.Node("No token provided", 401)
            : currentUser.UserId;
    }
}

// ── POST /api/connections/generate-pin ───────────────────────────────────────

/// <summary>
/// The one body field <c>POST /api/connections/generate-pin</c> reads:
/// <c>req.body.clinicId</c> (connections.js:1518).
///
/// <para>Non-nullable <see cref="JsonElement"/> so the handler can tell an ABSENT key from an
/// explicit <c>null</c> and from a truthy NON-STRING, which are three different outcomes in Node:
/// the first two fall through to the <c>X-Clinic-Id</c> header, and the third reaches Prisma as a
/// non-string and produces the route's 500. A <c>string?</c> binding would collapse all three.</para>
///
/// <para>The whole record is nullable on the command because the client sends
/// <c>{ clinicId: undefined }</c>, which axios serializes as <c>{}</c>
/// (ConnectionsPage.jsx:315) — and because a body-less POST must reach this handler rather than
/// the model binder's <c>{"error":"Validation failed",…}</c>, which is a shape no Connections
/// route produces.</para>
/// </summary>
/// <param name="ClinicId">
/// The clinic to act for. Only consulted on the staff path, and stored on the pin row either way
/// (<c>clinicId: clinicId || null</c>, connections.js:1575).
/// </param>
public sealed record ConnPinGenerateBody(JsonElement ClinicId);

/// <param name="CallerUserId">
/// <c>req.user.id</c>. This is a WRITE, so per the module's house rule the id travels on the
/// command rather than through <see cref="ICurrentUser"/>. It is BOTH the identity whose physician
/// profile is probed AND the identity whose display name is echoed as
/// <c>physicianName</c> — which is the quirk at connections.js:1584.
/// </param>
/// <param name="ClinicIdHeader">
/// The raw <c>X-Clinic-Id</c> request header, or null.
///
/// <para><b>Bind this with <c>[FromHeader(Name = "X-Clinic-Id")]</c>; do NOT inject
/// <c>IClinicContext</c>.</b> That kernel service resolves the header and then falls back to the
/// <c>?clinicId=</c> QUERY STRING, and this route's precedence is
/// <c>req.body.clinicId || req.headers['x-clinic-id'] || null</c> (connections.js:1518) with no
/// query arm at all. Using IClinicContext would resolve a clinic from a query parameter Node
/// ignores, turning a 403 into a successful staff pin. See docs/connections-surface.md §10.2 for
/// the seven different precedences in this file.</para>
/// </param>
/// <param name="Body">The parsed body, or null when none was sent.</param>
public sealed record ConnPinGenerateCommand(
    string CallerUserId,
    string? ClinicIdHeader,
    ConnPinGenerateBody? Body) : IRequest<ConnPinGenerateResponse>;

/// <summary>
/// Port of <c>POST /api/connections/generate-pin</c> (connections.js:1515-1596) → 200.
///
/// <para><b>The entry gate is the ABSENCE OF A PHYSICIAN PROFILE ROW, not the userType claim</b>
/// (connections.js:1522-1523). This is the only one of the seven staff fallbacks in the file that
/// works that way — the other six branch on <c>req.user.userType !== 'PHYSICIAN'</c>. So a caller
/// whose token says PHYSICIAN but who has no profile row takes the STAFF path here and the
/// physician path everywhere else, and a caller with a profile never reaches the staff path even
/// when they send an <c>X-Clinic-Id</c> for a clinic they do not work at.</para>
///
/// <para>The three staff failures map to three different bodies, and the mapping is this route's
/// alone: no clinic id at all is <c>403 "Only physicians or clinic staff can generate PINs"</c>,
/// a non-staff caller is <c>403 "Not authorized for this clinic"</c>, and a clinic with no
/// resolvable physician is <c>404 "Clinic physician not found"</c>. Node reaches that last one
/// through <c>if (!clinic?.physician)</c>, which is a single test over both "no clinic" and "no
/// physician" — hence two outcomes folded onto one literal below.</para>
///
/// <para><b>Two commits, in Node's order.</b> The force-expire sweep must be durable before the
/// uniqueness probe runs, because that probe is a database query: leaving the expiries in the
/// change tracker would let the physician's own about-to-die codes count as live collisions.
/// Node gets this for free — its <c>updateMany</c> is a statement, not a staged change.</para>
/// </summary>
/// <param name="database">The unit of work.</param>
/// <param name="pins">The <c>connection_pins</c> store.</param>
/// <param name="identity">Users and physician profiles.</param>
/// <param name="staff">The shared three-step staff fallback.</param>
/// <param name="logger">This handler's logger.</param>
public sealed class ConnPinGenerateHandler(
    IConnectionsDbContext database,
    IConnectionPinStore pins,
    IIdentityDirectory identity,
    ConnStaffResolver staff,
    IAppLogger<ConnPinGenerateHandler> logger)
    : IRequestHandler<ConnPinGenerateCommand, ConnPinGenerateResponse>
{
    /// <summary>The route's own catch-all 500 literal (connections.js:1595), verbatim.</summary>
    private const string FailureError = "Failed to generate PIN";

    /// <summary>The route's Node log line (connections.js:1594), verbatim.</summary>
    private const string LogMessage = "[Connections] Generate PIN error";

    /// <summary>
    /// The collision budget (connections.js:1565). <c>attempts</c> is incremented ONLY on a
    /// collision and tested after the loop, so this is twenty consecutive collisions and not
    /// twenty draws.
    /// </summary>
    private const int MaxCollisions = 20;

    /// <inheritdoc />
    public Task<ConnPinGenerateResponse> Handle(
        ConnPinGenerateCommand request, CancellationToken cancellationToken = default)
        => ConnPinPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnPinGenerateResponse> HandleCore(
        ConnPinGenerateCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var callerUserId = request.CallerUserId;
        var clinicId = ResolveClinicId(request);

        // L1521-L1523. The gate: a physician PROFILE row, not the userType claim.
        var physicianUserId = callerUserId;
        var physicianProfile = await identity.GetPhysicianByUserIdAsync(callerUserId, cancellationToken);

        if (physicianProfile is null)
        {
            var resolution = await staff.ResolveAsync(callerUserId, clinicId, cancellationToken);

            physicianUserId = resolution.Outcome switch
            {
                // L1525-L1527. No clinic id: the caller is neither a physician nor plausibly staff.
                ConnStaffOutcome.NoClinicId =>
                    throw ConnErrors.Forbidden("Only physicians or clinic staff can generate PINs"),

                // L1531-L1533. A clinic id, but no ACTIVE clinic_staff row joining them to it.
                ConnStaffOutcome.NotStaff =>
                    throw ConnErrors.Forbidden("Not authorized for this clinic"),

                // L1539-L1541. `if (!clinic?.physician)` is ONE test over both misses, so an
                // unknown clinic id and a clinic whose physician profile cannot be resolved
                // produce the same 404 literal.
                ConnStaffOutcome.ClinicNotFound or ConnStaffOutcome.NoClinicPhysician =>
                    throw ConnErrors.NotFound("Clinic physician not found"),

                _ => resolution.PhysicianUserId
                     ?? throw ConnErrors.NotFound("Clinic physician not found")
            };
        }

        // L1546-L1553. One live code per physician: force-expire the rest. `expiresAt = now` is
        // enough because redeemability is `expiresAt > now`, strictly.
        var now = DateTime.UtcNow;
        var live = await pins.ListRedeemableForUpdateAsync(physicianUserId, now, cancellationToken);

        if (live.Count > 0)
        {
            foreach (var stale in live) stale.ForceExpire(now);

            // Committed HERE, before the loop below, because that loop asks the DATABASE whether a
            // candidate code is live. See the class summary.
            await database.SaveChangesAsync(cancellationToken);
        }

        var pin = await DrawUniquePinAsync(cancellationToken);

        // L1571-L1572. `new Date()` re-read after the loop, then +5 minutes. Node's setMinutes
        // arithmetic is local-time; UTC addition is the equivalent everywhere except across a DST
        // transition, where Node would move the expiry by the offset change. Not modelled.
        var expiresAt = DateTime.UtcNow.Add(ConnectionPin.Lifetime);

        // L1574-L1581. `clinicId || null` — an unresolved clinic is simply not recorded, including
        // on the physician path where the header may name a clinic the caller does not work at.
        var created = ConnectionPin.Create(physicianUserId, pin, expiresAt, clinicId);
        pins.Add(created);
        await database.SaveChangesAsync(cancellationToken);

        // L1584-L1587. ⚠ THE CALLER, not `physicianUserId`. On the staff path this is the
        // receptionist's name beside the doctor's code. Reproduced deliberately.
        var caller = await identity.GetUserAsync(callerUserId, cancellationToken);

        // L1589-L1594. `pin` and `expiresAt` are read back off the created row; `expiresInSeconds`
        // is the literal 300 and is NOT recomputed from `expiresAt`.
        return new ConnPinGenerateResponse(
            created.Pin,
            created.ExpiresAt,
            caller?.DisplayName,
            ConnectionPin.LifetimeSeconds);
    }

    /// <summary>
    /// <c>req.body.clinicId || req.headers['x-clinic-id'] || null</c> (connections.js:1518) —
    /// body first on this route, header first on <c>GET /</c> only.
    ///
    /// <para>A TRUTHY NON-STRING body value is the route's 500 and not "absent". Node hands the
    /// raw value to Prisma — as <c>clinicStaff.findFirst({ where: { clinicId } })</c> on the staff
    /// path, or as <c>connectionPin.create({ data: { clinicId } })</c> on the physician path — and
    /// both reject a number, an array or an object with a validation error that lands in the
    /// route's own catch. Falling back to the header there would answer 200 where Node answers
    /// 500, so it is raised explicitly.</para>
    /// </summary>
    /// <param name="request">The command.</param>
    /// <returns>The clinic id, or null when neither source supplied a truthy one.</returns>
    private static string? ResolveClinicId(ConnPinGenerateCommand request)
    {
        var rawClinicId = request.Body?.ClinicId ?? default;

        if (ConnJs.IsTruthy(rawClinicId))
        {
            return ConnJs.AsString(rawClinicId) ?? throw ConnErrors.ServerError(FailureError);
        }

        return ConnJs.Truthy(request.ClinicIdHeader);
    }

    /// <summary>
    /// The generation loop (connections.js:1556-1569), transcribed rather than improved.
    ///
    /// <para>The probe is GLOBAL — "is any live pin in the whole table carrying this code" — not
    /// per physician, because <c>connect-by-pin</c> resolves a bare four-digit string to exactly
    /// one doctor. With at most 9000 codes and a five-minute window, twenty consecutive collisions
    /// means the clinic is issuing codes faster than the space allows, and the answer is the
    /// in-band <c>500 {"error":"Could not generate unique PIN, try again"}</c> — thrown here so it
    /// keeps its own literal rather than the catch-all's.</para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Four decimal digits that no live pin currently carries.</returns>
    private async Task<string> DrawUniquePinAsync(CancellationToken cancellationToken)
    {
        var collisions = 0;

        while (true)
        {
            var candidate = ConnectionPin.GeneratePin();

            if (!await pins.AnyRedeemableAsync(candidate, DateTime.UtcNow, cancellationToken))
            {
                return candidate;
            }

            collisions++;

            if (collisions >= MaxCollisions)
            {
                throw ConnErrors.ServerError("Could not generate unique PIN, try again");
            }
        }
    }
}

// ── POST /api/connections/connect-by-pin ─────────────────────────────────────

/// <summary>
/// The one body field <c>POST /api/connections/connect-by-pin</c> reads: <c>{ pin }</c>
/// (connections.js:1611).
///
/// <para>Non-nullable <see cref="JsonElement"/> because the gate is
/// <c>if (!pin || pin.length !== 4)</c> — a JavaScript expression over an unvalidated value, and
/// <c>.length</c> is <c>undefined</c> for a number, an object or a boolean. A <c>string</c>
/// binding would answer the model binder's <c>{"error":"Validation failed",…}</c> for inputs that
/// must reach this route's own 400.</para>
/// </summary>
/// <param name="Pin">The four characters the patient typed.</param>
public sealed record ConnPinConnectBody(JsonElement Pin);

/// <param name="CallerUserId">
/// <c>req.user.id</c> — the PATIENT, on every path. A WRITE, so the id is on the command.
/// </param>
/// <param name="Body">The parsed body, or null when none was sent.</param>
public sealed record ConnPinConnectCommand(
    string CallerUserId,
    ConnPinConnectBody? Body) : IRequest<object>;

/// <summary>
/// Port of <c>POST /api/connections/connect-by-pin</c> (connections.js:1608-1725) → 200 on all
/// three success branches.
///
/// <para><b>The response type is <c>object</c> because the three branches emit two incompatible
/// key sets</b> — <c>{ message, connection, physician }</c> when something changed and
/// <c>{ message, alreadyConnected, connection }</c> when nothing did. That is the Visits
/// precedent (<c>VisitReadDetailQuery : IRequest&lt;object&gt;</c>); a single nullable-everything
/// DTO would put <c>"alreadyConnected": null</c> and <c>"physician": null</c> on the wire, and
/// Node emits neither key on the branch that does not own it.</para>
///
/// <para><b>The PIN is the whole credential.</b> The lookup matches on the code plus the
/// redeemable window and nothing else — no physician, no clinic, no patient. It is deliberately
/// so: the code was read out loud in a room, and the five-minute window plus one-live-code-per-
/// physician is the entire security model. Do not add a clinic predicate.</para>
///
/// <para><b>The error ORDER is load-bearing.</b> The length gate runs before the lookup, the
/// lookup before the self-connect test, and the self-connect test before the existing-edge probe —
/// so a physician who types their own live code gets <c>400 "Cannot connect to yourself"</c> with
/// the pin STILL LIVE, because the burn happens after that point. Reordering would spend it.</para>
///
/// <para><b>The probe is <c>FindPairAsync(..., subprofileId: null)</c>, not
/// <c>FindAnyPairAsync</c>.</b> connections.js:1639 passes the key explicitly, so a physician
/// already connected to one of this patient's CHILDREN still gets a parent edge created here —
/// where <c>POST /add-by-phone</c>, which omits the key, would report "already connected" and
/// create nothing. The two routes disagree in Node and they disagree here.</para>
/// </summary>
/// <param name="database">The unit of work.</param>
/// <param name="pins">The <c>connection_pins</c> store.</param>
/// <param name="connections">The <c>doctor_patient_connections</c> store.</param>
/// <param name="identity">Users and physician profiles.</param>
/// <param name="notifications">The notification port, called after the commit.</param>
/// <param name="logger">This handler's logger.</param>
public sealed class ConnPinConnectHandler(
    IConnectionsDbContext database,
    IConnectionPinStore pins,
    IDoctorPatientConnectionStore connections,
    IIdentityDirectory identity,
    INotificationPublisher notifications,
    IAppLogger<ConnPinConnectHandler> logger)
    : IRequestHandler<ConnPinConnectCommand, object>
{
    /// <summary>The route's own catch-all 500 literal (connections.js:1723), verbatim.</summary>
    private const string FailureError = "Failed to connect";

    /// <summary>The route's Node log line (connections.js:1722), verbatim.</summary>
    private const string LogMessage = "[Connections] Connect by PIN error";

    /// <summary>
    /// The nested best-effort log line (connections.js:1715), verbatim. Its block must NOT reach
    /// the file guard — see the call site.
    /// </summary>
    private const string NotificationLogMessage = "[Connections] PIN notification error (non-blocking)";

    /// <summary>The success literal, shared by the create and re-accept branches.</summary>
    private const string ConnectedMessage = "Connected successfully!";

    /// <inheritdoc />
    public Task<object> Handle(
        ConnPinConnectCommand request, CancellationToken cancellationToken = default)
        => ConnPinPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<object> HandleCore(
        ConnPinConnectCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var patientUserId = request.CallerUserId;

        // L1613-L1615. `!pin || pin.length !== 4`. ConnJs.AsString is the `.length` half: a
        // number, a boolean or an object has no `.length`, so it fails the test exactly as a
        // wrong-length string does. FOUR CHARACTERS, not four digits — "abcd" passes this gate and
        // dies on the lookup below with the 404, which is the literal the client shows.
        var pin = ConnJs.AsString(request.Body?.Pin ?? default);

        if (pin is null || pin.Length != 4)
        {
            throw ConnErrors.BadRequest("Please enter a 4-digit PIN");
        }

        // L1618-L1624. Code + unused + unexpired. One literal covers wrong, spent and stale.
        var connectionPin = await pins.FindRedeemableForUpdateAsync(
                                pin, DateTime.UtcNow, cancellationToken)
                            ?? throw ConnErrors.NotFound(
                                "Invalid or expired PIN. Ask your doctor for a new code.");

        var physicianUserId = connectionPin.PhysicianUserId;

        // L1631-L1633. Before the burn, so a self-typed code survives.
        if (string.Equals(physicianUserId, patientUserId, StringComparison.Ordinal))
        {
            throw ConnErrors.BadRequest("Cannot connect to yourself");
        }

        // L1638-L1640. subprofileId compared as a VALUE. Tracked: this row may be re-accepted.
        var existing = await connections.FindPairAsync(
            physicianUserId, patientUserId, subprofileId: null, status: null,
            tracked: true, ct: cancellationToken);

        return existing is not null
            ? await RedeemAgainstExistingAsync(connectionPin, existing, patientUserId, cancellationToken)
            : await CreateConnectionAsync(connectionPin, physicianUserId, patientUserId, cancellationToken);
    }

    /// <summary>
    /// The two branches for a pair that already has a parent edge (connections.js:1642-1671).
    ///
    /// <para><b>The pin is burned first and unconditionally</b> (L1644-1647), before the status is
    /// even looked at — so a patient who re-enters a code against a doctor they are already
    /// connected to spends it for nothing.</para>
    ///
    /// <para><b>A PENDING or REJECTED edge is promoted straight to ACCEPTED</b> (L1650-1654).
    /// There is no state machine and no consent step: the physician's own code is the
    /// authorization, so a patient can revive an edge they themselves rejected, and a physician
    /// whose request is still pending can hand over a code instead of waiting for the accept.</para>
    /// </summary>
    /// <param name="connectionPin">The redeemed pin, tracked.</param>
    /// <param name="existing">The pre-existing edge, tracked.</param>
    /// <param name="patientUserId">The redeeming patient.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A <see cref="ConnPinConnectedResponse"/> or a <see cref="ConnPinAlreadyConnectedResponse"/>.</returns>
    private async Task<object> RedeemAgainstExistingAsync(
        ConnectionPin connectionPin,
        DoctorPatientConnection existing,
        string patientUserId,
        CancellationToken cancellationToken)
    {
        connectionPin.MarkUsed(DateTime.UtcNow, patientUserId);

        // L1667-L1670. Nothing on the edge changes, so the snapshot is simply the row as read.
        if (existing.Status == ConnectionStatus.ACCEPTED)
        {
            var untouched = ConnPinConnectionRow.From(existing);
            await database.SaveChangesAsync(cancellationToken);

            return new ConnPinAlreadyConnectedResponse("Already connected", true, untouched);
        }

        // ⚠ THE SNAPSHOT IS TAKEN BEFORE THE MUTATION AND THAT IS THE CONTRACT. Node answers
        // `{ ...existing, status: 'ACCEPTED' }` (L1660) where `existing` is the object the
        // findFirst returned — so the response carries the OLD connectedAt and the OLD updatedAt
        // with the NEW status, and disagrees with the row that was just written. Building the DTO
        // after Accept() would "fix" that and change the bytes.
        var snapshot = ConnPinConnectionRow.From(existing);
        existing.Accept();
        await database.SaveChangesAsync(cancellationToken);

        var physician = await PhysicianCardAsync(existing.PhysicianUserId, cancellationToken);

        return new ConnPinConnectedResponse(
            ConnectedMessage,
            snapshot with { Status = ConnectionStatus.ACCEPTED },
            physician);
    }

    /// <summary>
    /// The create branch (connections.js:1674-1724): an instantly ACCEPTED edge, because the pin
    /// IS the verification.
    ///
    /// <para>Node wraps the insert and the pin update in <c>prisma.$transaction([create,
    /// update])</c>. Both tables live in this context, so one <c>SaveChangesAsync</c> over the two
    /// staged changes is the faithful port and needs no explicit transaction — see
    /// docs/connections-surface.md §5.</para>
    ///
    /// <para><c>initiatedBy</c> is the PATIENT (L1681): they typed the code. That differs from
    /// <c>POST /create-patient</c>, which records the physician even when staff called it.</para>
    /// </summary>
    /// <param name="connectionPin">The redeemed pin, tracked.</param>
    /// <param name="physicianUserId">The pin's owner.</param>
    /// <param name="patientUserId">The redeeming patient.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The <see cref="ConnPinConnectedResponse"/>.</returns>
    private async Task<object> CreateConnectionAsync(
        ConnectionPin connectionPin,
        string physicianUserId,
        string patientUserId,
        CancellationToken cancellationToken)
    {
        var connection = DoctorPatientConnection.CreateAccepted(
            physicianUserId, patientUserId, initiatedBy: patientUserId);

        connections.Add(connection);
        connectionPin.MarkUsed(DateTime.UtcNow, patientUserId);
        await database.SaveChangesAsync(cancellationToken);

        // L1690-L1697. OUTSIDE the notification try/catch in Node, so a failure here really is the
        // route's 500 even though the rows are already committed. The file guard reproduces that.
        var physician = await PhysicianCardAsync(physicianUserId, cancellationToken);

        await NotifyPhysicianAsync(connection, physicianUserId, patientUserId, cancellationToken);

        return new ConnPinConnectedResponse(
            ConnectedMessage,
            ConnPinConnectionRow.From(connection),
            physician);
    }

    /// <summary>
    /// The physician's display card, reproducing Node's optional chain
    /// <c>physician?.displayName</c> / <c>physician?.physicianProfile?.specialty</c>
    /// (connections.js:1700-1703, :1719-1722).
    ///
    /// <para>The profile lookup is skipped when the user row does not resolve, because in Node
    /// both values hang off the same <c>findUnique</c> — a missing user suppresses the specialty
    /// too, even if a physician profile row exists for that id.</para>
    /// </summary>
    /// <param name="physicianUserId">The physician's USER id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The card. Either member may be null, which drops its key.</returns>
    private async Task<ConnPinPhysicianCard> PhysicianCardAsync(
        string physicianUserId, CancellationToken cancellationToken)
    {
        var user = await identity.GetUserAsync(physicianUserId, cancellationToken);

        var profile = user is null
            ? null
            : await identity.GetPhysicianByUserIdAsync(physicianUserId, cancellationToken);

        return new ConnPinPhysicianCard(user?.DisplayName, profile?.Specialty);
    }

    /// <summary>
    /// The best-effort notification block (connections.js:1700-1716).
    ///
    /// <para><b>The patient lookup is INSIDE the try and must stay there.</b> Node wraps the read
    /// and the write together in <c>try { … } catch (notifErr) { logger.error(…) }</c>, so a
    /// failure of either leaves a committed connection and a 200. Folding this into the file guard
    /// would turn a successful pairing into a 500 — the failure mode that has already bitten this
    /// port twice (docs/connections-surface.md §10.9).</para>
    ///
    /// <para>The type is the bare string <c>CONNECTION</c>, not <c>CONNECTION_ACCEPTED</c>, and it
    /// is not in the Node notification service's own type list — this route bypasses
    /// <c>createNotification</c> and writes <c>prisma.notification.create</c> directly, which is
    /// why it sends no email and no push. <c>SendEmail: false</c> is that fidelity.</para>
    /// </summary>
    /// <param name="connection">The edge just created, for its id.</param>
    /// <param name="physicianUserId">The recipient.</param>
    /// <param name="patientUserId">The patient, named in the message and in the payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task NotifyPhysicianAsync(
        DoctorPatientConnection connection,
        string physicianUserId,
        string patientUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var patient = await identity.GetUserAsync(patientUserId, cancellationToken);

            // `${patient?.displayName || 'A patient'}` — the `||` means an EMPTY display name also
            // falls back, which ConnJs.Truthy is exactly.
            var patientName = ConnJs.Truthy(patient?.DisplayName) ?? "A patient";

            var payload = JsonSerializer.Serialize(new
            {
                connectionId = connection.Id,
                patientUserId
            });

            await notifications.PublishAsync(
                new NotificationRequest(
                    physicianUserId,
                    "CONNECTION",
                    "New Patient Connected",
                    $"{patientName} connected via clinic PIN",
                    payload,
                    SendEmail: false),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Error(NotificationLogMessage, exception);
        }
    }
}

// ── GET /api/connections/qr-code ─────────────────────────────────────────────

/// <summary>
/// <c>GET /api/connections/qr-code</c> — no route parameters, no query string and no body. The
/// physician is the caller, so a READ handler with <see cref="ICurrentUser"/> is the whole input.
/// </summary>
public sealed record ConnPinQrCodeQuery : IRequest<ConnPinQrCodeResponse>;

/// <summary>
/// Port of <c>GET /api/connections/qr-code</c> (connections.js:1793-1816) → 200.
///
/// <para><b>It returns JSON, not an image.</b> The body is
/// <c>{ qrCode, connectUrl }</c> where <c>qrCode</c> is a <c>data:image/png;base64,…</c> string —
/// the route name is as misleading as <c>GET /api/prescriptions/{id}/pdf</c>, which answers
/// <c>text/html</c>. The controller must return it with <c>Payload(...)</c> like any other JSON
/// endpoint; do NOT reach for <c>File(...)</c> or set a content type.</para>
///
/// <para><b>It touches no database and no other module.</b> The encoded payload is a pure function
/// of the caller's user id — <c>`${CLIENT_URL || 'http://localhost:5173'}/connect/${userId}`</c> —
/// so there is no physician-profile check and a patient calling it gets a working QR code that
/// points at their own connect page. That is Node's behaviour, not an oversight to gate.</para>
///
/// <para>⚠ The fallback origin here is port <b>5173</b> while the two invite routes fall back to
/// <b>5174</b> (connections.js:1798 against :982 and :1019). Both are in
/// <c>ConnClientUrls</c>; the difference disappears the moment <c>Client:Url</c> is configured.</para>
///
/// <para>The encoder is QRCoder via <c>ConnQrCodeRenderer</c>, at error-correction level M — the
/// <c>qrcode</c> npm package's own default, which the Node call does not override. The base64
/// differs from Node's byte for byte and cannot not: what matters, and what is identical, is the
/// decoded string.</para>
/// </summary>
/// <param name="currentUser">The ambient caller — this is a READ.</param>
/// <param name="clientUrls">The client origin and the <c>/connect/{id}</c> shape.</param>
/// <param name="qrCodes">The PNG data-URL encoder.</param>
/// <param name="logger">This handler's logger.</param>
public sealed class ConnPinQrCodeHandler(
    ICurrentUser currentUser,
    ConnClientUrls clientUrls,
    ConnQrCodeRenderer qrCodes,
    IAppLogger<ConnPinQrCodeHandler> logger)
    : IRequestHandler<ConnPinQrCodeQuery, ConnPinQrCodeResponse>
{
    /// <summary>The route's own 500 literal (connections.js:1815), verbatim.</summary>
    private const string FailureError = "Failed to generate QR code";

    /// <summary>The route's Node log line (connections.js:1814), verbatim.</summary>
    private const string LogMessage = "[Connections] QR code generation error";

    /// <inheritdoc />
    public Task<ConnPinQrCodeResponse> Handle(
        ConnPinQrCodeQuery request, CancellationToken cancellationToken = default)
        => ConnPinPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore());

    private Task<ConnPinQrCodeResponse> HandleCore()
    {
        var userId = ConnPinCaller.RequireUserId(currentUser);

        // L1798-L1799.
        var connectUrl = clientUrls.ConnectUrl(userId);

        // L1801-L1805. A render failure — which QRCoder raises only for a payload too large for
        // any QR version — is the route's named 500 through the file guard, exactly as the npm
        // package's rejection is in Node.
        var qrCode = qrCodes.RenderPngDataUrl(connectUrl);

        return Task.FromResult(new ConnPinQrCodeResponse(qrCode, connectUrl));
    }
}
