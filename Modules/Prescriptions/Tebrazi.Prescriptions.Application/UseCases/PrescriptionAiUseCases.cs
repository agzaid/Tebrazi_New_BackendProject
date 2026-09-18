using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
//  The three AI endpoints on the prescriptions router:
//
//      POST /api/prescriptions/check-interactions  (prescriptions.js:735-868)
//      POST /api/prescriptions/extract-from-plan   (prescriptions.js:1078-1143)
//      POST /api/prescriptions/transcribe-rx       (prescriptions.js:972-1069)
//
//  Facts that hold for all three and are therefore stated once:
//
//  * `aiUsageCheck` (server/src/services/aiUsageLimiter.js) is NOT PORTED. Only transcribe-rx
//    sits behind it in Node, so its 403 "Feature not available on your plan", its 429
//    "Monthly AI limit reached" and its two 401 bodies ("Authentication required" / "User not
//    found", which differ from authCheck's) DO NOT EXIST here. The observable consequence is
//    named on that handler: in Node a PATIENT gets the plan 403, because the limiter runs before
//    the physician check; here they get the physician 403. Do not invent the gate, and do not
//    call IIdentityDirectory.GetSubscriptionTierAsync to fake it.
//
//  * Every error body in prescriptions.js is a bare `{ "error": "..." }`, so all three use
//    RxErrors and none of them may reach for ValidationException or ConflictException.
//
//  * The per-route named 500 comes from RxAiPersistence, one guard per handler:
//    "Failed to check interactions", "Failed to extract medications", and — uniquely in the whole
//    file — the RAW exception message for transcribe-rx.
//
//  * These are WRITE-SHAPED per the module's house rule (docs/prescriptions-surface.md §6):
//    check-interactions inserts an InteractionAlert row, and all three need `userType` or the
//    caller id for the model metadata, so the caller's identity arrives EXPLICITLY on the command
//    and none of them injects ICurrentUser.
//
//  * The global XSS sanitizer (server/src/index.js:57-81) is not ported, so the drug names and
//    `planText` these handlers send to the model are the RAW client strings where Node sends
//    HTML-stripped ones. transcribe-rx is unaffected — the sanitizer runs before multer populates
//    req.body there, so Node does not sanitize its `language` either.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// JavaScript value semantics over <see cref="JsonNode"/> that these three handlers depend on.
///
/// <para><see cref="RxJs"/> (the module's shared surface) covers <see cref="JsonElement"/>, which
/// is what a request body binds to. It is not enough here: the stored <c>medications</c> column
/// and the model's own output are handled as <see cref="JsonNode"/> so that mixed-type arrays can
/// be echoed back verbatim, and JsonNode needs its own truthiness and <c>String(x)</c>. Named with
/// the group's <c>RxAi</c> prefix because four handler files compile into this one namespace.</para>
/// </summary>
internal static class RxAiValues
{
    /// <summary>
    /// Serializer options for the two opaque JSON columns this group writes
    /// (<c>interaction_alerts.drugs</c> and <c>.interactions</c>). The relaxed encoder matches the
    /// host's own (Program.cs:56), so a drug name in Arabic is stored as UTF-8 rather than as
    /// <c>\uXXXX</c> escapes and <c>GET /interaction-history</c> echoes back the same bytes.
    /// </summary>
    private static readonly JsonSerializerOptions StorageOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// JS truthiness: <c>false</c>, <c>0</c>, <c>""</c>, <c>null</c> and an absent key are falsy,
    /// and <b>every object and array is truthy, including <c>{}</c> and <c>[]</c></b>.
    /// </summary>
    public static bool IsTruthy(JsonNode? value)
    {
        if (value is null) return false;

        return value.GetValueKind() switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => value.AsValue().TryGetValue<double>(out var number) && number != 0,
            JsonValueKind.String => Stringify(value).Length > 0,
            _ => true
        };
    }

    /// <summary>
    /// <c>String(value)</c>. Booleans are lowercase, an array becomes its comma-joined elements
    /// and any other object becomes the literal <c>"[object Object]"</c> — which is exactly what
    /// reaches the drug-interaction prompt when a stored medication element has no
    /// <c>drugName</c>, because <c>drugs.join(', ')</c> stringifies it (aiGateway.js:672).
    /// </summary>
    public static string Stringify(JsonNode? value)
    {
        if (value is null) return string.Empty;

        switch (value.GetValueKind())
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return string.Empty;
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.String:
                return value.AsValue().TryGetValue<string>(out var text) ? text ?? string.Empty : string.Empty;
            case JsonValueKind.Number:
                return value.AsValue().TryGetValue<double>(out var number)
                    ? number.ToString("R", CultureInfo.InvariantCulture)
                    : value.ToJsonString();
            case JsonValueKind.Array:
                return string.Join(",", value.AsArray().Select(Stringify));
            default:
                return "[object Object]";
        }
    }

    /// <summary><c>String(value || '')</c> — a falsy value becomes the empty string, not "null" or "0".</summary>
    public static string StringifyTruthy(JsonNode? value)
        => IsTruthy(value) ? Stringify(value) : string.Empty;

    /// <summary>The node's own string, or null when it is not a JSON string.</summary>
    public static string? AsString(JsonNode? value)
        => value is not null
           && value.GetValueKind() == JsonValueKind.String
           && value.AsValue().TryGetValue<string>(out var text)
            ? text
            : null;

    /// <summary>
    /// The identity <c>new Set</c> would use for this value, or <b>null</b> when the value can
    /// never collide — which is every object and array, because <c>Set</c> compares those by
    /// REFERENCE and two separately-parsed objects are never the same reference.
    ///
    /// <para>That asymmetry is observable: it is why the fewer-than-two-drugs short-circuit at
    /// prescriptions.js:804 can be defeated by two identical-looking medication objects, and why
    /// the dedupe of drug NAMES is exact and case-SENSITIVE, so <c>"Aspirin"</c> and
    /// <c>"aspirin"</c> both survive into <c>drugsChecked</c>.</para>
    /// </summary>
    public static string? SetKey(JsonNode? value)
        => value?.GetValueKind() switch
        {
            JsonValueKind.String => "s:" + Stringify(value),
            JsonValueKind.Number => "n:" + Stringify(value),
            JsonValueKind.True => "b:true",
            JsonValueKind.False => "b:false",
            _ => null
        };

    /// <summary>An opaque JSON column as a node, yielding null for null, blank or invalid JSON.</summary>
    public static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An opaque JSON value as the text to store in a Json column.</summary>
    public static string ToStorageJson(JsonNode node) => node.ToJsonString(StorageOptions);

    /// <summary>
    /// The medication normalization both AI extractors share, verbatim: filter on
    /// <c>m &amp;&amp; m.drugName</c>, then <c>String(m.x || '').trim()</c> into exactly five keys
    /// in a fixed order (prescriptions.js:1041-1048 and :1124-1133).
    ///
    /// <para>Only a JSON OBJECT can survive the filter. A string, number or array element is
    /// truthy but its <c>.drugName</c> is <c>undefined</c>, so it is dropped — which means the
    /// returned array can be SHORTER than the model's and its indices do not correspond.</para>
    /// </summary>
    public static IReadOnlyList<RxMedicationLine> ToMedicationLines(JsonArray array)
    {
        var lines = new List<RxMedicationLine>(array.Count);

        foreach (var element in array)
        {
            if (element is not JsonObject item) continue;
            if (!IsTruthy(item["drugName"])) continue;

            lines.Add(new RxMedicationLine(
                StringifyTruthy(item["drugName"]).Trim(),
                StringifyTruthy(item["dosage"]).Trim(),
                StringifyTruthy(item["frequency"]).Trim(),
                StringifyTruthy(item["duration"]).Trim(),
                StringifyTruthy(item["instructions"]).Trim()));
        }

        return lines;
    }
}

/// <summary>
/// The route-level catch-all, one call per handler, mirroring
/// <c>StatusPersistence.RunAsync</c> in Appointments. Node wraps each of these three handlers END
/// TO END, so a failure in the PRE-CALL work — the physician lookup, the visit lookup, the drug
/// sweep — answers the same named 500 as a failure in the model call; without this the kernel's
/// generic <c>{"error":"Internal Server Error", "message":…}</c> would reach a client that
/// branches on <c>err.response.data.error</c>.
///
/// <para>An <see cref="AppException"/> passes through UNTOUCHED, so the deliberate 400s and 403s
/// keep their exact bodies, and Node's inner best-effort <c>catch</c> blocks stay nested inside
/// this one exactly as Node nests them.</para>
///
/// <para>Declared internal to this file with the group's prefix: the log message and the failure
/// literal differ per route, and three sibling handler files are being written into this same
/// namespace.</para>
/// </summary>
internal static class RxAiPersistence
{
    /// <summary>
    /// The fixed-literal form, used by check-interactions and extract-from-plan. Deliberately a
    /// DIFFERENT NAME from <c>RunWithDynamicErrorAsync</c> rather than an overload: an
    /// overload set whose only difference is <c>string</c> versus
    /// <c>Func&lt;Exception, string&gt;</c> resolves on a lambda conversion, which is exactly the
    /// kind of subtlety a parallel-authored file cannot afford.
    /// </summary>
    /// <param name="logger">The calling handler's logger; <c>THandler</c> is inferred from it.</param>
    /// <param name="logMessage">The message on the Node route's own <c>logger.error</c> line.</param>
    /// <param name="failureError">The exact 500 literal from the Node route's catch block.</param>
    /// <param name="cancellationToken">Checked so a client disconnect is not reported as a route failure.</param>
    /// <param name="body">The handler's <c>HandleCore</c>.</param>
    public static Task<TResponse> RunAsync<THandler, TResponse>(
        IAppLogger<THandler> logger,
        string logMessage,
        string failureError,
        CancellationToken cancellationToken,
        Func<Task<TResponse>> body)
        => RunWithDynamicErrorAsync(logger, logMessage, _ => failureError, cancellationToken, body);

    /// <summary>
    /// The computed form. Only <c>transcribe-rx</c> needs it: prescriptions.js:1067 is
    /// <c>error.message || 'Failed to transcribe prescription'</c>, the file's ONLY dynamic 500.
    /// </summary>
    /// <param name="logger">The calling handler's logger; <c>THandler</c> is inferred from it.</param>
    /// <param name="logMessage">The message on the Node route's own <c>logger.error</c> line.</param>
    /// <param name="failureError">Maps the caught exception to the 500's <c>error</c> value.</param>
    /// <param name="cancellationToken">Checked so a client disconnect is not reported as a route failure.</param>
    /// <param name="body">The handler's <c>HandleCore</c>.</param>
    public static async Task<TResponse> RunWithDynamicErrorAsync<THandler, TResponse>(
        IAppLogger<THandler> logger,
        string logMessage,
        Func<Exception, string> failureError,
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
            throw RxErrors.ServerError(failureError(exception));
        }
    }
}

// ── POST /api/prescriptions/check-interactions ────────────────────────────────

/// <summary>
/// Checks the caller's drug list, plus every active drug the backend can discover for the patient,
/// for interactions.
/// </summary>
/// <param name="CallerUserId">
/// The authenticated user. Used for the model metadata and to decide WHICH column of the
/// <c>InteractionAlert</c> row is filled — never to authorize anything, because this route has no
/// authorization at all.
/// </param>
/// <param name="CallerUserType">
/// The caller's <c>userType</c> claim, RAW. Compared as <c>== "PHYSICIAN"</c> and never parsed
/// into an enum, because the claim can carry a value the enum does not have. It is load-bearing:
/// with no <c>visitId</c>, auto-discovery runs only for a NON-physician, so the same body returns
/// a different <c>drugsChecked</c> for a physician than for a patient.
/// </param>
/// <param name="Medications">
/// The body's <c>medications</c>, RAW. The client sends bare drug-name strings
/// (PrescriptionPanel.jsx:158, DrugScreeningsPage.jsx:82, PatientProfilePage.jsx:160) but nothing
/// validates that, and <c>PrescriptionsPage.jsx:266</c> sends an entirely empty body — so a
/// <c>default</c> value (JsonValueKind.Undefined) must bind cleanly and mean "absent".
/// </param>
/// <param name="SubprofileId">
/// The body's <c>subprofileId</c>, RAW. Used <b>unvalidated and unauthorized</b>; only its
/// truthiness and its being a string matter.
/// </param>
/// <param name="VisitId">The body's <c>visitId</c>, RAW. Same — unvalidated and unauthorized.</param>
public sealed record RxAiCheckInteractionsCommand(
    string CallerUserId,
    string? CallerUserType,
    JsonElement Medications,
    JsonElement SubprofileId,
    JsonElement VisitId) : IRequest<RxAiCheckInteractionsResponse>;

/// <summary>
/// Port of <c>POST /api/prescriptions/check-interactions</c> (prescriptions.js:735-868).
///
/// <para><b>THIS ROUTE HAS NO VALIDATION AND NO AUTHORIZATION — NOT ONE 400 AND NOT ONE 403.</b>
/// <c>visitId</c> and <c>subprofileId</c> are read straight out of the body and used to pull
/// another patient's data, so any authenticated caller can pass someone else's <c>visitId</c> and
/// receive that patient's full active drug list back in <c>drugsChecked</c>. It is an IDOR, the
/// response contract depends on it, and a faithful port reproduces it. <b>It is flagged to the
/// team rather than silently fixed here</b> (docs/prescriptions-surface.md §11).</para>
///
/// <para>DISCOVERY ORDER IS OBSERVABLE, because it is the order of <c>drugsChecked</c>: the
/// caller's own <c>medications</c>, then the named subprofile's reported medications, then
/// cross-prescription medications (prescription order, then array order), then every family
/// subprofile's reported medications. The dedupe is <c>new Set</c> — exact-string and
/// case-SENSITIVE — so a <see cref="HashSet{T}"/> alone would lose the order and change the JSON.
/// </para>
///
/// <para>NO CACHING to reproduce: unlike <c>GET /</c> and <c>GET /summary</c>, this route is not
/// wrapped in <c>cacheMiddleware</c>, and the reasoning chat sets <c>useCache: false</c>, so every
/// call re-runs the whole sweep.</para>
/// </summary>
public sealed class RxAiCheckInteractionsHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IInteractionAlertStore alerts,
    IIdentityDirectory identity,
    IVisitDirectory visits,
    IPatientDirectory patients,
    IAiGateway ai,
    IAppLogger<RxAiCheckInteractionsHandler> logger)
    : IRequestHandler<RxAiCheckInteractionsCommand, RxAiCheckInteractionsResponse>
{
    /// <summary>
    /// The cross-prescription sweep's statuses (prescriptions.js:771). <b>SIGNED is included
    /// here</b>, unlike the patient list filter on <c>GET /</c>, so a never-sent prescription
    /// still contributes its drugs.
    /// </summary>
    private static readonly PrescriptionStatus[] ActiveStatuses =
    [
        PrescriptionStatus.SIGNED,
        PrescriptionStatus.CONFIRMED,
        PrescriptionStatus.SENT,
        PrescriptionStatus.DISPENSED
    ];

    private const string PhysicianUserType = "PHYSICIAN";

    /// <summary>U+2014, with a single space on each side. A hyphen here is a byte-level divergence.</summary>
    private const string OneDrugMessage = "Only 1 active medication found — no interactions to check";

    private const string NoDrugsMessage = "No active medications found";

    /// <summary>The checker's own zero-interaction message, and the endpoint's COMMON case.</summary>
    private const string NoInteractionsMessage = "No known interactions detected";

    public Task<RxAiCheckInteractionsResponse> Handle(
        RxAiCheckInteractionsCommand request, CancellationToken cancellationToken = default)
        => RxAiPersistence.RunAsync(
            logger, "[Prescriptions] Interaction check error", "Failed to check interactions",
            cancellationToken, () => HandleCore(request, cancellationToken));

    private async Task<RxAiCheckInteractionsResponse> HandleCore(
        RxAiCheckInteractionsCommand request, CancellationToken ct)
    {
        // Both id fields are read up front. Node reads them lazily and so hits its Prisma type
        // error on `visitId` only after source 1's query has run; with no observable side effect
        // between the two, the earlier throw produces the identical 500.
        var subprofileId = ReadIdField(request.SubprofileId, "subprofileId");
        var visitId = ReadIdField(request.VisitId, "visitId");

        // Resolved ONCE and reused, as Node does: the visit is queried a single time
        // (prescriptions.js:753) and `patientUserId` feeds both the drug sweep and the decision to
        // look for clinical context at all. Hoisting it above the sweep swaps the order of two
        // READS relative to Node — the visit now precedes the subprofile's medications — which is
        // unobservable: both are reads, and a failure in either lands on the same route 500.
        var patientUserId = await ResolvePatientUserIdAsync(request, visitId, ct);

        var allDrugs = await BuildDrugListAsync(request, subprofileId, patientUserId, ct);

        // The fewer-than-two short-circuit (prescriptions.js:804-806). It never calls the checker,
        // never writes an InteractionAlert row, and shadows the gateway's own
        // "Need at least 2 drugs to check", which is therefore unreachable from this route.
        if (allDrugs.Count < 2)
        {
            return new RxAiCheckInteractionsResponse(
                Interactions: new JsonArray(),
                DrugsChecked: allDrugs,
                Message: allDrugs.Count == 0 ? NoDrugsMessage : OneDrugMessage);
        }

        var (allergies, conditions) =
            await TryReadClinicalContextAsync(subprofileId, patientUserId, ct);

        var check = await CheckAsync(allDrugs, allergies, conditions, request.CallerUserId, ct);

        // The M3.3 history row. Its own try/catch in Node (prescriptions.js:846-857) covers the
        // PHYSICIAN LOOKUP as well as the insert, so a database failure anywhere in here still
        // answers 200 with the interactions the caller asked for.
        await TryLogAlertAsync(request.CallerUserId, allDrugs, check.Interactions, ct);

        return new RxAiCheckInteractionsResponse(
            Interactions: check.Interactions,
            // The handler re-emits its OWN allDrugs rather than the checker's echo of it
            // (prescriptions.js:856). Same content in Node, but only because it is the same array.
            DrugsChecked: allDrugs,
            Message: check.Message);
    }

    // ── The four discovery sources ───────────────────────────────────────────

    /// <summary>
    /// <c>[...new Set([...inputMeds, ...existingDrugs].filter(Boolean))]</c>, assembled in the
    /// order Node assembles it because that order is the response's.
    /// </summary>
    private async Task<JsonArray> BuildDrugListAsync(
        RxAiCheckInteractionsCommand request,
        string? subprofileId,
        string? patientUserId,
        CancellationToken ct)
    {
        var candidates = new List<JsonNode>();

        // Source 0 — the caller's own list. `Array.isArray` first: `medications: null`, a string
        // or an object all mean "none", with no error.
        if (request.Medications.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in request.Medications.EnumerateArray())
            {
                // `.filter(Boolean)`, so "" / 0 / false / null are dropped here already.
                if (RxJs.IsTruthy(element)) candidates.Add(JsonNode.Parse(element.GetRawText())!);
            }
        }

        // Source 1 — the named subprofile's own reported medications (prescriptions.js:742-745).
        // `isActive: true` and NO `deletedAt` predicate, so a soft-deleted-but-active row IS
        // included; IPatientDirectory.ListActiveMedicationNamesAsync ignores the Patients
        // soft-delete query filter for exactly that reason.
        if (subprofileId is not null)
        {
            foreach (var name in await patients.ListActiveMedicationNamesAsync([subprofileId], ct))
                candidates.Add(JsonValue.Create(name)!);
        }

        if (patientUserId is not null)
        {
            // Source 2 — cross-prescription awareness: every active prescription for this patient,
            // from ANY physician. `prescriptions` has no patient column, so the visit ids are
            // resolved first; an empty result is a filter that matches NOTHING, not "no filter".
            // `where: { visit: { patientUserId } }` (prescriptions.js:770) scopes on the patient
            // only, never on the subprofile, so `subprofileId` stays null even when the request
            // body supplied one.
            var visitIds = await visits.ListVisitIdsForPatientAsync(patientUserId, subprofileId: null, ct);
            var rows = await prescriptions.ListForInteractionCheckAsync(visitIds, ActiveStatuses, ct);

            foreach (var row in rows)
            {
                // `if (Array.isArray(rx.medications))` — a scalar or object column is skipped.
                if (RxAiValues.Parse(row.Medications) is not JsonArray stored) continue;

                foreach (var element in stored)
                {
                    // A JSON `null` ELEMENT is not a skip — it is a 500. `m.stoppedAt` on `null` is
                    // `TypeError: Cannot read properties of null` (prescriptions.js:779), which
                    // escapes the sweep, reaches the route's outer catch at :866 and answers
                    // 500 {"error":"Failed to check interactions"}. Such a column value really is
                    // reachable: `PUT /api/prescriptions/{id}` stores `req.body.medications`
                    // verbatim with no per-element validation (prescriptions.js:261). Note the
                    // asymmetry with source 0 above, where `.filter(Boolean)` runs FIRST and drops
                    // a null before anything reads a property off it.
                    if (element is null || element.GetValueKind() == JsonValueKind.Null)
                    {
                        throw new InvalidOperationException(
                            "A stored medications array holds a JSON null; Node reads `.stoppedAt` "
                            + "off it and throws a TypeError, answering the route's named 500.");
                    }

                    // `.filter(m => !m.stoppedAt)`. A bare STRING element has no `stoppedAt`, so
                    // it always survives — the array legally holds either shape.
                    if (element is JsonObject entry && RxAiValues.IsTruthy(entry["stoppedAt"])) continue;

                    // `.map(m => m.drugName || m)`: an object with no truthy drugName contributes
                    // THE WHOLE OBJECT, which is echoed verbatim in drugsChecked and stringifies
                    // as "[object Object]" in the prompt. Then `.filter(Boolean)` again.
                    var drug = element is JsonObject named && RxAiValues.IsTruthy(named["drugName"])
                        ? named["drugName"]
                        : element;

                    if (RxAiValues.IsTruthy(drug)) candidates.Add(drug!.DeepClone());
                }
            }

            // Source 3 — the family sweep, which runs ONLY when no subprofileId was given: passing
            // one narrows discovery to that dependant. Node walks the dependants in an N+1 loop
            // (prescriptions.js:786-797) and this reproduces the loop rather than batching it,
            // because the per-dependant grouping is part of drugsChecked's order. Same
            // `isActive`-only filter as source 1 (:792-795) — soft-deleted rows included.
            if (subprofileId is null)
            {
                foreach (var dependantId in await patients.ListSubprofileIdsByUserAsync(patientUserId, ct))
                {
                    foreach (var name in await patients.ListActiveMedicationNamesAsync([dependantId], ct))
                        candidates.Add(JsonValue.Create(name)!);
                }
            }
        }

        var allDrugs = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (!RxAiValues.IsTruthy(candidate)) continue;

            // A null key means "cannot collide" — every object and array survives, because Set
            // compares those by reference.
            var key = RxAiValues.SetKey(candidate);
            if (key is not null && !seen.Add(key)) continue;

            allDrugs.Add(candidate.DeepClone());
        }

        return allDrugs;
    }

    /// <summary>
    /// <c>visitId ? visit?.patientUserId : (userType !== 'PHYSICIAN' ? req.user.id : null)</c>
    /// (prescriptions.js:751-765).
    ///
    /// <para>Two quirks live in that one expression. A truthy <c>visitId</c> that resolves to
    /// nothing yields null and there is <b>no fallback</b> to the caller — auto-discovery is simply
    /// off. And a PHYSICIAN calling with no <c>visitId</c> also gets null, so they see only the
    /// drugs they typed, while a patient sending the identical body gets the full sweep.</para>
    /// </summary>
    private async Task<string?> ResolvePatientUserIdAsync(
        RxAiCheckInteractionsCommand request, string? visitId, CancellationToken ct)
    {
        if (visitId is not null)
            return (await visits.GetAsync(visitId, ct))?.PatientUserId;

        return string.Equals(request.CallerUserType, PhysicianUserType, StringComparison.Ordinal)
            ? null
            : request.CallerUserId;
    }

    /// <summary>
    /// The allergy and chronic-condition context for the prompt — which in Node is
    /// <b>ALWAYS EMPTY, on every request, unconditionally</b>. This method therefore returns two
    /// empty lists and reads nothing.
    ///
    /// <para><b>Why.</b> prescriptions.js:823-826 runs both halves of a <c>Promise.all</c> against
    /// fields that do not exist:</para>
    ///
    /// <list type="bullet">
    /// <item><c>prisma.allergy.findMany({ where: { subprofileId, isActive: true }, … })</c> —
    /// <c>model Allergy</c> (schema.prisma:441-458) has NO <c>isActive</c> column, only id,
    /// patientProfileId, subprofileId, allergen, severity, reaction, createdAt, updatedAt and
    /// deletedAt. Prisma raises <c>PrismaClientValidationError: Unknown argument 'isActive'</c>
    /// before any SQL is sent. (The same file gets it right at ai.js:70, which passes no
    /// <c>isActive</c> at all.)</item>
    /// <item><c>prisma.chronicCondition.findMany({ …, select: { name: true } })</c> —
    /// <c>model ChronicCondition</c> (schema.prisma:460-478) has <c>condition</c>, not
    /// <c>name</c>, so <c>select</c> raises <c>Unknown field 'name'</c>.</item>
    /// </list>
    ///
    /// <para><c>src/lib/prisma.js</c> is a plain <c>new PrismaClient()</c> with no extensions, so
    /// nothing patches those fields in. <c>Promise.all</c> rejects on the first validation error and
    /// the BARE <c>catch { /* continue without context */ }</c> at prescriptions.js:830 swallows it,
    /// leaving <c>patientAllergies</c> and <c>patientConditions</c> at their <c>[]</c> initialisers
    /// from :813-814.</para>
    ///
    /// <para><b>Three wire consequences, all of which this port must reproduce.</b>
    /// (1) <c>aiGateway.js</c>'s stage-3 allergy scan iterates <c>patientAllergies</c>
    /// (aiGateway.js:715), so Node can NEVER emit an <c>ALLERGY: …</c> element or the
    /// <c>"{n} interaction(s) found"</c> message from this route — supplying real allergies here
    /// would have the client render a fabricated HIGH-severity contraindication.
    /// (2) <c>drug.toLowerCase()</c> at aiGateway.js:716 is unreachable, so the TypeError-500 that
    /// audit-1 error #3 describes cannot happen in Node; the pre-flight guard in
    /// <c>CheckAsync</c> is correspondingly dead.
    /// (3) The prompt's allergy and condition blocks (aiGateway.js:634-640) and its
    /// <c>hasAllergies</c> flag (:626, :628) are always the empty-context variants, so a real
    /// gateway would be asked a different question if this method returned data.</para>
    ///
    /// <para>The subprofile resolution Node performs first (prescriptions.js:815-820 — the
    /// <c>subprofileId || first dependant, unordered</c> lookup) is deliberately NOT performed:
    /// it succeeds in Node but its result is only ever fed to the two doomed queries, so skipping
    /// it is wire-equivalent and saves a round trip. The parameters are retained so the call site
    /// still documents what Node consults.</para>
    ///
    /// <para><b>Do not "fix" the Node query, and do not add <c>isActive</c> to the Patients
    /// module's allergy read</b> — that column exists in neither backend.</para>
    /// </summary>
    /// <param name="subprofileId">The body's <c>subprofileId</c>; consulted by Node, unused here.</param>
    /// <param name="patientUserId">The resolved patient; consulted by Node, unused here.</param>
    /// <param name="ct">Cancellation token.</param>
    private static Task<(IReadOnlyList<string> Allergies, IReadOnlyList<string> Conditions)>
        TryReadClinicalContextAsync(string? subprofileId, string? patientUserId, CancellationToken ct)
    {
        _ = subprofileId;
        _ = patientUserId;
        ct.ThrowIfCancellationRequested();

        return Task.FromResult<(IReadOnlyList<string>, IReadOnlyList<string>)>(([], []));
    }

    // ── The checker call, and its degraded answer ────────────────────────────

    /// <summary>
    /// <c>aiGateway.checkDrugs</c> (prescriptions.js:833-838), whose <c>interactions</c> AND
    /// <c>message</c> are the endpoint's own — the handler computes neither.
    ///
    /// <para><b>Node's checkDrugs EFFECTIVELY NEVER THROWS</b> (aiGateway.js:587-762): the FDA
    /// lookups catch per pair, the reasoning chat has two identical degrade blocks, and stage 3 is
    /// pure string matching. That is why this route has no AI-failure branch in Node and why its
    /// only 500 is a database failure. The .NET port's <see cref="IAiGateway"/> DOES throw — the
    /// registered <c>UnconfiguredAiGateway</c> throws from every call — so letting that reach the
    /// file guard would answer 500 where Node answers 200 with a real body. This reproduces the
    /// gateway's terminal state instead: no FDA data and no model, which in Node leaves
    /// <c>enrichedInteractions</c> empty and returns just the stage-3 allergy warnings. An
    /// unreadable <c>Raw</c> is treated the same way, being a gateway failure by another name.</para>
    /// </summary>
    private async Task<(JsonArray Interactions, string Message)> CheckAsync(
        JsonArray allDrugs,
        IReadOnlyList<string> allergies,
        IReadOnlyList<string> conditions,
        string callerUserId,
        CancellationToken ct)
    {
        // AiDrugCheckRequest.Drugs is IReadOnlyList<string>, but allDrugs may hold objects. Node's
        // stage 3 calls `drug.toLowerCase()` on every element whenever the patient has ANY
        // allergy, so a non-string there is a TypeError that escapes checkDrugs, reaches the
        // route's outer catch and answers 500 {"error":"Failed to check interactions"}. Raised
        // BEFORE the call rather than after: the wire result is identical, only the wasted FDA and
        // model spend differs.
        //
        // UNREACHABLE, and that is correct. `allergies` is ALWAYS empty on this route because
        // Node's allergy query is invalid against the schema and its bare catch swallows the
        // rejection — see TryReadClinicalContextAsync. So `drug.toLowerCase()` at aiGateway.js:716
        // is never evaluated in Node either, which means audit-1 error #3's TypeError-500 does not
        // exist on this route and a non-string element answers 200. Kept, guarded on the same
        // condition Node guards it on, so that the day the Node query is repaired both backends
        // start behaving the same way rather than this port silently diverging.
        if (allergies.Count > 0 && allDrugs.Any(drug => drug?.GetValueKind() != JsonValueKind.String))
        {
            throw new InvalidOperationException(
                "A non-string entry in drugsChecked reaches drug.toLowerCase() in the allergy scan; "
                + "Node answers 500 for this input.");
        }

        try
        {
            var result = await ai.CheckDrugsAsync(
                new AiDrugCheckRequest(
                    Drugs: allDrugs.Select(RxAiValues.Stringify).ToList(),
                    PatientAllergies: allergies,
                    PatientConditions: conditions,
                    Agent: "drug_check",
                    UserId: callerUserId),
                ct);

            if (RxAiValues.Parse(result.Raw) is not JsonObject raw
                || raw["interactions"] is not JsonArray interactions
                || RxAiValues.AsString(raw["message"]) is not { } message)
            {
                throw new InvalidOperationException(
                    "IAiGateway.CheckDrugsAsync returned a payload without the checkDrugs contract's "
                    + "`interactions` array and `message` string.");
            }

            return ((JsonArray)interactions.DeepClone(), message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Warning(
                "[Rx] Drug checker unavailable; answering with the string-match allergy warnings only",
                new { Error = exception.Message, Drugs = allDrugs.Count });

            var warnings = BuildAllergyWarnings(allDrugs, allergies);

            return (warnings, warnings.Count > 0
                ? $"{warnings.Count} interaction(s) found"
                : NoInteractionsMessage);
        }
    }

    /// <summary>
    /// Stage 3 of <c>checkDrugs</c> (aiGateway.js:712-733) — the deterministic allergy scan, which
    /// is the half of this feature that would work with no provider at all.
    ///
    /// <para><b>It returns an empty array on this route, always.</b> Its only caller passes the
    /// <c>allergies</c> list from <see cref="TryReadClinicalContextAsync"/>, which is empty by
    /// contract because Node's allergy query cannot succeed — so Node can never emit an
    /// <c>ALLERGY: …</c> element or the <c>"{n} interaction(s) found"</c> message from
    /// <c>check-interactions</c> either, and emitting one here would show the client a fabricated
    /// HIGH-severity contraindication. Retained, correct, and exercised the moment the Node query
    /// is repaired; the description below documents the behaviour it would then have.</para>
    ///
    /// <para>The match is BIDIRECTIONAL substring and case-insensitive, so a patient allergic to
    /// "Pen" flags every drug containing "pen", and an empty allergen flags EVERY drug. Two
    /// allergies that both match one drug produce two warnings. Keys are emitted in the object
    /// literal's order, and <c>severity</c>, <c>recommendation</c> and the <c>ALLERGY: </c> prefix
    /// are fixed strings.</para>
    /// </summary>
    private static JsonArray BuildAllergyWarnings(JsonArray allDrugs, IReadOnlyList<string> allergies)
    {
        var warnings = new JsonArray();

        foreach (var node in allDrugs)
        {
            // Guaranteed a string by the guard in CheckAsync whenever allergies is non-empty.
            var drug = RxAiValues.Stringify(node);
            var lowerDrug = drug.ToLowerInvariant();

            foreach (var allergy in allergies)
            {
                var lowerAllergy = allergy.ToLowerInvariant();

                if (!lowerDrug.Contains(lowerAllergy, StringComparison.Ordinal)
                    && !lowerAllergy.Contains(lowerDrug, StringComparison.Ordinal))
                {
                    continue;
                }

                // Node skips a pair the model already flagged; with no model there is nothing to
                // have flagged it, so every match is emitted.
                warnings.Add(new JsonObject
                {
                    ["drug1"] = drug,
                    ["drug2"] = $"ALLERGY: {allergy}",
                    ["severity"] = "HIGH",
                    ["description"] =
                        $"Patient has a documented allergy to {allergy}. {drug} may cause an allergic reaction.",
                    ["recommendation"] = "DO NOT prescribe. Consider alternatives.",
                    ["alternatives"] = new JsonArray()
                });
            }
        }

        return warnings;
    }

    // ── The history row ──────────────────────────────────────────────────────

    /// <summary>
    /// The <c>InteractionAlert</c> insert that <c>GET /interaction-history</c> reads back
    /// (prescriptions.js:846-857). Best-effort by construction: Node wraps the physician lookup
    /// AND the insert in one try/catch that only logs, so nothing in here may fail the request.
    ///
    /// <para><c>physicianId</c> and <c>patientUserId</c> are MUTUALLY EXCLUSIVE — a physician's
    /// check never records the patient it was about — and <c>visitId</c> is <b>never written</b>
    /// even though the column exists and <c>req.body.visitId</c> is in hand, which is why that
    /// column is always null in practice. Both are preserved rather than fixed: changing them
    /// alters stored data, not JSON, so it belongs in a separate flagged change.</para>
    /// </summary>
    private async Task TryLogAlertAsync(
        string callerUserId, JsonArray allDrugs, JsonArray interactions, CancellationToken ct)
    {
        try
        {
            var physician = await identity.GetPhysicianByUserIdAsync(callerUserId, ct);

            alerts.Add(InteractionAlert.Create(
                drugs: RxAiValues.ToStorageJson(allDrugs),
                interactions: RxAiValues.ToStorageJson(interactions),
                alertCount: interactions.Count,
                physicianId: physician?.Id,
                patientUserId: physician is null ? callerUserId : null));

            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error("[Rx] Interaction log error", exception);
        }
    }

    /// <summary>
    /// <c>subprofileId</c> / <c>visitId</c>: truthiness decides whether the field is used at all,
    /// and a truthy NON-string is a 500 rather than a 400 — Node hands it straight to Prisma as an
    /// id, and Prisma rejects the type, which lands in the route's outer catch.
    /// </summary>
    private static string? ReadIdField(JsonElement value, string field)
    {
        if (!RxJs.IsTruthy(value)) return null;

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"{field} must be a string; Prisma rejects any other type for an id filter.");
        }

        return value.GetString();
    }
}

// ── POST /api/prescriptions/extract-from-plan ─────────────────────────────────

/// <summary>
/// Pulls structured medications out of the free text of a SOAP note's PLAN section.
/// </summary>
/// <param name="CallerUserId">
/// The authenticated user. Gates the route — a physician profile must exist — and names the caller
/// in the model metadata.
/// </param>
/// <param name="PlanText">
/// The body's <c>planText</c>, RAW. It must stay untyped: a truthy NON-string reaches
/// <c>.trim()</c> in Node and throws, which is a <b>500, not the 400</b>, and a <c>string</c>
/// binding would collapse the two.
/// </param>
public sealed record RxAiExtractFromPlanCommand(string CallerUserId, JsonElement PlanText)
    : IRequest<RxAiExtractFromPlanResponse>;

/// <summary>
/// Port of <c>POST /api/prescriptions/extract-from-plan</c> (prescriptions.js:1078-1143).
///
/// <para>Physician-only, and otherwise always a 200 once that gate passes: <b>every AI failure
/// short of a throw is invisible</b>. No match, an unparseable block, a non-array and an
/// all-dropped array each answer <c>200 {"medications":[]}</c> with NO error key, and a port must
/// not add one — the client can only report "No medications found in the Plan text".</para>
///
/// <para>It reads nothing from the visit: <c>planText</c> comes from the body, so
/// <see cref="IVisitDirectory"/> is deliberately not injected even though
/// <c>VisitSummary.Plan</c> exists.</para>
///
/// <para>Unmetered in Node as well — there is no <c>aiUsageCheck</c> on this route — so unlike
/// <c>transcribe-rx</c> this handler has no parity gap on quota.</para>
/// </summary>
public sealed class RxAiExtractFromPlanHandler(
    IIdentityDirectory identity,
    IAiGateway ai,
    IAppLogger<RxAiExtractFromPlanHandler> logger)
    : IRequestHandler<RxAiExtractFromPlanCommand, RxAiExtractFromPlanResponse>
{
    /// <summary>
    /// GREEDY on purpose: the FIRST <c>[</c> to the LAST <c>]</c> anywhere in the model's answer
    /// (prescriptions.js:1119). It is not a JSON-aware scan, so prose containing a bracket before
    /// or after the array corrupts the match and the swallowed parse failure yields <c>[]</c>. It
    /// also happens to survive markdown fences, which is why this route — unlike
    /// <c>transcribe-rx</c> — strips none.
    /// </summary>
    private static readonly Regex JsonArrayPattern = new(@"\[[\s\S]*\]", RegexOptions.Compiled);

    public Task<RxAiExtractFromPlanResponse> Handle(
        RxAiExtractFromPlanCommand request, CancellationToken cancellationToken = default)
        => RxAiPersistence.RunAsync(
            logger, "[Rx Extract] Error", "Failed to extract medications",
            cancellationToken, () => HandleCore(request, cancellationToken));

    private async Task<RxAiExtractFromPlanResponse> HandleCore(
        RxAiExtractFromPlanCommand request, CancellationToken ct)
    {
        var planText = ReadPlanText(request.PlanText);

        // THE 400 IS A LENGTH TEST, not an emptiness test: `!planText || planText.trim().length < 5`
        // (prescriptions.js:1081), so "abc", "ok", "1234" and "    " all 400 as well.
        if (planText is null || planText.Trim().Length < 5)
            throw RxErrors.BadRequest("Plan text is required");

        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, ct);
        if (physician is null)
            throw RxErrors.Forbidden("Only physicians can extract prescriptions");

        var result = await ai.ChatAsync(
            new AiChatRequest(
                System: ExtractSystemPrompt,
                User: planText,
                Temperature: 0.1,
                MaxTokens: 1000,
                // Explicitly false (prescriptions.js:1114): identical plan text always re-hits the
                // model. The transcribe-rx parse call is the opposite and takes the cached default.
                UseCache: false,
                Agent: "specialty_extract",
                UserId: request.CallerUserId),
            ct);

        IReadOnlyList<RxMedicationLine> medications = [];

        // The regex itself lives INSIDE Node's try (prescriptions.js:1118-1136), so a gateway that
        // hands back no text at all degrades to the empty array here rather than 500ing.
        try
        {
            var match = JsonArrayPattern.Match(result.Text);

            // No match at all is a SILENT empty result — Node's catch never even runs.
            // A parse that succeeds but yields a non-array (the model wrapped the list in an
            // object) also resets to [].
            if (match.Success && JsonNode.Parse(match.Value) is JsonArray parsed)
                medications = RxAiValues.ToMedicationLines(parsed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Logged and swallowed (prescriptions.js:1135). The response is indistinguishable
            // from "the model found nothing".
            string? rawText = result.Text;

            logger.Error("[Rx Extract] Failed to parse AI response", exception, new
            {
                // `result.text?.slice(0, 200)` — null-safe, because this is the path a null text
                // arrives on.
                RawText = rawText is { Length: > 200 } ? rawText[..200] : rawText
            });
        }

        logger.Information(
            "[Rx Extract] Extracted medications from plan text", new { Count = medications.Count });

        return new RxAiExtractFromPlanResponse(medications);
    }

    /// <summary>
    /// Distinguishes the three inputs Node distinguishes: falsy takes the 400, a string is
    /// measured, and a truthy non-string throws — <c>planText.trim is not a function</c> — which
    /// the file guard renders as <c>500 {"error":"Failed to extract medications"}</c>.
    /// </summary>
    private static string? ReadPlanText(JsonElement planText)
    {
        if (!RxJs.IsTruthy(planText)) return null;

        if (planText.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "planText is not a string; Node throws on planText.trim() and answers 500.");
        }

        return planText.GetString();
    }

    /// <summary>
    /// prescriptions.js:1093-1108, verbatim. The arrows are U+2192 and the quoting is the source's.
    /// </summary>
    private const string ExtractSystemPrompt =
        """
        You are a prescription parser. Given the PLAN section of a SOAP note, extract ONLY the medications mentioned. Return a JSON array of medication objects.

        For each medication, extract:
        - drugName: The drug name (required)
        - dosage: Dose amount and form (e.g., "500mg", "10mg tablet")
        - frequency: How often (e.g., "Once daily", "Twice daily", "Three times daily", "Every 8 hours")
        - duration: Treatment duration (e.g., "7 days", "30 days", "2 weeks", "ongoing")
        - instructions: Special instructions (e.g., "Take with food", "Before bedtime", "On empty stomach")

        Rules:
        - Extract ONLY medications (drugs, drops, injections, etc.). Skip non-medication items (referrals, follow-ups, lifestyle advice).
        - If a field is not mentioned, set it to an empty string "".
        - Normalize drug names to proper capitalization.
        - Normalize frequency to standard phrases (e.g., "once a day" → "Once daily", "BID" → "Twice daily", "TID" → "Three times daily", "QID" → "Four times daily").
        - Return ONLY a JSON array. No markdown, no explanation, no code fences.
        - If no medications found, return an empty array [].
        """;
}

// ── POST /api/prescriptions/transcribe-rx ─────────────────────────────────────

/// <summary>
/// Dictation: an audio recording becomes a transcript, and the transcript becomes structured
/// medications.
/// </summary>
/// <param name="CallerUserId">
/// The authenticated user. Gates the route — a physician profile must exist — and names the caller
/// in the model metadata.
/// </param>
/// <param name="Audio">
/// The uploaded file's bytes. The controller owns the multipart binding, the 25 MB cap and the
/// <c>audio/*</c> filter, exactly as <c>VisitsController.Transcribe</c> does — multer's
/// rejections reach Node's GLOBAL error handler and come out as 500s with multer's own text, not
/// as 4xx. An absent part must arrive here as an EMPTY array so that this handler, not the
/// controller, renders the 400.
/// </param>
/// <param name="FileName">Passed to the gateway for its temp file; the client always sends <c>rx_dictation.webm</c>.</param>
/// <param name="Language">
/// The multipart <c>language</c> field. Empty means <c>"en"</c> — Node's
/// <c>req.body.language || 'en'</c> is applied in the ROUTE, not the gateway, which makes
/// Whisper's auto-detection unreachable and transcribes Arabic dictation as English.
/// </param>
public sealed record RxAiTranscribeCommand(
    string CallerUserId,
    byte[] Audio,
    string? FileName,
    string? Language) : IRequest<RxAiTranscribeResult>;

/// <summary>
/// Port of <c>POST /api/prescriptions/transcribe-rx</c> (prescriptions.js:972-1069).
///
/// <para>THE ASYMMETRY THAT MATTERS: the handler degrades gracefully when the parse step's TEXT is
/// unreadable, and not at all when either model call THROWS. A throw discards a transcript that
/// was already produced and paid for, and answers 500 with the RAW exception message — the only
/// dynamic error body in the file, and the only signal the client's recorder has that no provider
/// is configured. Both are reproduced rather than tidied.</para>
///
/// <para><b><c>aiUsageCheck('scribe') IS NOT PORTED.</c></b> In Node the middleware order is
/// <c>multer → aiUsageCheck → handler</c>, so a PATIENT calling this receives
/// <c>403 {"error":"Feature not available on your plan", …, "upgradeRequired":true}</c> and never
/// reaches the physician check. Here they receive
/// <c>403 {"error":"Only physicians can dictate prescriptions"}</c> instead, and the 429
/// "Monthly AI limit reached" body does not exist at all. A known parity gap
/// (docs/prescriptions-surface.md §11) — do NOT invent the gate.</para>
///
/// <para>Nothing is persisted. The physician reviews the extracted lines and posts
/// <c>POST /api/prescriptions</c> separately.</para>
/// </summary>
public sealed class RxAiTranscribeHandler(
    IIdentityDirectory identity,
    IAiGateway ai,
    IAppLogger<RxAiTranscribeHandler> logger)
    : IRequestHandler<RxAiTranscribeCommand, RxAiTranscribeResult>
{
    // The fence stripping here is `.replace(/```json\n?/g,'').replace(/```\n?/g,'').trim()`
    // followed by JSON.parse of the WHOLE cleaned string — DIFFERENT from extract-from-plan's
    // greedy bracket regex. The two endpoints fail on different malformed outputs and both are
    // ported literally.
    private static readonly Regex JsonFencePattern = new(@"```json\n?", RegexOptions.Compiled);
    private static readonly Regex FencePattern = new(@"```\n?", RegexOptions.Compiled);

    private const string ParseErrorMessage =
        "Could not parse medications from transcript. Please add them manually.";

    public Task<RxAiTranscribeResult> Handle(
        RxAiTranscribeCommand request, CancellationToken cancellationToken = default)
        => RxAiPersistence.RunWithDynamicErrorAsync(
            logger,
            "[Rx Dictation] Error",
            // `error.message || 'Failed to transcribe prescription'` (prescriptions.js:1067).
            exception => string.IsNullOrEmpty(exception.Message)
                ? "Failed to transcribe prescription"
                : exception.Message,
            cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxAiTranscribeResult> HandleCore(
        RxAiTranscribeCommand request, CancellationToken ct)
    {
        // Node's `if (!req.file)`. A zero-byte part would pass that test, but the two are
        // indistinguishable once the file is a byte[] and an empty upload has nothing to
        // transcribe — the same call the Visits transcribe port made.
        if (request.Audio is null or { Length: 0 })
            throw RxErrors.BadRequest("No audio file provided");

        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, ct);
        if (physician is null)
            throw RxErrors.Forbidden("Only physicians can dictate prescriptions");

        var transcribed = await ai.TranscribeAsync(
            new AiTranscriptionRequest(
                Audio: request.Audio,
                FileName: request.FileName,
                Language: string.IsNullOrEmpty(request.Language) ? "en" : request.Language,
                Agent: "scribe",
                UserId: request.CallerUserId),
            ct);

        var transcript = transcribed.Text;

        // Charged for Whisper and audited BEFORE this rejection — the 400 comes after the spend.
        if (string.IsNullOrEmpty(transcript) || transcript.Trim().Length < 3)
            throw RxErrors.BadRequest("Could not transcribe audio. Please try again.");

        var parsed = await ai.ChatAsync(
            new AiChatRequest(
                System: ParseSystemPrompt,
                User: transcript,
                Temperature: 0.1,
                MaxTokens: 1000,
                // The DEFAULT here, unlike the other two AI routes: two identical transcripts
                // return the cached medications with no second model call.
                UseCache: true,
                // 'scribe' maps to {openai, whisper-1} in AGENT_MODEL_DEFAULTS, but only the
                // PROVIDER reorders the chain — the model is never passed on, so this chat still
                // runs on the configured chat model. Inert, and must not be "fixed".
                Agent: "scribe",
                UserId: request.CallerUserId),
            ct);

        try
        {
            var cleaned = FencePattern
                .Replace(JsonFencePattern.Replace(parsed.Text, string.Empty), string.Empty)
                .Trim();

            // A successful parse that yields a NON-ARRAY is not a parse failure: medications
            // becomes [] and the response keeps the success shape, transcribeDuration included.
            IReadOnlyList<RxMedicationLine> medications = JsonNode.Parse(cleaned) is JsonArray array
                ? RxAiValues.ToMedicationLines(array)
                : [];

            return new RxAiTranscribeResponse(transcript, medications, transcribed.Duration);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 200, not 500: the transcript is returned so the physician can type the drugs in.
            logger.Error("[Rx Dictation] Failed to parse AI response", exception, new
            {
                RawText = parsed.Text
            });

            return new RxAiTranscribeParseErrorResponse(transcript, [], ParseErrorMessage);
        }
    }

    /// <summary>
    /// prescriptions.js:1002-1020, verbatim. The arrows are U+2192; the example output line is one
    /// long line in the source and must stay one line.
    /// </summary>
    private const string ParseSystemPrompt =
        """
        You are a prescription parser for physicians. Given a dictated prescription transcript, extract ONLY the medications mentioned. Return a JSON array of medication objects.

        For each medication, extract:
        - drugName: The drug name (required)
        - dosage: Dose amount and form (e.g., "500mg", "10mg tablet")
        - frequency: How often (e.g., "Once daily", "Twice daily", "Three times daily", "Every 8 hours")
        - duration: Treatment duration (e.g., "7 days", "30 days", "2 weeks", "ongoing")
        - instructions: Special instructions (e.g., "Take with food", "Before bedtime", "On empty stomach")

        Rules:
        - Extract ONLY what is explicitly mentioned. Do NOT infer or add information.
        - If a field is not mentioned, set it to an empty string "".
        - Normalize drug names to proper capitalization (e.g., "metformin" → "Metformin").
        - Normalize frequency to standard phrases (e.g., "once a day" → "Once daily", "BID" → "Twice daily", "TID" → "Three times daily").
        - If the doctor says "and" or lists multiple drugs, create separate entries for each.
        - Return ONLY a JSON array. No markdown, no explanation.

        Example input: "Amoxicillin 500mg three times a day for 7 days, and Ibuprofen 400mg as needed for pain"
        Example output: [{"drugName":"Amoxicillin","dosage":"500mg","frequency":"Three times daily","duration":"7 days","instructions":""},{"drugName":"Ibuprofen","dosage":"400mg","frequency":"As needed","duration":"","instructions":"For pain"}]
        """;
}
