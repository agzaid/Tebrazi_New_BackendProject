using System.Text.Json;
using Tebrazi.Connections.Application.Abstractions.Persistence;
using Tebrazi.Connections.Application.ApiModels.Responses;
using Tebrazi.Connections.Application.Services;
using Tebrazi.Connections.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Connections.Application.UseCases;

// ═════════════════════════════════════════════════════════════════════════════
//  DEPENDANT EDGES — the patient sharing one of their already-connected doctors with a family
//  member, and taking that sharing back. Ported from server/src/routes/connections.js:
//
//      POST   /api/connections/assign-subprofile                               (L310-L369) -> 201
//      DELETE /api/connections/unassign-subprofile/{subprofileId}/{physicianUserId}
//                                                                              (L375-L391) -> 200
//
//  Facts that hold across both, stated once here rather than twice:
//
//  * WHO IS ALLOWED: the PATIENT, and only ever the patient. Both routes bind `req.user.id` to
//    `patientUserId` and nothing else (L312/L381, L377/L381), so a caller can only ever act on
//    their OWN family. Note what that means in the negative: THERE IS NO userType GATE on either
//    route. A caller whose userType is PHYSICIAN, RECEPTIONIST or STAFF is not rejected — they are
//    simply treated as the patient, and the assign then 400s at "Patient profile not found"
//    because a physician account has no patientProfile row. Do not add a `!== 'PATIENT'` check:
//    it would turn that 400 into a 403 and change the wire. Equally, do NOT add a check that the
//    assignee is really a physician; nothing here looks at physicianProfile except the response
//    projection, and an ACCEPTED parent edge is the whole authorization story.
//
//  * A NEW ROW, NOT AN UPDATE. This is the single most mistakeable thing in the group.
//    `subprofileId` is the third column of the composite unique key
//    `@@unique([physicianUserId, patientUserId, subprofileId])` (schema.prisma:526), so it is part
//    of the row's IDENTITY. `assign-subprofile` therefore CREATES a second, independent row
//    (L344) and leaves the ACCEPTED parent edge — the one with `subprofileId = NULL` — completely
//    untouched. It does not move the parent, it does not copy `connectedAt` from it, and it does
//    not upsert. A patient sharing one doctor with three children ends up with FOUR rows, and the
//    `GET /` patient branch renders four cards. Conversely `unassign` HARD-DELETES only the
//    dependant row (L385) and can never touch the parent, because its `subprofileId` predicate is
//    a route parameter and therefore never NULL.
//
//  * WHAT HAPPENS WHEN THE TARGET ROW ALREADY EXISTS: `409 {"error":"This doctor is already
//    assigned to this family member"}` (L340-L342), and the probe that produces it is
//    STATUS-BLIND (L337-L339 passes no `status`). So an existing PENDING or REJECTED dependant
//    edge also 409s, and there is no path in this router that revives it — not this route, and
//    not `/accept`, which addresses rows by id. That is the contract; do not "fix" it into an
//    upsert. The 409 is also not the last line of defence: two concurrent assigns both pass the
//    probe, the second insert violates the unique index, and both backends answer their route's
//    named 500 (Prisma P2002 into the route catch, `DbUpdateException` into the guard below).
//
//  * BOTH DELETES IN THIS ROUTER ARE HARD DELETES and there is no soft-delete column on this
//    model at all — `deletedAt` does not occur anywhere in connections.js. Removing the row is
//    what revokes the physician's access to that dependant's chart.
//
//  * NEITHER ROUTE READS `X-Clinic-Id`, neither has a staff fallback, neither notifies anybody and
//    neither writes an audit row. The physician is not told that they gained or lost a dependant.
//
//  * FAMILY SUBPROFILES BELONG TO THE PATIENTS MODULE. They are reached through
//    IPatientDirectory — never a DbContext — which is why the ownership test is expressed as two
//    reads plus a comparison instead of Node's single two-predicate `findFirst`.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The catch-all both routes are wrapped in (connections.js:365-368, :387-390), each with its own
/// log line and its own 500 literal.
///
/// <para>The Node try covers the WHOLE handler, so a failure in the patient-profile read, the
/// subprofile read, either connection probe or the insert all answer the same named literal.
/// Letting an EF or port exception escape instead would emit <c>ExceptionHandlingMiddleware</c>'s
/// generic <c>{"error":"Internal Server Error"}</c>, and the client branches on
/// <c>err.response.data.error</c> to build its toast.</para>
///
/// <para><see cref="AppException"/> passes through untouched, so the deliberate 400/404/409 keep
/// their own bodies and status codes. A cancelled request is not logged and not converted — a
/// client disconnect is not a server error.</para>
///
/// <para>Declared internal to THIS file, with a name narrower than the group's
/// <c>ConnSubprofile</c> prefix, because the five Connections handler files compile into one
/// namespace and the sibling file carrying <c>GET /patient-summaries</c> shares that prefix.
/// <c>internal</c> is no defence — they are in the same assembly.</para>
/// </summary>
internal static class ConnSubprofileAssignmentPersistence
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

/// <summary>
/// The JSON body of <c>POST /api/connections/assign-subprofile</c>:
/// <c>{ physicianUserId, subprofileId }</c> (connections.js:313).
///
/// <para>Both members are non-nullable <see cref="JsonElement"/> so the handler can tell an ABSENT
/// key (<see cref="JsonValueKind.Undefined"/>) from an explicit <c>null</c> and from a wrongly
/// typed value — all three are falsy to Node's <c>if (!physicianUserId || !subprofileId)</c>, but
/// a truthy NON-STRING is not, and it reaches Prisma and produces a 500 rather than a 400. A
/// <c>string?</c> binding would collapse the two outcomes.</para>
///
/// <para>The whole record is nullable on the command so an empty or absent body reaches the
/// handler's own 400 rather than the model binder's
/// <c>{"error":"Validation failed","message":…}</c>, which is a body no Connections route ever
/// produces. The controller must bind it with
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>.</para>
/// </summary>
public sealed record ConnSubprofileAssignBody(
    JsonElement PhysicianUserId,
    JsonElement SubprofileId);

/// <summary>
/// <c>POST /api/connections/assign-subprofile</c> — a WRITE, so the caller's id arrives explicitly
/// per the module's house rule and the handler injects no <c>ICurrentUser</c>.
/// </summary>
public sealed record ConnSubprofileAssignCommand(
    string CallerUserId,
    ConnSubprofileAssignBody? Body) : IRequest<ConnSubprofileAssignResponse>;

/// <summary>
/// Port of <c>POST /api/connections/assign-subprofile</c> (connections.js:310-369) →
/// <b>201</b>.
///
/// <para><b>The five gates, in Node's order — and the order is on the wire.</b> Each one is
/// reached only if every gate above it passed, so a request that fails two of them learns about
/// the first:</para>
/// <list type="number">
///   <item><description>either field falsy → <b>400</b>
///     <c>{"error":"physicianUserId and subprofileId are required"}</c> (L315-L317);</description></item>
///   <item><description>the caller has no <c>patientProfile</c> → <b>400</b>
///     <c>{"error":"Patient profile not found"}</c> (L320-L321) — <b>400, not 404</b>, which is
///     the one status in this handler that reads like a mistake and is not;</description></item>
///   <item><description>the subprofile does not exist, <b>or belongs to someone else</b> →
///     <b>404</b> <c>{"error":"Family member not found"}</c> (L323-L326). Node expresses this as
///     one <c>findFirst({ id, patientProfileId })</c>, so "no such dependant" and "not your
///     dependant" are deliberately indistinguishable — an enumeration defence that must
///     survive;</description></item>
///   <item><description>no ACCEPTED PARENT edge with that doctor → <b>400</b>
///     <c>{"error":"You must be connected to this doctor first"}</c> (L329-L334). The probe pins
///     <c>subprofileId: null</c> AND <c>status: 'ACCEPTED'</c>, so a PENDING request to that
///     doctor, or a link that exists only for a different child, does not
///     qualify;</description></item>
///   <item><description>a row already exists for this exact triple → <b>409</b>
///     <c>{"error":"This doctor is already assigned to this family member"}</c> (L337-L342),
///     regardless of that row's status.</description></item>
/// </list>
///
/// <para><b>There is no <c>isActive</c> filter on the dependant</b> (L323-L325). A deactivated
/// family member can still be shared with a doctor. Adding the filter would 404 a case Node
/// serves.</para>
///
/// <para><b>The created row is ACCEPTED immediately</b>, with <c>connectedAt = now</c> and
/// <c>initiatedBy</c> = the patient (L344-L352). No handshake, no PENDING state, and no
/// notification to the physician — the patient's own accepted parent edge is the consent.</para>
/// </summary>
public sealed class ConnSubprofileAssignHandler(
    IConnectionsDbContext database,
    IDoctorPatientConnectionStore connections,
    IPatientDirectory patients,
    IIdentityDirectory identity,
    IAppLogger<ConnSubprofileAssignHandler> logger)
    : IRequestHandler<ConnSubprofileAssignCommand, ConnSubprofileAssignResponse>
{
    /// <summary>The route's own 500 literal (connections.js:367), verbatim.</summary>
    private const string FailureError = "Failed to assign doctor to family member";

    /// <summary>The route's Node log line (connections.js:366), verbatim.</summary>
    private const string LogMessage = "[Connections] Assign subprofile error";

    public Task<ConnSubprofileAssignResponse> Handle(
        ConnSubprofileAssignCommand request, CancellationToken cancellationToken = default)
        => ConnSubprofileAssignmentPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnSubprofileAssignResponse> HandleCore(
        ConnSubprofileAssignCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rawPhysicianUserId = request.Body?.PhysicianUserId ?? default;
        var rawSubprofileId = request.Body?.SubprofileId ?? default;

        // L315-L317. JavaScript truthiness over BOTH fields before either is used: absent, null,
        // false, 0 and "" all land here, and an absent body does too.
        if (!ConnJs.IsTruthy(rawPhysicianUserId) || !ConnJs.IsTruthy(rawSubprofileId))
        {
            throw ConnErrors.BadRequest("physicianUserId and subprofileId are required");
        }

        // L320-L321. The caller's own patient profile, and a MISS IS 400 — not 404. Node uses 404
        // three lines further down for the dependant, so the two are genuinely different codes for
        // two genuinely different misses.
        var patientProfileId = await patients.GetPatientProfileIdAsync(
            request.CallerUserId, cancellationToken);

        if (patientProfileId is null) throw ConnErrors.BadRequest("Patient profile not found");

        // A value that is truthy but not a string survives the gate above and is handed straight to
        // Prisma, which rejects it as a type error INSIDE the route's try — so Node answers this
        // route's named 500, not a 400. The position matters: the subprofile query (L323) runs
        // before the connection query (L329), so a bad subprofileId 500s first and a bad
        // physicianUserId only 500s after the dependant has been found.
        var subprofileId = ConnJs.AsString(rawSubprofileId)
                           ?? throw ConnErrors.ServerError(FailureError);

        // L323-L326. Node's `findFirst({ id: subprofileId, patientProfileId: profile.id })` is one
        // query; family subprofiles belong to the Patients module, so here it is a read through
        // IPatientDirectory plus the ownership comparison. No isActive filter, matching Node.
        // BOTH failures collapse to the same 404 so the route cannot be used to enumerate other
        // families' dependant ids.
        var subprofile = await patients.GetSubprofileAsync(subprofileId, cancellationToken);

        if (subprofile is null || subprofile.PatientProfileId != patientProfileId)
        {
            throw ConnErrors.NotFound("Family member not found");
        }

        var physicianUserId = ConnJs.AsString(rawPhysicianUserId)
                              ?? throw ConnErrors.ServerError(FailureError);

        // L329-L334. The PARENT edge: subprofileId compared as a VALUE, so this matches only the
        // row whose subprofile_id IS NULL, and it must be ACCEPTED. A PENDING request to the same
        // doctor does not qualify, and neither does an ACCEPTED edge for a different dependant.
        var parentConnection = await connections.FindPairAsync(
            physicianUserId,
            request.CallerUserId,
            subprofileId: null,
            status: ConnectionStatus.ACCEPTED,
            tracked: false,
            ct: cancellationToken);

        if (parentConnection is null)
        {
            throw ConnErrors.BadRequest("You must be connected to this doctor first");
        }

        // L337-L342. The duplicate probe, deliberately WITHOUT a status predicate: an existing
        // PENDING or REJECTED dependant edge 409s exactly like an ACCEPTED one, and this router
        // offers no way to revive it.
        var existing = await connections.FindPairAsync(
            physicianUserId,
            request.CallerUserId,
            subprofileId,
            status: null,
            tracked: false,
            ct: cancellationToken);

        if (existing is not null)
        {
            throw ConnErrors.Conflict("This doctor is already assigned to this family member");
        }

        // The two reads that Node performs as the create's `include` (L353-L361). They run BEFORE
        // the insert on purpose: in Node the include is part of the create round trip, so a failure
        // there leaves NO row behind, and reading first is the only ordering that reproduces that.
        // Neither value can change during the request, so the response bytes are unaffected.
        var physicianUser = await identity.GetUserAsync(physicianUserId, cancellationToken);
        var physicianProfile = await identity.GetPhysicianByUserIdAsync(
            physicianUserId, cancellationToken);

        // L344-L352. A NEW row. The ACCEPTED parent edge above is left exactly as it was.
        var connection = DoctorPatientConnection.CreateAccepted(
            physicianUserId,
            request.CallerUserId,
            initiatedBy: request.CallerUserId,
            subprofileId: subprofileId);

        connections.Add(connection);
        await database.SaveChangesAsync(cancellationToken);

        return ConnSubprofileAssignResponse.From(
            connection, physicianUser, physicianProfile, subprofile);
    }
}

/// <summary>
/// <c>DELETE /api/connections/unassign-subprofile/{subprofileId}/{physicianUserId}</c> — a WRITE,
/// so the caller's id arrives explicitly and the handler injects no <c>ICurrentUser</c>.
///
/// <para><b>⚠ The route's two segments are <c>{subprofileId}</c> FIRST and
/// <c>{physicianUserId}</c> SECOND</b> (connections.js:375). Both are opaque id strings of the
/// same shape, so swapping them compiles, binds, runs, finds nothing and answers a plausible
/// <c>404 {"error":"Assignment not found"}</c> forever. The record's parameter order below matches
/// the URL's segment order for exactly that reason; the client sends
/// <c>/connections/unassign-subprofile/${subprofileId}/${physicianUserId}</c>
/// (ConnectionsPage.jsx:254, FamilyPage.jsx:125).</para>
/// </summary>
public sealed record ConnSubprofileUnassignCommand(
    string SubprofileId,
    string PhysicianUserId,
    string CallerUserId) : IRequest<ConnSubprofileUnassignResponse>;

/// <summary>
/// Port of <c>DELETE /api/connections/unassign-subprofile/{subprofileId}/{physicianUserId}</c>
/// (connections.js:375-391) → 200 <c>{"message":"Doctor unassigned from family member"}</c>.
///
/// <para><b>One gate, one effect.</b> Look up the row by the exact triple
/// <c>{ physicianUserId, patientUserId: caller, subprofileId }</c> (L380-L382); a miss is
/// <b>404</b> <c>{"error":"Assignment not found"}</c>, and a hit is hard-deleted (L385). There is
/// no second authorization test and none is needed: <c>patientUserId</c> is bound to the caller,
/// so the query can only ever return the caller's own row.</para>
///
/// <para><b>The probe is STATUS-BLIND</b> — no <c>status</c> predicate at all — so a PENDING or
/// REJECTED dependant edge is removed just like an ACCEPTED one. This is the only route in the
/// router that can clear a REJECTED dependant row, and it is also what lets a patient escape the
/// 409 that <c>assign-subprofile</c> raises against one.</para>
///
/// <para><b>The parent edge is unreachable from here.</b> <c>subprofileId</c> is a route segment
/// and therefore never null, and <see cref="IDoctorPatientConnectionStore.FindPairAsync"/>
/// compares it as a VALUE, so the row with <c>subprofile_id IS NULL</c> can never match. A patient
/// cannot accidentally sever their own connection to the doctor by unassigning a child —
/// disconnecting entirely is <c>DELETE /api/connections/{id}</c>, a different route.</para>
///
/// <para><b>The delete is HARD</b> (L385 is <c>prisma.doctorPatientConnection.delete</c>). This
/// model has no <c>deletedAt</c> column in either backend, so there is no softer option and the
/// physician's access to that dependant's chart ends with the row. Nothing cascades: visits,
/// prescriptions and appointments already recorded for the dependant stay exactly where they
/// are.</para>
///
/// <para>No notification, no audit row, and the physician is not told. Matching Node.</para>
/// </summary>
public sealed class ConnSubprofileUnassignHandler(
    IConnectionsDbContext database,
    IDoctorPatientConnectionStore connections,
    IAppLogger<ConnSubprofileUnassignHandler> logger)
    : IRequestHandler<ConnSubprofileUnassignCommand, ConnSubprofileUnassignResponse>
{
    /// <summary>The route's own 500 literal (connections.js:389), verbatim.</summary>
    private const string FailureError = "Failed to unassign doctor";

    /// <summary>The route's Node log line (connections.js:388), verbatim.</summary>
    private const string LogMessage = "[Connections] Unassign subprofile error";

    public Task<ConnSubprofileUnassignResponse> Handle(
        ConnSubprofileUnassignCommand request, CancellationToken cancellationToken = default)
        => ConnSubprofileAssignmentPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnSubprofileUnassignResponse> HandleCore(
        ConnSubprofileUnassignCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // L380-L383. Tracked, because the row is about to be deleted. No status predicate.
        var connection = await connections.FindPairAsync(
            request.PhysicianUserId,
            request.CallerUserId,
            request.SubprofileId,
            status: null,
            tracked: true,
            ct: cancellationToken);

        if (connection is null) throw ConnErrors.NotFound("Assignment not found");

        // L385. A hard delete — the row is the access grant.
        connections.Remove(connection);
        await database.SaveChangesAsync(cancellationToken);

        return ConnSubprofileUnassignResponse.Unassigned;
    }
}
