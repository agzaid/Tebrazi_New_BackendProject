using System.Text.Json;
using System.Text.Json.Nodes;
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
//  The prescription LIFECYCLE — creation and the four state writes, ported from
//  server/src/routes/prescriptions.js:
//
//      POST /api/prescriptions               (L99-L138)   -> 201
//      PUT  /api/prescriptions/{id}          (L242-L275)  -> 200
//      PUT  /api/prescriptions/{id}/sign     (L280-L302)  -> 200
//      PUT  /api/prescriptions/{id}/send     (L308-L435)  -> 200
//      PUT  /api/prescriptions/{id}/dispense (L446-L512)  -> 200
//
//  Facts that hold across the whole group, stated once here rather than five times:
//
//  * They are WRITE endpoints, so per the module's house rule the caller's user id arrives as an
//    explicit `CallerUserId` on the command; none of them injects ICurrentUser.
//  * THERE IS NO STATE MACHINE, with exactly ONE exception. /sign, /send and /dispense have no
//    status precondition at all: a DISPENSED row can be re-signed (regressing to CONFIRMED while
//    keeping sentToPatientAt), re-sent (regressing to SENT), or dispensed again. The single
//    precondition in the entire router lives on PUT /{id} — `status === 'SENT'` -> 400 — and
//    DISPENSED and CONFIRMED stay editable there.
//  * THERE IS NO deletedAt FILTER anywhere. `IPrescriptionStore.GetForUpdateAsync` is a bare
//    findUnique by id, matching Node, so a soft-deleted prescription can still be updated,
//    signed, sent and dispensed. Do not add a guard the client does not expect.
//  * 404 PRECEDES 403 on all four id-addressed routes: the row is read first, so a caller with no
//    rights to a row that does not exist learns that it does not exist.
//  * NO ROUTE CACHE IS INVALIDATED. `GET /` (15s) and `GET /summary` (30s) are wrapped in
//    cacheMiddleware with zero invalidation anywhere in prescriptions.js, so in Node the client's
//    own refresh can legitimately return the PRE-mutation array and counts for up to 15s/30s
//    after every endpoint in this file. The port deliberately does not reproduce that stale
//    window — a fresher response cannot break the client, a stale one can — which means the .NET
//    backend is strictly more current here than Node. Recorded because a reader diffing the two
//    would otherwise think the caching was missed; the decision and its rationale live in
//    docs/prescriptions-surface.md §11.
//
//  THE AUTO-SIGN INVERSION, which shapes the whole group: `POST /` writes status SIGNED with
//  signedAt already stamped (prescriptions.js:125-126), and `PUT /{id}/sign` writes status
//  CONFIRMED (prescriptions.js:294). So DRAFT is unreachable through any ported endpoint, the
//  enum member named SIGNED is only ever the creation state, and "signing" is the rung ABOVE it.
//  Neither of those is a bug to fix; both are the contract, and `GET /summary`'s `signed` bucket
//  counts BOTH statuses precisely because of it.
//
//  THE TWO NO-OP CROSS-MODULE PORTS. `/send` schedules medication reminders through
//  IMedicationReminderScheduler and `/dispense` deducts stock through IInventoryDeductionWriter.
//  Both modules are unwritten, both ports are registered to placeholders that write nothing and
//  log at warning level exactly what they skipped, and neither handler pretends the effect
//  happened. Each call site below names the Node lines it does not reproduce. What IS fully
//  implemented is each endpoint's own behaviour: the status write, the response bytes, and the
//  notifications this module owns.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// JavaScript coercions over <see cref="JsonNode"/>, which <see cref="RxJs"/> does not cover — it
/// speaks <see cref="JsonElement"/>, and <c>/send</c> walks the stored <c>medications</c> column,
/// which is parsed as a node tree so it can be echoed back as raw JSON elsewhere.
///
/// <para>Declared internal to this file with the group's prefix on purpose: four sibling handler
/// files are being written into this one namespace and an unprefixed helper would collide with a
/// sibling nobody can see until the solution builds.</para>
/// </summary>
internal static class RxLifecycleJs
{
    /// <summary>
    /// JavaScript truthiness. <c>null</c>, <c>false</c>, <c>0</c> and <c>""</c> are falsy;
    /// <b>every object and array is truthy, including <c>{}</c> and <c>[]</c></b>. A JSON
    /// <c>null</c> inside an array arrives here as a C# null and is falsy — but see
    /// <c>RxLifecycleSendPrescriptionHandler</c>, where reading a PROPERTY off that null is a
    /// TypeError in Node and must abort the whole block rather than skip one element.
    /// </summary>
    public static bool IsTruthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is JsonObject or JsonArray) return true;

        var value = node.AsValue();

        if (value.TryGetValue<bool>(out var flag)) return flag;
        if (value.TryGetValue<string>(out var text)) return text.Length > 0;
        if (value.TryGetValue<double>(out var number)) return number != 0;

        return true;
    }

    /// <summary>
    /// The node's value when it is a JSON STRING, and null for every other kind — including a
    /// truthy non-string. Used where Node would call a string method on the value and throw a
    /// TypeError for anything else.
    /// </summary>
    public static string? AsString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// <c>Array.isArray(prescription.medications) ? prescription.medications : []</c>
    /// (prescriptions.js:352) over the stored JSON text. A scalar, an object, or text Prisma could
    /// never have written all degrade to the empty array rather than failing the request.
    /// </summary>
    public static JsonArray AsArrayOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonArray();

        try
        {
            return JsonNode.Parse(json) as JsonArray ?? new JsonArray();
        }
        catch (JsonException)
        {
            return new JsonArray();
        }
    }
}

/// <summary>
/// The 404-then-403 gate the four id-addressed routes share verbatim
/// (prescriptions.js:247-253, :284-290, :312-318, :451-457).
///
/// <para>All four read the row with a bare <c>findUnique({ where: { id } })</c> — no
/// <c>deletedAt</c> filter — answer <c>404 {"error":"Prescription not found"}</c> for a miss, then
/// resolve the caller's OWN physician profile and require
/// <c>prescription.physicianId === profile.id</c>.</para>
///
/// <para><b>The 403 collapses two different failures into one message.</b> "The caller is not a
/// physician at all" and "the prescription belongs to another physician" both answer
/// <c>403 {"error":"Not your prescription"}</c>. There is deliberately no "Physician profile
/// required" here — that literal exists only on <c>POST /</c>, and only with status 400.</para>
///
/// <para>Identity, not clinic membership: nothing consults <see cref="IClinicDirectory"/>, so a
/// colleague at the same clinic is not authorized and the authoring physician stays authorized at
/// a clinic they no longer own. Adding a clinic access test would grant access Node does not.</para>
/// </summary>
internal static class RxLifecycleGuard
{
    public static async Task<(Prescription Prescription, PhysicianSummary Physician)> LoadOwnedAsync(
        string prescriptionId,
        string callerUserId,
        IPrescriptionStore prescriptions,
        IIdentityDirectory identity,
        CancellationToken cancellationToken)
    {
        var prescription = await prescriptions.GetForUpdateAsync(prescriptionId, cancellationToken)
            ?? throw RxErrors.NotFound("Prescription not found");

        var physician = await identity.GetPhysicianByUserIdAsync(callerUserId, cancellationToken);

        if (physician is null || prescription.PhysicianId != physician.Id)
            throw RxErrors.Forbidden("Not your prescription");

        return (prescription, physician);
    }
}

/// <summary>
/// The per-file catch-all guard, plus the save wrapper, that map any failure onto the ROUTE'S OWN
/// named 500 body.
///
/// <para>Every handler in <c>prescriptions.js</c> is wrapped end to end in a try/catch whose only
/// outcome is a named literal — <c>"Failed to create prescription"</c>,
/// <c>"Failed to update prescription"</c>, <c>"Failed to sign prescription"</c>,
/// <c>"Failed to send prescription"</c>, <c>"Failed to dispense prescription"</c>. Letting an EF
/// or port exception escape would emit <c>ExceptionHandlingMiddleware</c>'s generic
/// <c>{"error":"Internal Server Error","message":"An unexpected error occurred"}</c> instead, and
/// the React client branches on <c>err.response.data.error</c>.</para>
///
/// <para>Because Node's try opens BEFORE the row read, everything inside
/// <see cref="RxLifecycleGuard.LoadOwnedAsync"/> — the prescription read and the physician-profile
/// lookup — is covered by the same named 500. An <see cref="AppException"/> passes through
/// untouched, so the deliberate 400s, 403s and 404s keep their exact bodies and
/// <see cref="SaveAsync"/>'s own named 500 is neither re-wrapped nor re-logged.</para>
///
/// <para><b>What this guard must NOT swallow the wrong way round:</b> <c>/send</c>'s two
/// side-effect blocks each carry their OWN nested catch in Node, so a failure inside either still
/// answers 200 — including a failure of the visit READ that sits inside them. Those nested
/// catches are reproduced as nested catches in the handler, not folded into this guard.
/// <c>/dispense</c> is the opposite: its inventory loop has no inner catch, so a failure there
/// really is the route's 500, on an already-DISPENSED prescription.</para>
/// </summary>
internal static class RxLifecyclePersistence
{
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

    public static async Task SaveAsync<THandler>(
        IPrescriptionsDbContext dbContext,
        IAppLogger<THandler> logger,
        string logMessage,
        string failureError,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is not AppException && !cancellationToken.IsCancellationRequested)
        {
            logger.Error(logMessage, exception);
            throw RxErrors.ServerError(failureError);
        }
    }
}

// ── POST /api/prescriptions ──────────────────────────────────────────────────

/// <summary>
/// The three body fields <c>POST /api/prescriptions</c> reads (prescriptions.js:102). Every other
/// key is silently ignored — a client-supplied <c>subprofileId</c>, <c>status</c> or
/// <c>signedAt</c> is NOT honoured.
///
/// <para>All three are non-nullable <see cref="JsonElement"/> rather than typed members because
/// Node applies JavaScript truthiness and then hands the raw value to Prisma, and both halves of
/// that are observable. A typed <c>string?</c> would let the model binder answer
/// <c>400 {"error":"Validation failed", …}</c> — a body this router never produces — for inputs
/// that must reach the handler's own 400, or Prisma's 500. Non-nullable so an ABSENT key
/// (<c>JsonValueKind.Undefined</c>) stays distinguishable from an explicit
/// <c>null</c> (<c>JsonValueKind.Null</c>); this route treats them alike, but the sibling
/// <c>PUT /{id}</c> does not, and one convention across the file is safer than two.</para>
/// </summary>
public sealed record RxLifecycleCreateBody(
    JsonElement VisitId,
    JsonElement Medications,
    JsonElement Notes);

public sealed record RxLifecycleCreateCommand(
    string CallerUserId,
    RxLifecycleCreateBody? Body) : IRequest<RxLifecycleCreatedResponse>;

/// <summary>
/// Port of <c>POST /api/prescriptions</c> (prescriptions.js:99-138) → <b>201</b>.
///
/// <para><b>It auto-signs.</b> The row is inserted with status SIGNED and <c>signedAt</c> already
/// stamped, via <see cref="Prescription.CreateAutoSigned"/>. <see cref="Prescription.Create"/> is
/// never called from here: it defaults to DRAFT, and a DRAFT row is unreachable through the API
/// and invisible to the patient list, whose filter is CONFIRMED/SENT/DISPENSED. A side effect of
/// the auto-sign is that the PATIENT cannot see a just-created prescription until <c>/sign</c> or
/// <c>/send</c> runs, because SIGNED is not in that filter.</para>
///
/// <para><b>The two status codes look wrong and are the contract.</b> A missing physician profile
/// is <c>400 {"error":"Physician profile required"}</c>, while a foreign OR NONEXISTENT visit is
/// <c>403 {"error":"Visit not found or not yours"}</c> — never 404. Node resolves the visit with
/// one <c>findFirst({ id, physicianId })</c>, so both visit failures collapse into the 403.</para>
///
/// <para><c>subprofileId</c> is inherited from the visit; <c>medications</c> is stored with zero
/// normalization (no trimming, no per-element <c>drugName</c> requirement, no dedup) so
/// <c>[null]</c>, <c>[{}]</c> and <c>[1,2,3]</c> all pass; and <c>notes</c> goes through
/// <c>notes || null</c>, so <c>""</c> is persisted as SQL NULL rather than the empty string —
/// which is NOT what the sibling <c>PUT /{id}</c> does with the same field.</para>
///
/// <para>Two Node side effects are absent by design. The <c>auditLog('CREATE','prescription')</c>
/// middleware — this is the router's ONLY audited route — writes an <c>activity_logs</c> row
/// fire-and-forget with its failure swallowed; <c>activity_logs</c> is not ported, so the row is
/// simply not written and nothing in the response changes. And the global body sanitizer in
/// <c>server/src/index.js</c> rewrites every string in the body before Node's handler sees it, so
/// stored drug names and notes containing <c>data:</c>, <c>javascript:</c> or <c>url(</c> differ
/// between the two backends. Both are known, recorded parity gaps, not things to implement here.
/// </para>
/// </summary>
public sealed class RxLifecycleCreatePrescriptionHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IVisitDirectory visits,
    IAppLogger<RxLifecycleCreatePrescriptionHandler> logger)
    : IRequestHandler<RxLifecycleCreateCommand, RxLifecycleCreatedResponse>
{
    private const string LogMessage = "[Prescriptions] Create error";
    private const string FailureError = "Failed to create prescription";

    public Task<RxLifecycleCreatedResponse> Handle(
        RxLifecycleCreateCommand request, CancellationToken cancellationToken = default)
        => RxLifecyclePersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxLifecycleCreatedResponse> HandleCore(
        RxLifecycleCreateCommand request, CancellationToken cancellationToken)
    {
        // ONE 400 covers all four failures (prescriptions.js:104-106):
        //   !visitId || !medications || !Array.isArray(medications) || medications.length === 0
        // Note the array test comes after the truthiness test, so `medications: {}` is truthy but
        // not an array and still lands here, and `medications: []` is truthy with length 0.
        var visitIdValue = request.Body?.VisitId ?? default;
        var medicationsValue = request.Body?.Medications ?? default;

        if (!RxJs.IsTruthy(visitIdValue)
            || medicationsValue.ValueKind != JsonValueKind.Array
            || medicationsValue.GetArrayLength() == 0)
        {
            throw RxErrors.BadRequest("visitId and at least one medication are required");
        }

        // 400, not 403 — the only route in the file with this literal.
        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken)
            ?? throw RxErrors.BadRequest("Physician profile required");

        // A truthy NON-STRING visitId (a number, true, an object) survives the truthiness test and
        // reaches `where: { id: visitId }`, where Prisma rejects it as the wrong type for a String
        // column and Node answers the route's 500 — not the 403 and not a 400. Reproduced here,
        // in Node's position: after the physician lookup, before the visit lookup.
        if (visitIdValue.ValueKind != JsonValueKind.String)
            throw RxErrors.ServerError(FailureError);

        var visitId = visitIdValue.GetString()!;

        // `findFirst({ where: { id: visitId, physicianId: physician.id } })` -> 403 on a miss.
        // Split across the port's two methods, as docs/prescriptions-surface.md §3.2 prescribes:
        // BelongsToPhysicianAsync answers the gate, GetAsync then supplies subprofileId for the
        // insert and chiefComplaint/visitDate for the nested response object. A null from GetAsync
        // can only mean the visit vanished between the two reads, and takes the same 403 the
        // single Node query would have given.
        if (!await visits.BelongsToPhysicianAsync(visitId, physician.Id, cancellationToken))
            throw RxErrors.Forbidden("Visit not found or not yours");

        var visit = await visits.GetAsync(visitId, cancellationToken)
            ?? throw RxErrors.Forbidden("Visit not found or not yours");

        // `notes: notes || null` — JS truthiness, so "", 0 and false all become NULL. A truthy
        // non-string reaches Prisma as the wrong type for a String? column and is the route's 500.
        var notesValue = request.Body?.Notes ?? default;
        string? notes = null;

        if (RxJs.IsTruthy(notesValue))
        {
            if (notesValue.ValueKind != JsonValueKind.String)
                throw RxErrors.ServerError(FailureError);

            notes = notesValue.GetString();
        }

        // The medications array is stored VERBATIM as JSON text — extra keys, nulls and all.
        var prescription = Prescription.CreateAutoSigned(
            visitId,
            physician.Id,
            medicationsValue.GetRawText(),
            visit.SubprofileId,
            notes);

        prescriptions.Add(prescription);

        await RxLifecyclePersistence.SaveAsync(
            dbContext, logger, LogMessage, FailureError, cancellationToken);

        return RxLifecycleMapper.ToCreatedResponse(prescription, visit);
    }
}

// ── PUT /api/prescriptions/{id} ──────────────────────────────────────────────

/// <summary>
/// The two body fields <c>PUT /api/prescriptions/{id}</c> reads (prescriptions.js:244).
///
/// <para><b>Non-nullable <see cref="JsonElement"/> is load-bearing here</b>, not stylistic. Node
/// builds its patch with <c>!== undefined</c> guards, so this route is the one place in the module
/// where an ABSENT key and an explicit <c>null</c> mean different things: absent PRESERVES the
/// column, explicit null CLEARS it (notes) or is a 500 (medications). A nullable
/// <c>JsonElement?</c> binds both cases to C# null and would lose the distinction; a non-nullable
/// member gives <see cref="JsonValueKind.Undefined"/> for absent and
/// <see cref="JsonValueKind.Null"/> for explicit null.</para>
/// </summary>
public sealed record RxLifecycleUpdateBody(
    JsonElement Medications,
    JsonElement Notes);

public sealed record RxLifecycleUpdateCommand(
    string PrescriptionId,
    string CallerUserId,
    RxLifecycleUpdateBody? Body) : IRequest<RxLifecycleUpdatedResponse>;

/// <summary>
/// Port of <c>PUT /api/prescriptions/{id}</c> (prescriptions.js:242-275) → 200.
///
/// <para><b>This route holds the ONLY status precondition in the entire router:</b>
/// <c>if (prescription.status === 'SENT')</c> →
/// <c>400 {"error":"Cannot edit a prescription that has already been sent"}</c>
/// (prescriptions.js:256-258). CONFIRMED, SIGNED, DISPENSED and DRAFT all remain editable — the
/// guard is a single-status test, not a "terminal state" test, so a DISPENSED prescription can
/// still have its drug lines rewritten. The test runs AFTER the 404 and the 403.</para>
///
/// <para><b>An empty body is a successful 200 that still bumps <c>updatedAt</c>.</b> Node's
/// <c>prisma.update({ data: {} })</c> succeeds and Prisma's <c>@updatedAt</c> fires anyway; EF's
/// change tracker would see nothing modified and write nothing, so
/// <see cref="IPrescriptionStore.MarkModified"/> forces the UPDATE. A port that short-circuited on
/// an empty patch would leave the timestamp behind Node's.</para>
///
/// <para>Two Prisma type failures are reproduced as this route's own 500 rather than as a clear or
/// a coercion. An explicit <c>medications: null</c> is refused by Prisma on a required Json column,
/// so it is <c>500 {"error":"Failed to update prescription"}</c> — persisting the four-character
/// text <c>"null"</c> would be a silent divergence, which is why
/// <see cref="Prescription.ReplaceMedications"/> is not called on that path. A non-null,
/// non-string <c>notes</c> is likewise the wrong type for a <c>String?</c> column and is the same
/// 500. Conversely <c>medications</c> is written with NO array validation at all, so
/// <c>{"medications": 5}</c> really is persisted as the JSON scalar <c>5</c> — which will later
/// crash <c>/stop-medication</c>, and that too is the contract.</para>
///
/// <para>Note <c>notes</c> is written VERBATIM here, unlike <c>POST /</c>'s
/// <c>notes || null</c>: <c>notes: ""</c> stores the empty string on this route and SQL NULL on
/// that one.</para>
///
/// <para>Not called by the React client — every client call goes to a <c>/{id}/{action}</c>
/// sub-route — and ported deliberately for parity per docs/port-contracts/audit-1.json.</para>
/// </summary>
public sealed class RxLifecycleUpdatePrescriptionHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IAppLogger<RxLifecycleUpdatePrescriptionHandler> logger)
    : IRequestHandler<RxLifecycleUpdateCommand, RxLifecycleUpdatedResponse>
{
    private const string LogMessage = "[Prescriptions] Update error";
    private const string FailureError = "Failed to update prescription";

    public Task<RxLifecycleUpdatedResponse> Handle(
        RxLifecycleUpdateCommand request, CancellationToken cancellationToken = default)
        => RxLifecyclePersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxLifecycleUpdatedResponse> HandleCore(
        RxLifecycleUpdateCommand request, CancellationToken cancellationToken)
    {
        var (prescription, _) = await RxLifecycleGuard.LoadOwnedAsync(
            request.PrescriptionId, request.CallerUserId, prescriptions, identity, cancellationToken);

        // The router's only status precondition, and only SENT is blocked.
        if (prescription.Status == PrescriptionStatus.SENT)
            throw RxErrors.BadRequest("Cannot edit a prescription that has already been sent");

        var medications = request.Body?.Medications ?? default;
        var notes = request.Body?.Notes ?? default;

        // `if (medications !== undefined) updateData.medications = medications;`
        if (medications.ValueKind != JsonValueKind.Undefined)
        {
            // Prisma refuses a bare null on a required Json column.
            if (medications.ValueKind == JsonValueKind.Null)
                throw RxErrors.ServerError(FailureError);

            prescription.ReplaceMedications(medications.GetRawText());
        }

        // `if (notes !== undefined) updateData.notes = notes;` — verbatim, no `|| null`.
        if (notes.ValueKind != JsonValueKind.Undefined)
        {
            if (notes.ValueKind == JsonValueKind.Null)
            {
                prescription.SetNotes(null);
            }
            else if (notes.ValueKind == JsonValueKind.String)
            {
                prescription.SetNotes(notes.GetString());
            }
            else
            {
                // Wrong type for a String? column.
                throw RxErrors.ServerError(FailureError);
            }
        }

        // Node's `prisma.prescription.update` at prescriptions.js:264-267 is UNCONDITIONAL on
        // every path that reaches it, so Prisma's `@updatedAt` always fires: for an EMPTY body
        // `{}` (`data: {}` succeeds), and equally for a patch whose value EQUALS the stored one --
        // byte-identical `medications` JSON, or `notes` already holding that string, or
        // `notes: null` on a row whose notes are already null. EF sees no modified property in any
        // of those cases and would write nothing, so `MarkModified` is unconditional here too.
        prescriptions.MarkModified(prescription);

        await RxLifecyclePersistence.SaveAsync(
            dbContext, logger, LogMessage, FailureError, cancellationToken);

        return RxLifecycleMapper.ToUpdatedResponse(prescription);
    }
}

// ── PUT /api/prescriptions/{id}/sign ─────────────────────────────────────────

public sealed record RxLifecycleSignCommand(
    string PrescriptionId,
    string CallerUserId) : IRequest<RxLifecycleSignedResponse>;

/// <summary>
/// Port of <c>PUT /api/prescriptions/{id}/sign</c> (prescriptions.js:280-302) → 200.
///
/// <para><b>It writes status CONFIRMED, not SIGNED</b> (prescriptions.js:294), because
/// <c>POST /</c> already wrote SIGNED at creation. And it overwrites <c>signedAt</c>
/// UNCONDITIONALLY — the Node update payload is exactly
/// <c>{ status: 'CONFIRMED', signedAt: new Date() }</c>, with no <c>|| prescription.signedAt</c>
/// preservation, so re-signing MOVES the timestamp and loses the original. Both are encoded in
/// <see cref="Prescription.Sign"/>; the payload was re-checked field by field against the Node
/// source, because the Appointments phase found a mistaken <c>??=</c> on three status transitions
/// and this is the same shape of trap.</para>
///
/// <para>No status precondition, no <c>deletedAt</c> filter, and no side effects at all: no
/// notification, no reminder, no audit row. Signing a SENT or DISPENSED row succeeds and regresses
/// it to CONFIRMED while leaving <c>sentToPatientAt</c> populated.</para>
///
/// <para>The request body is read by Node but never used, and the client sends none.</para>
/// </summary>
public sealed class RxLifecycleSignPrescriptionHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IAppLogger<RxLifecycleSignPrescriptionHandler> logger)
    : IRequestHandler<RxLifecycleSignCommand, RxLifecycleSignedResponse>
{
    private const string LogMessage = "[Prescriptions] Sign error";
    private const string FailureError = "Failed to sign prescription";

    public Task<RxLifecycleSignedResponse> Handle(
        RxLifecycleSignCommand request, CancellationToken cancellationToken = default)
        => RxLifecyclePersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxLifecycleSignedResponse> HandleCore(
        RxLifecycleSignCommand request, CancellationToken cancellationToken)
    {
        var (prescription, _) = await RxLifecycleGuard.LoadOwnedAsync(
            request.PrescriptionId, request.CallerUserId, prescriptions, identity, cancellationToken);

        // status -> CONFIRMED, signedAt -> now, unconditionally.
        prescription.Sign();

        await RxLifecyclePersistence.SaveAsync(
            dbContext, logger, LogMessage, FailureError, cancellationToken);

        return RxLifecycleMapper.ToSignedResponse(prescription, "Prescription signed");
    }
}

// ── PUT /api/prescriptions/{id}/send ─────────────────────────────────────────

public sealed record RxLifecycleSendCommand(
    string PrescriptionId,
    string CallerUserId) : IRequest<RxLifecycleSentResponse>;

/// <summary>
/// Port of <c>PUT /api/prescriptions/{id}/send</c> (prescriptions.js:308-435) → 200.
///
/// <para>The endpoint's own behaviour is the status write — <c>status: 'SENT'</c> and
/// <c>sentToPatientAt: new Date()</c>, and NOT <c>signedAt</c> — plus the patient notification.
/// Then two side-effect blocks run, in Node's order.</para>
///
/// <para><b>Both side-effect blocks are best-effort and each has its OWN nested catch</b>
/// (prescriptions.js:343, :412), so a failure in either still answers 200. That includes the
/// READS inside them: Node queries the visit separately in each block, and a throwing read must
/// not turn a committed 200 into a 500. The two reads are kept as two reads for exactly that
/// reason — resolving the value once would let a transient failure in the first block silently
/// suppress the second, which Node's retry does not.</para>
///
/// <para>The reminder work goes through <see cref="IMedicationReminderScheduler"/>, whose only
/// implementation today is a placeholder that writes nothing and logs what it skipped. What it
/// does NOT reproduce is prescriptions.js:373-406: the per-drug purge of the patient's existing
/// ACTIVE MEDICATION reminders (a case-sensitive <c>contains</c> on the title, so it also wipes
/// that drug's reminders from OTHER prescriptions, and a short drug name wipes any drug whose name
/// contains it) and the one <c>reminders</c> row per dose time. This handler still builds every
/// row in full through <see cref="MedicationSchedule.BuildGroup"/>, so the titles, descriptions,
/// <c>remindAt</c> instants and six-key <c>notes</c> JSON stay diffable against Node — and it
/// still owns the DOSE COUNT, because the count decides whether its own second notification
/// fires. The placeholder returns 0, so it does not, and the patient's inbox stays honest rather
/// than announcing reminders that do not exist.</para>
///
/// <para>Nothing about either side effect reaches the response, which is what makes this body
/// byte-identical to Node's regardless.</para>
///
/// <para>No status precondition and no idempotency: a DISPENSED row can be re-sent, regressing to
/// SENT, and every re-send re-runs the whole chain — another notification, another email, another
/// push, another purge, another full set of reminder rows.</para>
/// </summary>
public sealed class RxLifecycleSendPrescriptionHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IVisitDirectory visits,
    INotificationPublisher notifications,
    IMedicationReminderScheduler reminders,
    IAppLogger<RxLifecycleSendPrescriptionHandler> logger)
    : IRequestHandler<RxLifecycleSendCommand, RxLifecycleSentResponse>
{
    private const string LogMessage = "[Prescriptions] Send error";
    private const string FailureError = "Failed to send prescription";

    public Task<RxLifecycleSentResponse> Handle(
        RxLifecycleSendCommand request, CancellationToken cancellationToken = default)
        => RxLifecyclePersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxLifecycleSentResponse> HandleCore(
        RxLifecycleSendCommand request, CancellationToken cancellationToken)
    {
        var (prescription, physician) = await RxLifecycleGuard.LoadOwnedAsync(
            request.PrescriptionId, request.CallerUserId, prescriptions, identity, cancellationToken);

        // The reminder block reads `prescription.medications` from the PRE-update snapshot
        // (prescriptions.js:352). The transition does not touch the column, so the value is
        // identical either way, but the snapshot is taken here to keep the ordering honest.
        var medicationsSnapshot = prescription.Medications;

        // status -> SENT, sentToPatientAt -> now. signedAt is NOT touched, so a never-signed row
        // reads SENT with signedAt still null. No DRAFT guard — the Node comment "Auto-sign is now
        // done at creation" documents its removal.
        prescription.SendToPatient();

        await RxLifecyclePersistence.SaveAsync(
            dbContext, logger, LogMessage, FailureError, cancellationToken);

        await NotifyPatientAsync(prescription, physician, request, cancellationToken);
        await ScheduleRemindersAsync(prescription, medicationsSnapshot, request, cancellationToken);

        return RxLifecycleMapper.ToSentResponse(prescription, "Prescription sent to patient");
    }

    /// <summary>
    /// Side-effect block 1 (prescriptions.js:328-348): notify the patient, with an email.
    ///
    /// <para>Reproduces Node's own <c>catch (e) { logger.error(…) }</c>, so a failure of the visit
    /// read inside it cannot fail the request. <see cref="INotificationPublisher"/> itself never
    /// throws — that guarantee is what replaces Node's inner catch around
    /// <c>createNotification</c> — so no extra try/catch is wrapped around the publish.</para>
    ///
    /// <para>Node reads the sending doctor's display name with a separate
    /// <c>user.findUnique({ where: { id: req.user.id } })</c>. The name on
    /// <see cref="PhysicianSummary"/> IS that user's <c>displayName</c>, already in hand from the
    /// authorization gate, so the extra round trip is dropped; the <c>|| 'Your doctor'</c> fallback
    /// is kept because an empty display name is falsy in JS. <c>"Dr. "</c> is hardcoded in Node, so
    /// a name that already carries a title renders "Dr. Dr. Ahmed" — reproduced, not fixed.</para>
    ///
    /// <para>The notification only fires when the visit's <c>patientUserId</c> is truthy, and its
    /// <c>data.prescriptionId</c> is the RAW <c>:id</c> route segment, not the reloaded row's id.
    /// </para>
    /// </summary>
    private async Task NotifyPatientAsync(
        Prescription prescription,
        PhysicianSummary physician,
        RxLifecycleSendCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var visit = await visits.GetAsync(prescription.VisitId, cancellationToken);

            if (RxJs.Truthy(visit?.PatientUserId) is not { } patientUserId) return;

            var doctorName = RxJs.Truthy(physician.DisplayName) ?? "Your doctor";

            await notifications.PublishAsync(
                new NotificationRequest(
                    UserId: patientUserId,
                    Type: "PRESCRIPTION_SENT",
                    Title: "New Prescription Available",
                    Message: $"Dr. {doctorName} has sent you a new prescription."
                        + " View it in your Prescriptions page.",
                    Data: new JsonObject { ["prescriptionId"] = request.PrescriptionId }.ToJsonString(),
                    SendEmail: true),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Error("[Prescriptions] Notification error", exception);
        }
    }

    /// <summary>
    /// Side-effect block 2 (prescriptions.js:351-428): auto-create the medication reminders, then
    /// notify the patient about them when any were created.
    ///
    /// <para>Wrapped in Node's own swallowing catch, so the visit read, the medication walk and the
    /// scheduler call can all fail without touching the committed 200. The whole block is gated on
    /// <c>patientUserId &amp;&amp; medications.length &gt; 0</c>, which is also why the summary log
    /// line and the second notification never fire for an empty medications array.</para>
    ///
    /// <para>The second notification fires only when the scheduler reports a POSITIVE count. The
    /// port returns 0 on any failure and never a partial count, deliberately: Node's
    /// <c>if (remindersCreated &gt; 0)</c> sits inside the same try a mid-loop throw escapes, so a
    /// partial count here would fire a notification Node would not.</para>
    /// </summary>
    private async Task ScheduleRemindersAsync(
        Prescription prescription,
        string medicationsSnapshot,
        RxLifecycleSendCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var medications = RxLifecycleJs.AsArrayOrEmpty(medicationsSnapshot);

            // Node's SECOND visit query for the same value (prescriptions.js:350, :364). Kept as a
            // second read so a transient failure of the first cannot suppress this block.
            var visit = await visits.GetAsync(prescription.VisitId, cancellationToken);

            if (RxJs.Truthy(visit?.PatientUserId) is not { } patientUserId) return;
            if (medications.Count == 0) return;

            // One reference instant for the whole prescription. Node re-reads the clock per dose,
            // which is unobservable, but a single instant keeps the rows self-consistent.
            var groups = BuildReminderGroups(medications, DateTime.Now);

            // null means a medication element made Node throw, which aborts the ENTIRE block:
            // no purge, no rows, no notification. See BuildReminderGroups.
            if (groups is null) return;

            var remindersCreated = 0;

            if (groups.Count > 0)
            {
                // NO-OP PORT. Does not reproduce prescriptions.js:373-406 — the per-drug purge of
                // existing ACTIVE MEDICATION reminders, and one reminders row per dose time. The
                // placeholder logs the prescription, patient, drug count and dose count it skipped
                // and returns 0, which correctly suppresses the notification below.
                remindersCreated = await reminders.ScheduleAsync(
                    new MedicationReminderPlan(
                        PatientUserId: patientUserId,
                        // reminders.created_by_id is the SENDING PHYSICIAN's user id, while
                        // reminders.user_id is the patient's (prescriptions.js:394-395).
                        CreatedByUserId: request.CallerUserId,
                        PrescriptionId: request.PrescriptionId,
                        Medications: groups),
                    cancellationToken);
            }

            if (remindersCreated > 0)
            {
                // `${n} medication reminder${n > 1 ? 's have' : ' has'} been automatically created
                //  from your new prescription.` — note the pluralization straddles the space.
                var plural = remindersCreated > 1 ? "s have" : " has";

                // Node nests this notification in a try/catch OF ITS OWN, with its own log literal
                // (prescriptions.js:412-423). That is load-bearing for the two lines below it: a
                // throw here is swallowed at :423, so the `logger.info(... 'Auto-created medication
                // reminders')` at :426 STILL RUNS and the outer block-2 catch at :428 is never
                // reached — so the '[Prescriptions] Auto-reminder creation error' literal is not
                // emitted for this failure. Folding the two catches together would log the wrong
                // literal and skip the info line. INotificationPublisher documents that it never
                // throws, so this is belt-and-braces today, but the shape has to match.
                try
                {
                    await notifications.PublishAsync(
                        new NotificationRequest(
                            UserId: patientUserId,
                            Type: "MEDICATION_REMINDER_SET",
                            Title: "Medication Reminders Set",
                            Message: $"{remindersCreated} medication reminder{plural}"
                                + " been automatically created from your new prescription.",
                            Data: new JsonObject { ["prescriptionId"] = request.PrescriptionId }.ToJsonString(),
                            SendEmail: false),
                        cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.Error("[Prescriptions] Reminder notification error", exception);
                }
            }

            logger.Information(
                "[Prescriptions] Auto-created medication reminders",
                new { prescriptionId = request.PrescriptionId, remindersCreated });
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Error("[Prescriptions] Auto-reminder creation error", exception);
        }
    }

    /// <summary>
    /// Walks the stored medications array and builds one fully composed reminder group per
    /// medication that has BOTH a truthy <c>drugName</c> and a truthy <c>frequency</c>
    /// (prescriptions.js:362-409).
    ///
    /// <para>Returns <b>null</b> to mean "Node would have thrown here", which aborts the entire
    /// block rather than skipping one element. Three inputs do that:</para>
    ///
    /// <list type="bullet">
    /// <item>a JSON <c>null</c> element — <c>null.frequency</c> is a TypeError;</item>
    /// <item>a truthy non-string <c>frequency</c> — <c>parseFrequency</c> calls
    /// <c>.toLowerCase()</c> unguarded;</item>
    /// <item>a truthy non-string <c>drugName</c> — it is sent into Prisma's <c>contains</c> as the
    /// purge key, which rejects the wrong type.</item>
    /// </list>
    ///
    /// <para>Everything else that is not an object — a string, a number, a boolean, a nested array
    /// — has an <c>undefined</c> <c>frequency</c> and is skipped silently, contributing neither a
    /// purge nor a row, exactly like an object with an empty-string frequency.</para>
    ///
    /// <para>Divergence worth naming: where the abort happens mid-array, Node has already written
    /// the earlier medications' purges and rows before throwing, whereas this builds the whole plan
    /// before submitting it and therefore writes nothing. Unobservable today — the port is a no-op
    /// and neither path fires the notification, because the throw escapes Node's
    /// <c>if (remindersCreated &gt; 0)</c> too — but it is a real difference once Reminders lands.
    /// </para>
    /// </summary>
    private static List<MedicationReminderGroup>? BuildReminderGroups(
        JsonArray medications, DateTime nowLocal)
    {
        var groups = new List<MedicationReminderGroup>(medications.Count);

        foreach (var element in medications)
        {
            // A JSON null element: reading a property off it is a TypeError.
            if (element is null) return null;

            // Not an object: `.frequency` is undefined, which is falsy, so skip.
            if (element is not JsonObject medication) continue;

            var frequency = medication["frequency"];
            var drugName = medication["drugName"];

            // `if (!med.frequency || !med.drugName) continue;`
            if (!RxLifecycleJs.IsTruthy(frequency) || !RxLifecycleJs.IsTruthy(drugName)) continue;

            if (RxLifecycleJs.AsString(frequency) is not { } frequencyText) return null;
            if (RxLifecycleJs.AsString(drugName) is not { } drugNameText) return null;

            groups.Add(MedicationSchedule.BuildGroup(
                drugNameText, ResolveDosage(medication["dosage"]), frequencyText, nowLocal));
        }

        return groups;
    }

    /// <summary>
    /// <c>med.dosage || ''</c> (prescriptions.js:368). Null for every falsy value, which
    /// <see cref="MedicationSchedule.BuildGroup"/> treats as the empty string and which makes the
    /// description's <c>" — "</c> separator disappear entirely rather than leave a dangling dash.
    ///
    /// <para>A truthy non-string is stringified rather than rejected, because Node does not throw
    /// on it: it interpolates the value into the description and puts it into the <c>notes</c>
    /// JSON. The raw JSON text matches JavaScript's <c>String(x)</c> for numbers and booleans, and
    /// diverges for an object or an array (JS gives "[object Object]" and a comma-joined list).
    /// That text only ever reaches a reminder row, never the HTTP response, and the row is
    /// currently discarded by the no-op port.</para>
    /// </summary>
    private static string? ResolveDosage(JsonNode? dosage)
        => RxLifecycleJs.IsTruthy(dosage)
            ? RxLifecycleJs.AsString(dosage) ?? dosage!.ToJsonString()
            : null;
}

// ── PUT /api/prescriptions/{id}/dispense ─────────────────────────────────────

/// <summary>
/// The one body field <c>PUT /api/prescriptions/{id}/dispense</c> reads (prescriptions.js:449).
/// Entirely optional — the React client sends NO BODY AT ALL.
///
/// <para><see cref="JsonElement"/> rather than a typed list because the value is never validated:
/// any non-array (an object, a string, a number, null, absent) is SILENTLY IGNORED and the
/// response comes back with <c>inventoryDeducted: []</c>, and the elements themselves carry
/// untyped <c>itemId</c> / <c>quantity</c> fields whose JavaScript coercions are observable. A
/// typed binding would answer <c>400 {"error":"Validation failed", …}</c> for inputs that must
/// reach the handler.</para>
/// </summary>
public sealed record RxLifecycleDispenseBody(JsonElement InventoryItems);

public sealed record RxLifecycleDispenseCommand(
    string PrescriptionId,
    string CallerUserId,
    RxLifecycleDispenseBody? Body) : IRequest<RxLifecycleDispensedResponse>;

/// <summary>
/// Port of <c>PUT /api/prescriptions/{id}/dispense</c> (prescriptions.js:446-512) → 200.
///
/// <para><b>The status write comes FIRST, before any inventory work, and must stay first.</b>
/// prescriptions.js:459-462 flips the row to DISPENSED with no wrapping transaction, which is what
/// makes the "500 on an already-dispensed prescription" outcome reachable — and that outcome is the
/// contract, filed by audit-1 as a contract error against the extraction that omitted the ordering.
/// </para>
///
/// <para>The deduction goes through <see cref="IInventoryDeductionWriter"/>, whose only
/// implementation today is a placeholder that decrements nothing, records no transaction history,
/// logs the item ids it did not touch, and returns an empty list. What it does NOT reproduce is
/// prescriptions.js:464-508: the per-item <c>findUnique</c>, the atomic decrement paired with an
/// <c>inventory_transactions</c> row inside a per-item <c>$transaction</c>, and the compensating
/// increment when the post-decrement stock goes negative — including that rollback's LEAK, where
/// the transaction row survives a reverted decrement.</para>
///
/// <para><b>Unlike <c>/send</c>, this result is LOAD-BEARING.</b> Node does not wrap the inventory
/// loop in a catch of its own, so the lines are part of the 200 body and a failure really is
/// <c>500 {"error":"Failed to dispense prescription"}</c>. A <c>null</c> from the port is answered
/// with that 500 and never substituted with <c>[]</c>; an empty list is a normal 200, which Node
/// genuinely produces on several paths and which is what the live client always sees, since it
/// sends no body.</para>
///
/// <para>No status precondition, no <c>deletedAt</c> filter, and <b>no tenant scoping on the item
/// ids</b> — Node accepts any inventory item id in the database, so a physician can decrement
/// another clinic's stock. Reproduced because the response contract depends on it; flagged rather
/// than silently fixed.</para>
/// </summary>
public sealed class RxLifecycleDispensePrescriptionHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IIdentityDirectory identity,
    IInventoryDeductionWriter inventory,
    IAppLogger<RxLifecycleDispensePrescriptionHandler> logger)
    : IRequestHandler<RxLifecycleDispenseCommand, RxLifecycleDispensedResponse>
{
    private const string LogMessage = "[Prescriptions] Dispense error";
    private const string FailureError = "Failed to dispense prescription";

    public Task<RxLifecycleDispensedResponse> Handle(
        RxLifecycleDispenseCommand request, CancellationToken cancellationToken = default)
        => RxLifecyclePersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxLifecycleDispensedResponse> HandleCore(
        RxLifecycleDispenseCommand request, CancellationToken cancellationToken)
    {
        var (prescription, _) = await RxLifecycleGuard.LoadOwnedAsync(
            request.PrescriptionId, request.CallerUserId, prescriptions, identity, cancellationToken);

        // status -> DISPENSED, and this is committed BEFORE the deduction. Do not reorder.
        prescription.Dispense();

        // `prisma.prescription.update({ data: { status: 'DISPENSED' } })` (prescriptions.js:459-462)
        // is UNCONDITIONAL — there is no status precondition on this route, so a row that is
        // ALREADY DISPENSED is still written and Prisma's `@updatedAt` still fires. `Dispense()`
        // assigns nothing new in that case, EF sees no modified property and would issue no UPDATE,
        // leaving `updatedAt` stale in the 200 body AND in the stored row. Force the write.
        prescriptions.MarkModified(prescription);

        await RxLifecyclePersistence.SaveAsync(
            dbContext, logger, LogMessage, FailureError, cancellationToken);

        var deducted = await DeductAsync(request, cancellationToken);

        return RxLifecycleMapper.ToDispensedResponse(
            prescription, "Prescription dispensed", deducted);
    }

    /// <summary>
    /// The inventory pass. Returns the <c>inventoryDeducted</c> lines — empty when there was
    /// nothing to do, and never null.
    /// </summary>
    private async Task<IReadOnlyList<RxLifecycleInventoryDeductedLine>> DeductAsync(
        RxLifecycleDispenseCommand request, CancellationToken cancellationToken)
    {
        var items = ReadItems(request.Body?.InventoryItems ?? default);

        // `if (Array.isArray(inventoryItems) && inventoryItems.length > 0)` — anything else leaves
        // `deducted` at its initial [], with no error and no hint that nothing moved.
        if (items.Count == 0) return [];

        var lines = await inventory.DeductForPrescriptionAsync(
            new InventoryDeductionRequest(
                PrescriptionId: request.PrescriptionId,
                // inventory_transactions.performed_by_id is the dispensing physician's USER id.
                PerformedByUserId: request.CallerUserId,
                Items: items),
            cancellationToken);

        // Null means the deduction could not be performed. Node has no catch around its loop, so
        // that is the route's own 500 — on a prescription that is already DISPENSED. Substituting
        // [] here would turn a failure into a success.
        if (lines is null) throw RxErrors.ServerError(FailureError);

        return lines
            .Select(line => new RxLifecycleInventoryDeductedLine(
                line.ItemId, line.Name, line.Quantity, line.NewStock))
            .ToList();
    }

    /// <summary>
    /// Applies the two filters Node applies before it touches inventory, because both are quirks of
    /// the dispense contract rather than of the Inventory module.
    ///
    /// <para>A non-array <c>inventoryItems</c> — an object, a string, a number, null, or an absent
    /// key — yields no items at all and is silently ignored, and so does an empty array.</para>
    ///
    /// <para>Per element: <c>const { itemId, quantity } = element</c> is a TypeError for a JSON
    /// <c>null</c>, which is NOT caught and becomes the route's 500 with the prescription already
    /// DISPENSED; a non-object element destructures to two <c>undefined</c>s and is skipped. Then
    /// <c>if (!itemId || !quantity) continue</c> is pure truthiness, so a quantity of <c>0</c>,
    /// <c>""</c>, <c>null</c> or <c>false</c> drops the item with no error and no response entry.
    /// A truthy non-string <c>itemId</c> reaches <c>where: { id: itemId }</c> as the wrong type for
    /// a String column and is likewise the route's 500.</para>
    ///
    /// <para><c>quantity</c> goes through JavaScript's <c>parseInt</c>, which is lossy on purpose:
    /// <c>"5 boxes"</c> is 5 and <c>1.9</c> is 1. A truthy but unparseable quantity such as
    /// <c>"abc"</c> is NaN, carried to the port as a null <c>Quantity</c>; in Node that NaN reaches
    /// Prisma's <c>decrement</c> and throws, so a real implementation must stop there and return
    /// null after the preceding items have already been applied. A NEGATIVE quantity is legal and
    /// INCREASES stock.</para>
    /// </summary>
    private static List<InventoryDeductionItem> ReadItems(JsonElement inventoryItems)
    {
        if (inventoryItems.ValueKind != JsonValueKind.Array) return [];

        var items = new List<InventoryDeductionItem>(inventoryItems.GetArrayLength());

        foreach (var element in inventoryItems.EnumerateArray())
        {
            // Destructuring null throws, and nothing catches it.
            if (element.ValueKind == JsonValueKind.Null)
                throw RxErrors.ServerError(FailureError);

            // A non-object element destructures to two undefineds and is skipped below.
            var itemId = ReadProperty(element, "itemId");
            var quantity = ReadProperty(element, "quantity");

            if (!RxJs.IsTruthy(itemId) || !RxJs.IsTruthy(quantity)) continue;

            // Wrong type for the String primary key.
            if (itemId.ValueKind != JsonValueKind.String)
                throw RxErrors.ServerError(FailureError);

            items.Add(new InventoryDeductionItem(itemId.GetString()!, RxJs.ParseInt(quantity)));
        }

        return items;
    }

    /// <summary>
    /// One field of an <c>inventoryItems</c> element, or <see cref="JsonValueKind.Undefined"/> when
    /// the element is not an object or does not carry the key — which is what JavaScript's
    /// destructuring yields for both cases, and what the truthiness skip then drops.
    /// </summary>
    private static JsonElement ReadProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;

        return element.TryGetProperty(name, out var value) ? value : default;
    }
}
