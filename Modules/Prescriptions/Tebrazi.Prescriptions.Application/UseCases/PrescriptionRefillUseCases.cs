using System.Globalization;
using System.Text.Encodings.Web;
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
//  Group C — refills and patient medication control, ported from
//  server/src/routes/prescriptions.js:
//
//      POST /api/prescriptions/{id}/refill-request    (L526-L577)
//      PUT  /api/prescriptions/{id}/refill-respond    (L585-L637)
//      PUT  /api/prescriptions/{id}/stop-medication   (L646-L686)
//      PUT  /api/prescriptions/{id}/resume-medication (L694-L729)
//
//  All four are WRITE endpoints, so per the module's house rule the caller's user id arrives as an
//  explicit `CallerUserId` on the command and none of them injects ICurrentUser.
//
//  ─── The three asymmetries this group exists to preserve ───
//
//  1. THE refillNotes NULL-VS-PRESERVE ASYMMETRY.
//     refill-request writes `refillNotes: notes || null` (L552) — an UNCONDITIONAL overwrite, so a
//     re-request with no notes silently WIPES whatever note the physician left with their last
//     decision. refill-respond writes `refillNotes: notes || prescription.refillNotes` (L610) — a
//     PRESERVE, so there is no way to clear the column through that route. Same column, opposite
//     rule, and getting them the same way round is a defect. The two entity methods already encode
//     this (`RequestRefill` assigns unconditionally, `RespondToRefill` only when notes is
//     non-null), so both handlers just have to hand them the JS-truthy value.
//
//  2. THE AUTHORIZATION ASYMMETRY. refill-request is the PATIENT's action and refill-respond is the
//     PHYSICIAN's, so they gate on different things in a different ORDER:
//
//       refill-request : prescription -> 404, then visit.patientUserId == caller -> 403.
//       refill-respond : action validity -> 400 FIRST (before the row is even loaded, so a bad
//                        action on a nonexistent id is a 400 and NOT a 404), then prescription ->
//                        404, then the caller's own physician profile -> 403.
//
//     Both medication routes use refill-request's patient gate. So on all three patient routes the
//     prescribing physician gets 403, and on refill-respond the patient gets 403 — the 403 literal
//     is the same "Not your prescription" on all four.
//
//  3. THE STOP-VS-RESUME 400/500 ASYMMETRY. stop-medication opens with
//     `if (medicationIndex === undefined || medicationIndex === null)` -> 400 (L652-L654).
//     resume-medication HAS NO SUCH CHECK, so an absent index slips past both bounds comparisons
//     (`undefined < 0` and `undefined >= n` are both false) and destructures `meds[undefined]`,
//     throwing a TypeError that becomes 500 {"error":"Failed to resume medication"}. audit-1 files
//     one contract error on each route over this. It is NOT to be normalised into matching
//     validation.
//
//  ─── What none of the four does ───
//
//  * No status gate on the medication routes: a patient can stop a medication on a never-sent
//    prescription. refill-request has the group's only status gate.
//  * No `deletedAt` filter anywhere — every one of them is a bare `findUnique({ where: { id } })`,
//    which is why they all load through `IPrescriptionStore.GetForUpdateAsync`.
//  * No transaction. Node uses none here; a single SaveChangesAsync per endpoint is the port.
//  * No cache invalidation. `GET /` (15s) and `GET /summary` (30s) keep serving the pre-mutation
//    view in Node; the port deliberately does not reproduce that stale window (see the Group A
//    handlers), which is why nothing here touches a cache.
//  * No reminder cleanup. Stopping a medication does NOT cancel the DAILY reminders `/send`
//    created, in either backend. `IMedicationReminderScheduler` is a `/send`-only port and is not
//    injected here.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The route-level catch-all, one per handler, mapping any unexpected failure onto that route's OWN
/// named 500 body.
///
/// <para>Each Node handler is wrapped END TO END in a try/catch whose only outcome is a named 500 —
/// <c>"Failed to request refill"</c>, <c>"Failed to respond to refill"</c>,
/// <c>"Failed to stop medication"</c>, <c>"Failed to resume medication"</c>. Letting an EF or
/// directory exception escape would emit ExceptionHandlingMiddleware's generic
/// <c>{"error":"Internal Server Error"}</c> instead, and the client branches on
/// <c>err.response.data.error</c>.</para>
///
/// <para>Because Node's try opens before the prescription read and closes after the update, this
/// guard covers the read, the directory lookups AND <c>SaveChangesAsync</c> — there is no separate
/// save-level guard, since all four routes would give it the same literal.</para>
///
/// <para>It is also the mechanism that reproduces Node's THROWN paths as their documented statuses:
/// the handlers below raise a plain <see cref="InvalidOperationException"/> exactly where Node
/// raises a TypeError (destructuring <c>undefined</c>, dereferencing a missing
/// <c>prescription.visit</c>) or hands Prisma a value of the wrong type for a <c>String</c> column.
/// The guard then logs it under Node's own log message and answers Node's own 500 — which is what
/// Node's outer catch does, and is why those sites do not throw <c>RxErrors.ServerError</c>
/// directly (an <see cref="AppException"/> passes through unlogged).</para>
///
/// <para>Declared internal to this file with the group's prefix: four sibling handler files are
/// being written into this one namespace and a shared copy of this type would collide.</para>
/// </summary>
internal static class RxRefillPersistence
{
    /// <remarks>
    /// An <see cref="AppException"/> passes through untouched, so the deliberate 400/403/404s below
    /// keep their bare <c>{ "error": ... }</c> bodies. The nested best-effort notification catches
    /// stay INSIDE this, exactly as Node nests its own.
    /// </remarks>
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

/// <summary>
/// One slot of the medications array as JavaScript sees it, which needs THREE states where a
/// <see cref="JsonNode"/> alone has two.
///
/// <list type="bullet">
/// <item><c>Exists == false</c> — JS <c>undefined</c>: the property is not on the array at all
/// (an out-of-range or non-index key). Spreading it yields <c>{}</c>; DESTRUCTURING it throws.</item>
/// <item><c>Exists == true, Value == null</c> — a JSON <c>null</c> element. Spreading it also
/// yields <c>{}</c>, and destructuring it ALSO throws — which is why the two states behave
/// identically on stop-medication and identically on resume-medication, but the pair still has to
/// be distinguishable from a real element.</item>
/// <item><c>Exists == true, Value != null</c> — the element.</item>
/// </list>
/// </summary>
/// <param name="Exists">False for JS <c>undefined</c> — the key is absent from the array.</param>
/// <param name="Value">The element, or null for a JSON <c>null</c> element.</param>
internal readonly record struct RxRefillSlot(bool Exists, JsonNode? Value);

/// <summary>
/// The JavaScript coercions the two medication routes depend on. They are here rather than in
/// <c>RxJs</c> because only this group needs them, and they are the difference between reproducing
/// audit-1's stop/resume index matrix and returning the model binder's
/// <c>400 {"error":"Validation failed", ...}</c> for every interesting row of it.
///
/// <para><b>Two different conversions are applied to the same <c>medicationIndex</c> value</b>, and
/// conflating them is the easiest way to get this wrong:</para>
///
/// <list type="number">
/// <item>the bounds test <c>medicationIndex &lt; 0 || medicationIndex &gt;= meds.length</c>
/// (L666, L710) uses <b>ToNumber</b> — so <c>"1"</c> is 1, <c>null</c> is 0, <c>true</c> is 1,
/// <c>[]</c> is 0, and <c>"abc"</c> / <c>{}</c> / <c>undefined</c> are NaN, whose every relational
/// comparison is false and which therefore SLIPS PAST the guard;</item>
/// <item>the element access <c>meds[medicationIndex]</c> (L671, L715) uses <b>ToString as a
/// property key</b> — so <c>1.5</c> becomes <c>"1.5"</c>, <c>true</c> becomes <c>"true"</c> and
/// <c>[]</c> becomes <c>""</c>, none of which is an array index, while <c>-0</c> becomes
/// <c>"0"</c> and DOES target element 0.</item>
/// </list>
///
/// <para>A value that passes the guard on NaN but is not a canonical index lands on a NAMED
/// property of the array, which <c>JSON.stringify</c> drops — the source of stop-medication's
/// silent <c>200 {"message":"Stopped undefined","medications":&lt;unchanged&gt;}</c> no-op.</para>
/// </summary>
internal static class RxRefillJs
{
    /// <summary>
    /// Express emits UTF-8 verbatim and so must the JSON text this group writes back into the
    /// medications column: <c>ToJsonString</c>'s default encoder escapes every non-ASCII character,
    /// so an Arabic drug name would land in the stored value as a run of <c>\uXXXX</c> escapes.
    /// Matches the host's own <c>UnsafeRelaxedJsonEscaping</c> (Program.cs:56); the RESPONSE is
    /// already serialized with that encoder by the MVC pipeline.
    /// </summary>
    private static readonly JsonSerializerOptions MedicationsJson =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// <c>Array.isArray(prescription.medications) ? [...prescription.medications] : []</c>
    /// (L665, L709) over the opaque JSON text this port stores instead of a jsonb column.
    ///
    /// <para>A shallow JS spread is enough for Node because the elements are only ever REPLACED,
    /// never mutated in place; the clone here is deep because a <see cref="JsonNode"/> may not be
    /// attached to two parents. Cloning also preserves each untouched element's original key order
    /// and raw number text, which is what makes the echoed array byte-faithful.</para>
    ///
    /// <para>Anything that is not a JSON array — an object, a scalar, or (unreachable in Node,
    /// where the column is jsonb and cannot hold invalid JSON) unparseable text — becomes the EMPTY
    /// array, which both routes then write back OVER the stored value. That data loss is Node's,
    /// not this port's.</para>
    /// </summary>
    public static JsonArray CloneArray(string? medicationsJson)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(medicationsJson ?? "null");
        }
        catch (JsonException)
        {
            return new JsonArray();
        }

        if (parsed is not JsonArray array) return new JsonArray();

        var copy = new JsonArray();
        foreach (var element in array) copy.Add(element?.DeepClone());
        return copy;
    }

    /// <summary>Compact JSON text for the rebuilt array, UTF-8 verbatim.</summary>
    public static string Serialize(JsonArray medications) => medications.ToJsonString(MedicationsJson);

    /// <summary>
    /// JavaScript <c>ToNumber</c>, for the bounds guard only. <c>undefined</c> and a plain object
    /// are NaN; <c>null</c> and <c>false</c> are 0; <c>true</c> is 1; an array goes through its
    /// join (<c>[]</c> → <c>""</c> → 0, <c>[5]</c> → <c>"5"</c> → 5, <c>[1,2]</c> → <c>"1,2"</c> →
    /// NaN).
    /// </summary>
    public static double ToNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => double.NaN,
        JsonValueKind.Null => 0d,
        JsonValueKind.True => 1d,
        JsonValueKind.False => 0d,
        JsonValueKind.Number => value.TryGetDouble(out var number) ? number : double.NaN,
        JsonValueKind.String => StringToNumber(value.GetString()),
        JsonValueKind.Array => StringToNumber(JoinArray(value)),
        _ => double.NaN
    };

    /// <summary>
    /// JavaScript <c>ToString</c> applied to a value used as a property key —
    /// <c>meds[medicationIndex]</c>. A plain object is the literal <c>"[object Object]"</c>.
    /// </summary>
    public static string ToPropertyKey(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => "undefined",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => NumberToString(value.TryGetDouble(out var number) ? number : double.NaN),
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => JoinArray(value),
        _ => "[object Object]"
    };

    /// <summary>
    /// Whether a property key is a canonical array index, which is what decides between replacing
    /// an element and setting a named property JSON serialization discards. <c>"0"</c> and
    /// <c>"12"</c> qualify; <c>"01"</c>, <c>"1.0"</c>, <c>" 1"</c>, <c>"+1"</c>, <c>"-1"</c>,
    /// <c>"1e0"</c>, <c>""</c> and <c>"length"</c> do not.
    /// </summary>
    /// <param name="key">The property key produced by <see cref="ToPropertyKey"/>.</param>
    /// <param name="index">The parsed index; 0 when this returns false.</param>
    public static bool TryArrayIndex(string key, out int index)
    {
        index = 0;
        if (key.Length is 0 or > 10) return false;
        if (key[0] == '0' && key.Length > 1) return false;
        foreach (var character in key)
        {
            if (character is < '0' or > '9') return false;
        }

        return int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>Reads <c>meds[key]</c> with JavaScript's three-state result.</summary>
    /// <param name="medications">The array being indexed.</param>
    /// <param name="key">The property key.</param>
    public static RxRefillSlot Read(JsonArray medications, string key)
        => TryArrayIndex(key, out var index) && index < medications.Count
            ? new RxRefillSlot(true, medications[index])
            : new RxRefillSlot(false, null);

    /// <summary>
    /// Assigns <c>meds[key] = value</c>. A canonical in-range index replaces the element; almost
    /// everything else is a named property in JavaScript, which <c>JSON.stringify</c> drops — so
    /// this writes nothing and the array is unchanged on the wire and in the column.
    ///
    /// <para><b>The one exception is <c>"length"</c>, which is an existing own property of every
    /// Array and is NOT an ordinary named-property set.</b> <c>meds["length"] = {...}</c> runs the
    /// spec's ArraySetLength, which computes <c>ToUint32({})</c> = <c>0</c> and
    /// <c>ToNumber({})</c> = <c>NaN</c>; the two disagree, so V8 throws
    /// <c>RangeError: Invalid array length</c> — in sloppy mode too, and for an empty array as well
    /// as a populated one. On stop-medication (prescriptions.js:671) that RangeError escapes to the
    /// route's outer catch and answers <c>500 {"error":"Failed to stop medication"}</c>, so this
    /// throws rather than returning silently and lets the file guard name that 500. The
    /// <c>NaN</c>/<c>"abc"</c>/<c>"1.5"</c>/<c>""</c>/<c>"[object Object]"</c> no-op is unaffected:
    /// those really are discarded named properties and really do answer 200.</para>
    ///
    /// <para><c>"__proto__"</c> deliberately stays on the no-op path. It assigns successfully in
    /// Node and leaves an Array exotic object that <c>JSON.stringify</c> still serializes as an
    /// array, so the 200 and the <c>"Stopped undefined"</c> message agree with this port already.
    /// Prototype method names such as <c>"map"</c> are not own properties and merely shadow, which
    /// is also a 200.</para>
    /// </summary>
    /// <param name="medications">The array being written.</param>
    /// <param name="key">The property key.</param>
    /// <param name="value">The replacement element.</param>
    public static void Write(JsonArray medications, string key, JsonObject value)
    {
        if (TryArrayIndex(key, out var index) && index < medications.Count)
        {
            medications[index] = value;
            return;
        }

        if (key == "length")
        {
            throw new InvalidOperationException(
                "medicationIndex resolved to the property 'length'; assigning an object to an "
                + "array's length runs ArraySetLength and throws RangeError: Invalid array length "
                + "in Node, which reaches the route's outer catch and names its own 500.");
        }
    }

    /// <summary>
    /// The own-enumerable-property copy an object spread makes — <c>{...meds[i]}</c> and the
    /// <c>...cleanMed</c> rest pattern build the same set.
    ///
    /// <para>JS spreads <c>undefined</c> and <c>null</c> to <c>{}</c> rather than throwing, a
    /// string to its per-character index map, and an array to its numeric index map; a number or a
    /// boolean has no own enumerable properties and yields <c>{}</c>. Key order is preserved,
    /// which the byte contract depends on.</para>
    /// </summary>
    /// <param name="slot">The element being spread.</param>
    public static JsonObject Spread(RxRefillSlot slot)
    {
        var spread = new JsonObject();
        switch (slot.Value)
        {
            case JsonObject sourceObject:
                foreach (var property in sourceObject)
                {
                    spread[property.Key] = property.Value?.DeepClone();
                }

                break;

            case JsonArray sourceArray:
                for (var index = 0; index < sourceArray.Count; index++)
                {
                    spread[index.ToString(CultureInfo.InvariantCulture)] = sourceArray[index]?.DeepClone();
                }

                break;

            case JsonValue sourceValue when sourceValue.GetValueKind() == JsonValueKind.String:
                var text = sourceValue.GetValue<string>();
                for (var index = 0; index < text.Length; index++)
                {
                    spread[index.ToString(CultureInfo.InvariantCulture)] =
                        JsonValue.Create(text[index].ToString());
                }

                break;
        }

        return spread;
    }

    /// <summary>
    /// A property read inside a template literal — <c>`Stopped ${element.drugName}`</c>. An absent
    /// key stringifies to the literal text <c>"undefined"</c> and a JSON null to <c>"null"</c>,
    /// which is how both routes come to answer 200 with <c>"Stopped undefined"</c>.
    /// </summary>
    /// <param name="element">The rebuilt element the message is read back off.</param>
    /// <param name="name">The property name.</param>
    public static string Interpolate(JsonObject element, string name)
        => element.TryGetPropertyValue(name, out var value) ? Stringify(value) : "undefined";

    /// <summary>JavaScript <c>String(x)</c> over a node, for template-literal interpolation.</summary>
    /// <param name="node">The value being stringified; null for a JSON null.</param>
    public static string Stringify(JsonNode? node)
    {
        if (node is null) return "null";

        switch (node.GetValueKind())
        {
            case JsonValueKind.String:
                return node.GetValue<string>();
            case JsonValueKind.Number:
                // Via the raw token rather than GetValue<double>(): a JsonValue that was CREATED
                // (rather than parsed) stores its CLR type, and GetValue<T> on one of those demands
                // an exact type match — GetValue<double>() on a JsonValue.Create(5) throws.
                var raw = node.ToJsonString();
                return double.TryParse(
                    raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? NumberToString(number)
                    : raw;
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var element in node.AsArray())
                {
                    parts.Add(element is null ? string.Empty : Stringify(element));
                }

                return string.Join(",", parts);
            default:
                return "[object Object]";
        }
    }

    /// <summary>
    /// <c>Array.prototype.join(',')</c> as <c>ToPrimitive</c> applies it, with null and undefined
    /// elements rendering as the empty string.
    /// </summary>
    private static string JoinArray(JsonElement value)
    {
        var parts = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            parts.Add(
                element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? string.Empty
                    : ToPropertyKey(element));
        }

        return string.Join(",", parts);
    }

    /// <summary>
    /// JavaScript's string-to-number conversion (the <c>Number(x)</c> one, NOT <c>parseInt</c> —
    /// <c>RxJs.ParseInt</c> is the prefix-scanning cousin and would make <c>"1abc"</c> a valid
    /// index). Whitespace-only text is 0, the <c>Infinity</c> literals and the <c>0x</c> /
    /// <c>0o</c> / <c>0b</c> radix prefixes are honoured, and anything unparseable is NaN.
    /// </summary>
    private static double StringToNumber(string? text)
    {
        if (text is null) return double.NaN;

        var trimmed = text.Trim();
        if (trimmed.Length == 0) return 0d;

        switch (trimmed)
        {
            case "Infinity" or "+Infinity":
                return double.PositiveInfinity;
            case "-Infinity":
                return double.NegativeInfinity;
        }

        if (trimmed.Length > 2 && trimmed[0] == '0')
        {
            var radix = char.ToLowerInvariant(trimmed[1]) switch
            {
                'x' => 16,
                'o' => 8,
                'b' => 2,
                _ => 0
            };

            if (radix != 0)
            {
                try
                {
                    return Convert.ToInt64(trimmed[2..], radix);
                }
                catch (Exception exception) when (exception is FormatException or OverflowException
                                                      or ArgumentException)
                {
                    return double.NaN;
                }
            }
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : double.NaN;
    }

    /// <summary>
    /// JavaScript's <c>Number::toString</c> for the values that can become a property key. Negative
    /// zero renders <c>"0"</c>, which is why <c>medicationIndex: -0</c> targets element 0.
    ///
    /// <para>Known limit: a magnitude at or beyond 1e21 renders in .NET's exponent form
    /// (<c>"1E+21"</c>) where JS gives <c>"1e+21"</c>. Such a value can only ever be a NAMED
    /// property that JSON serialization discards, so the difference is unobservable in both the
    /// response and the column.</para>
    /// </summary>
    private static string NumberToString(double number)
    {
        if (double.IsNaN(number)) return "NaN";
        if (double.IsPositiveInfinity(number)) return "Infinity";
        if (double.IsNegativeInfinity(number)) return "-Infinity";
        if (number == 0d) return "0";
        return number.ToString("R", CultureInfo.InvariantCulture);
    }
}

// ── POST /api/prescriptions/{id}/refill-request ──────────────────────────────

/// <param name="Notes">
/// The only field this route reads. Typed <c>JsonElement?</c> rather than <c>string?</c> because
/// Node stores <c>notes || null</c> (L552) and the JS truthiness of a non-string is observable:
/// <c>0</c>, <c>false</c>, <c>""</c> and <c>null</c> all become a stored null, whereas the STRING
/// <c>"0"</c> is kept, and a truthy non-string reaches Prisma as the wrong type for a
/// <c>String?</c> column and surfaces as <c>500 {"error":"Failed to request refill"}</c>. A plain
/// <c>string?</c> member would let the model binder answer its own 400 for those instead.
///
/// <para>Nullable is correct here — unlike <c>medicationIndex</c> below, this route cannot tell an
/// absent key from an explicit null, because <c>x || null</c> treats them identically.</para>
/// </param>
public sealed record RxRefillRequestBody(JsonElement? Notes);

/// <param name="PrescriptionId">The raw <c>:id</c> route segment; no uuid validation in Node.</param>
/// <param name="CallerUserId">
/// <c>req.user.id</c>. Must be the VISIT's patient — the prescribing physician gets 403 here.
/// </param>
/// <param name="Body">
/// The parsed body, or null when none was sent. Both client call sites send <c>{}</c>
/// (PrescriptionPanel.jsx:228, PrescriptionsPage.jsx:254).
/// </param>
public sealed record RxRefillRequestCommand(
    string PrescriptionId,
    string CallerUserId,
    RxRefillRequestBody? Body) : IRequest<RxRefillRequestResponse>;

/// <summary>
/// Port of <c>POST /api/prescriptions/{id}/refill-request</c> (prescriptions.js:526-577). The
/// patient asks for a refill: <c>refillStatus</c> → <c>"PENDING"</c>, <c>refillRequestedAt</c>
/// stamped, <c>refillNotes</c> overwritten, and the PRESCRIBING PHYSICIAN notified without an
/// email.
///
/// <para>Gate order, which is the contract: 404 (row), then 403 (not the visit's patient), then 400
/// (wrong status), then 400 (already pending). There is no <c>userType</c> check — any account that
/// is the visit's patient passes, whatever its claim says.</para>
///
/// <para>The status gate's LITERAL undersells it: the message names sent/dispensed but the allowed
/// list is <c>['SENT','DISPENSED','CONFIRMED']</c> (L539), so a CONFIRMED prescription — which is
/// what <c>/sign</c> produces — is refillable. Only DRAFT and SIGNED are rejected, and SIGNED is
/// what <c>POST /</c> creates, so a freshly created prescription cannot be refilled until it is
/// signed or sent.</para>
///
/// <para>Only <c>"PENDING"</c> blocks a re-request: after an APPROVED or DENIED decision the
/// patient may immediately request again, which resets the status to PENDING, moves
/// <c>refillRequestedAt</c> forward and — quirk 1 — WIPES the physician's decision note.</para>
/// </summary>
public sealed class RxRefillRequestHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IVisitDirectory visits,
    IIdentityDirectory identity,
    INotificationPublisher notifications,
    IAppLogger<RxRefillRequestHandler> logger)
    : IRequestHandler<RxRefillRequestCommand, RxRefillRequestResponse>
{
    private const string LogMessage = "[Rx] Refill request error";
    private const string FailureError = "Failed to request refill";

    public Task<RxRefillRequestResponse> Handle(
        RxRefillRequestCommand request, CancellationToken cancellationToken = default)
        => RxRefillPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxRefillRequestResponse> HandleCore(
        RxRefillRequestCommand request, CancellationToken cancellationToken)
    {
        var prescription = await prescriptions.GetForUpdateAsync(request.PrescriptionId, cancellationToken)
            ?? throw RxErrors.NotFound("Prescription not found");

        // `prescription.visit.patientUserId` (L536). Node's include is on a REQUIRED relation, so
        // the visit is always there; a null here means the Visits module could not resolve it, and
        // Node's equivalent — dereferencing a null `visit` — is a TypeError that its outer catch
        // turns into this route's named 500. Not a 404: there is no "visit not found" body.
        var visit = await visits.GetAsync(prescription.VisitId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Visit '{prescription.VisitId}' could not be resolved for prescription "
                + $"'{prescription.Id}'; Node dereferences a null visit and answers its named 500.");

        if (visit.PatientUserId != request.CallerUserId)
            throw RxErrors.Forbidden("Not your prescription");

        // ['SENT','DISPENSED','CONFIRMED'].includes(status) (L539). The message is the source's.
        if (prescription.Status is not (PrescriptionStatus.SENT
            or PrescriptionStatus.DISPENSED
            or PrescriptionStatus.CONFIRMED))
        {
            throw RxErrors.BadRequest("Can only request refill on sent/dispensed prescriptions");
        }

        // Free-text column compared against the bare string, not an enum. APPROVED and DENIED do
        // NOT block, and neither does null.
        if (prescription.RefillStatus == "PENDING")
            throw RxErrors.BadRequest("Refill request already pending");

        var notes = ResolveNotes(request.Body?.Notes);

        // QUIRK 1, the NULL half: refillNotes is assigned UNCONDITIONALLY, so a request with no
        // notes writes null over whatever the physician left with their last decision.
        prescription.RequestRefill(notes);

        await dbContext.SaveChangesAsync(cancellationToken);

        await NotifyPhysicianAsync(prescription, request, cancellationToken);

        // Two keys, from the row just written. Not the prescription.
        return new RxRefillRequestResponse(prescription.RefillStatus, prescription.RefillRequestedAt);
    }

    /// <summary>
    /// prescriptions.js:555-570. Node wraps this WHOLE block — both directory reads included — in a
    /// try/catch that only logs, so a failure to resolve either party must still answer 200 with the
    /// refill recorded. <see cref="INotificationPublisher"/>'s own no-throw guarantee does not cover
    /// the reads, so the catch is reproduced here.
    ///
    /// <para>The recipient is derived, not stored: <c>prescription.physicianId</c> is a PROFILE id
    /// and the notification needs a USER id. When that profile cannot be read Node sends NOTHING
    /// rather than falling back to another recipient — the <c>if (physician)</c> at L559.</para>
    ///
    /// <para><c>sendEmail: false</c>, deliberately: the physician gets an in-app notification and a
    /// push attempt but no email. refill-respond is the opposite.</para>
    /// </summary>
    private async Task NotifyPhysicianAsync(
        Prescription prescription,
        RxRefillRequestCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Read in Node's order — patient first, then the physician profile.
            var patient = await identity.GetUserAsync(request.CallerUserId, cancellationToken);
            var physician = await identity.GetPhysicianAsync(prescription.PhysicianId, cancellationToken);

            if (physician is null) return;

            // `patient?.displayName || 'A patient'` — an empty display name falls back too.
            var patientName = patient?.DisplayName is { Length: > 0 } displayName
                ? displayName
                : "A patient";

            await notifications.PublishAsync(
                new NotificationRequest(
                    UserId: physician.UserId,
                    Type: "REFILL_REQUEST",
                    Title: "Prescription Refill Request",
                    Message: $"{patientName} requested a prescription refill.",
                    // The RAW :id route segment, which is also the id that was found.
                    Data: new JsonObject { ["prescriptionId"] = request.PrescriptionId }.ToJsonString(),
                    SendEmail: false),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Error("[Rx] Refill notification error", exception);
        }
    }

    /// <summary>
    /// <c>refillNotes: notes || null</c> (L552) over a raw JSON value. Falsy inputs store null; a
    /// truthy non-string is the wrong type for the column and takes the route's own 500 through the
    /// file guard, exactly as Prisma's validation error does in Node.
    /// </summary>
    private static string? ResolveNotes(JsonElement? notes)
        => notes switch
        {
            null => null,
            { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False } => null,
            { ValueKind: JsonValueKind.String } text
                => text.GetString() is { Length: > 0 } value ? value : null,
            // `0 || null` is null; every other number is truthy and then fails the String check.
            { ValueKind: JsonValueKind.Number } number
                => number.TryGetDouble(out var value) && value == 0
                    ? null
                    : throw new InvalidOperationException(
                        "refillNotes received a truthy non-string; Prisma rejects it for a String? "
                        + "column and Node answers its named 500."),
            _ => throw new InvalidOperationException(
                "refillNotes received a truthy non-string; Prisma rejects it for a String? column "
                + "and Node answers its named 500.")
        };
}

// ── PUT /api/prescriptions/{id}/refill-respond ───────────────────────────────

/// <param name="Action">
/// REQUIRED, and validated FIRST — before the prescription is loaded. Must be exactly the string
/// <c>"APPROVED"</c> or <c>"DENIED"</c>: the test is <c>['APPROVED','DENIED'].includes(action)</c>
/// (L590), which is strict equality, so it is case-sensitive and no non-string value can pass.
/// Typed <c>JsonElement?</c> so a number or an object reaches that 400 rather than the binder's.
/// </param>
/// <param name="Notes">
/// Optional physician note. <c>JsonElement?</c> for the same reason as refill-request's, and with
/// the same truthy-non-string 500 — but the opposite null rule (see the handler).
/// </param>
public sealed record RxRefillRespondBody(JsonElement? Action, JsonElement? Notes);

/// <param name="PrescriptionId">The raw <c>:id</c> route segment.</param>
/// <param name="CallerUserId">
/// <c>req.user.id</c>. Must own a PhysicianProfile whose id equals <c>prescription.physicianId</c>;
/// a patient always gets 403 here.
/// </param>
/// <param name="Body">The parsed body, or null when none was sent — which is a 400.</param>
public sealed record RxRefillRespondCommand(
    string PrescriptionId,
    string CallerUserId,
    RxRefillRespondBody? Body) : IRequest<RxRefillRespondResponse>;

/// <summary>
/// Port of <c>PUT /api/prescriptions/{id}/refill-respond</c> (prescriptions.js:585-637). The
/// prescribing physician approves or denies a PENDING refill, and the patient is notified WITH an
/// email.
///
/// <para>Gate order, and it differs from every other route in this group: the action check runs
/// FIRST (L590), before the row is even loaded, so a bad action against a nonexistent id answers
/// <c>400 {"error":"Action must be APPROVED or DENIED"}</c> and NOT a 404. Then 404 (row), then 403
/// (no physician profile, or not the prescriber — one literal for both), then 400 (nothing
/// pending).</para>
///
/// <para>The action string is written VERBATIM into the free-text <c>refillStatus</c> column, so the
/// stored value is the caller's own uppercase text and never an enum ordinal.
/// <c>refillRequestedAt</c> is left alone and keeps the original request timestamp.</para>
/// </summary>
public sealed class RxRefillRespondHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IVisitDirectory visits,
    IIdentityDirectory identity,
    INotificationPublisher notifications,
    IAppLogger<RxRefillRespondHandler> logger)
    : IRequestHandler<RxRefillRespondCommand, RxRefillRespondResponse>
{
    private const string LogMessage = "[Rx] Refill respond error";
    private const string FailureError = "Failed to respond to refill";

    public Task<RxRefillRespondResponse> Handle(
        RxRefillRespondCommand request, CancellationToken cancellationToken = default)
        => RxRefillPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxRefillRespondResponse> HandleCore(
        RxRefillRespondCommand request, CancellationToken cancellationToken)
    {
        // FIRST — before the row is read. Moving this below the 404 would change the status code
        // for a bad action on an unknown id from 400 to 404.
        var action = ResolveAction(request.Body?.Action);

        var prescription = await prescriptions.GetForUpdateAsync(request.PrescriptionId, cancellationToken)
            ?? throw RxErrors.NotFound("Prescription not found");

        // `!physician || prescription.physicianId !== physician.id` — the two cases collapse into
        // ONE literal. There is no "Physician profile required" body on this route.
        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken);
        if (physician is null || prescription.PhysicianId != physician.Id)
            throw RxErrors.Forbidden("Not your prescription");

        // Null (never requested), APPROVED and DENIED all land here.
        if (prescription.RefillStatus != "PENDING")
            throw RxErrors.BadRequest("No pending refill request");

        var notes = ResolveNotes(request.Body?.Notes);

        // QUIRK 1, the PRESERVE half: `refillNotes: notes || prescription.refillNotes`. A falsy
        // notes keeps the existing value — the entity assigns only when notes is non-null — so this
        // route can never clear the column. The exact opposite of refill-request.
        prescription.RespondToRefill(action, notes);

        await dbContext.SaveChangesAsync(cancellationToken);

        await NotifyPatientAsync(prescription, request, action, notes, cancellationToken);

        return new RxRefillRespondResponse(prescription.RefillStatus, prescription.RefillNotes);
    }

    /// <summary>
    /// prescriptions.js:614-630. Best-effort, and the read of the VISIT lives inside it: Node
    /// dereferences <c>prescription.visit.patientUserId</c> only here, so an unresolvable visit
    /// costs the notification and still answers 200 — unlike refill-request, where the same read
    /// gates a 403 and its failure is a 500. That asymmetry is why the two handlers place the
    /// lookup differently.
    ///
    /// <para><c>sendEmail: true</c>: this route dispatches a real email to the patient, with the
    /// title as the subject.</para>
    /// </summary>
    /// <param name="prescription">The row just updated.</param>
    /// <param name="request">The command, for the raw <c>:id</c> in the data payload.</param>
    /// <param name="action">The validated action string.</param>
    /// <param name="notes">The resolved notes, null when the body's value was falsy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task NotifyPatientAsync(
        Prescription prescription,
        RxRefillRespondCommand request,
        string action,
        string? notes,
        CancellationToken cancellationToken)
    {
        try
        {
            var visit = await visits.GetAsync(prescription.VisitId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Visit '{prescription.VisitId}' could not be resolved; Node's TypeError here is "
                    + "swallowed by the notification catch and the response stays 200.");

            // A ternary on APPROVED, so only DENIED can produce "Refill Denied" — no other action
            // value survives the gate.
            var title = action == "APPROVED" ? "Refill Approved" : "Refill Denied";

            // `' Note: ' + notes` — ONE leading space after the full stop, no comma. Gated on the
            // RAW body value's truthiness, which is the same test that resolved `notes`.
            var noteSuffix = notes is null ? string.Empty : $" Note: {notes}";

            await notifications.PublishAsync(
                new NotificationRequest(
                    UserId: visit.PatientUserId,
                    Type: "REFILL_RESPONSE",
                    Title: title,
                    // `action.toLowerCase()` -> "approved" / "denied".
                    Message: "Your prescription refill request has been "
                        + $"{action.ToLowerInvariant()}.{noteSuffix}",
                    Data: new JsonObject { ["prescriptionId"] = request.PrescriptionId }.ToJsonString(),
                    SendEmail: true),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Error("[Rx] Refill response notification error", exception);
        }
    }

    /// <summary>
    /// <c>if (!['APPROVED','DENIED'].includes(action))</c> (L590). Strict membership, so an absent
    /// key, a null, a lowercase spelling and any non-string all take the same 400.
    /// </summary>
    private static string ResolveAction(JsonElement? action)
    {
        if (action is { ValueKind: JsonValueKind.String } element
            && element.GetString() is { } value
            && value is "APPROVED" or "DENIED")
        {
            return value;
        }

        throw RxErrors.BadRequest("Action must be APPROVED or DENIED");
    }

    /// <summary>
    /// <c>refillNotes: notes || prescription.refillNotes</c> (L610) over a raw JSON value. Returns
    /// null for every falsy input — which the entity reads as "preserve" — and takes the route's own
    /// 500 for a truthy non-string, which Prisma rejects for a <c>String?</c> column.
    /// </summary>
    private static string? ResolveNotes(JsonElement? notes)
        => notes switch
        {
            null => null,
            { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False } => null,
            { ValueKind: JsonValueKind.String } text
                => text.GetString() is { Length: > 0 } value ? value : null,
            { ValueKind: JsonValueKind.Number } number
                => number.TryGetDouble(out var value) && value == 0
                    ? null
                    : throw new InvalidOperationException(
                        "refillNotes received a truthy non-string; Prisma rejects it for a String? "
                        + "column and Node answers its named 500."),
            _ => throw new InvalidOperationException(
                "refillNotes received a truthy non-string; Prisma rejects it for a String? column "
                + "and Node answers its named 500.")
        };
}

// ── PUT /api/prescriptions/{id}/stop-medication ──────────────────────────────

/// <param name="MedicationIndex">
/// REQUIRED — but only against <c>undefined</c> and <c>null</c> (L652). <c>0</c>, <c>""</c>,
/// <c>false</c> and <c>"abc"</c> all pass that check, and the bounds test that follows is a JS
/// numeric comparison, so this MUST NOT be typed <c>int</c>: a typed binding answers
/// <c>400 {"error":"Validation failed", ...}</c> — a body no prescriptions route ever produces —
/// for every interesting row of audit-1's coercion matrix.
///
/// <para>NON-nullable <see cref="JsonElement"/> on purpose. Both routes need to tell an ABSENT key
/// (<see cref="JsonValueKind.Undefined"/>) from an explicit <c>null</c>
/// (<see cref="JsonValueKind.Null"/>), and a nullable <c>JsonElement?</c> binds both to C# null.
/// Here the two happen to share the 400; on resume-medication they diverge into a 500 and a 400,
/// which is precisely the asymmetry this group must preserve, so both routes model it the same way.
/// </para>
/// </param>
public sealed record RxRefillStopMedicationBody(JsonElement MedicationIndex);

/// <param name="PrescriptionId">The raw <c>:id</c> route segment.</param>
/// <param name="CallerUserId">
/// <c>req.user.id</c>. Must be the visit's patient; the prescribing physician gets 403.
/// </param>
/// <param name="Body">The parsed body, or null when none was sent — which is the 400.</param>
public sealed record RxRefillStopMedicationCommand(
    string PrescriptionId,
    string CallerUserId,
    RxRefillStopMedicationBody? Body) : IRequest<RxRefillStopMedicationResponse>;

/// <summary>
/// Port of <c>PUT /api/prescriptions/{id}/stop-medication</c> (prescriptions.js:646-686). The
/// patient marks one element of the medications JSON as stopped by appending <c>stoppedAt</c> and
/// <c>stoppedByPatient</c> to it, and gets the whole rebuilt array back.
///
/// <para>Gate order: 400 (index required — BEFORE the row is read), 404 (row), 403 (not the visit's
/// patient), 400 (index out of bounds). No status gate and no <c>deletedAt</c> filter, so a
/// never-sent or soft-deleted prescription is fully mutable here.</para>
///
/// <para>THE NAN-INDEX NO-OP (audit-1, and quirk 3's benign half). A non-numeric or fractional
/// index passes the bounds guard — every relational comparison against NaN is false — and then
/// lands on a NAMED property of the array, which JSON serialization discards. The response is
/// <c>200 {"message":"Stopped undefined","medications":&lt;UNCHANGED&gt;}</c>: the client shows a
/// success toast for a write that did nothing. Resume-medication given the identical body answers
/// 500 instead. Neither is to be normalised.</para>
///
/// <para>No side effects at all: no notification, no email, no push, no audit row, and no reminder
/// cleanup — the DAILY reminders <c>/send</c> created keep firing for a stopped drug in both
/// backends. One silent knock-on: <c>POST /check-interactions</c> filters on <c>!m.stoppedAt</c>,
/// so this write changes that endpoint's result.</para>
/// </summary>
public sealed class RxRefillStopMedicationHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IVisitDirectory visits,
    IAppLogger<RxRefillStopMedicationHandler> logger)
    : IRequestHandler<RxRefillStopMedicationCommand, RxRefillStopMedicationResponse>
{
    private const string LogMessage = "[Rx] Stop medication error";
    private const string FailureError = "Failed to stop medication";

    public Task<RxRefillStopMedicationResponse> Handle(
        RxRefillStopMedicationCommand request, CancellationToken cancellationToken = default)
        => RxRefillPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxRefillStopMedicationResponse> HandleCore(
        RxRefillStopMedicationCommand request, CancellationToken cancellationToken)
    {
        var medicationIndex = request.Body?.MedicationIndex ?? default;

        // QUIRK 3, the validating half: `medicationIndex === undefined || medicationIndex === null`
        // (L652), and it runs BEFORE the prescription read — so a missing index on a nonexistent id
        // is a 400, not a 404. resume-medication has no equivalent.
        if (medicationIndex.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw RxErrors.BadRequest("medicationIndex is required");

        var prescription = await prescriptions.GetForUpdateAsync(request.PrescriptionId, cancellationToken)
            ?? throw RxErrors.NotFound("Prescription not found");

        var visit = await visits.GetAsync(prescription.VisitId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Visit '{prescription.VisitId}' could not be resolved for prescription "
                + $"'{prescription.Id}'; Node dereferences a null visit and answers its named 500.");

        if (visit.PatientUserId != request.CallerUserId)
            throw RxErrors.Forbidden("Not your prescription");

        var medications = RxRefillJs.CloneArray(prescription.Medications);

        // ToNumber semantics, NaN included: a NaN index passes and reaches the no-op path below.
        var numericIndex = RxRefillJs.ToNumber(medicationIndex);
        if (numericIndex < 0 || numericIndex >= medications.Count)
            throw RxErrors.BadRequest("Invalid medication index");

        // `meds[medicationIndex]` uses the value as a PROPERTY KEY, which is a different conversion
        // from the bounds test above.
        var key = RxRefillJs.ToPropertyKey(medicationIndex);
        var replacement = RxRefillJs.Spread(RxRefillJs.Read(medications, key));

        // `stoppedAt: new Date().toISOString()` — a STRING with exactly three fractional digits and
        // a trailing Z, in the response AND in the stored JSON. Never a DateTime.
        replacement["stoppedAt"] = JsonValue.Create(
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        replacement["stoppedByPatient"] = JsonValue.Create(true);

        RxRefillJs.Write(medications, key, replacement);

        Persist(prescription, medications);
        await dbContext.SaveChangesAsync(cancellationToken);

        // `Stopped ${meds[medicationIndex].drugName}`, read back off the object just assigned — so
        // on the named-property path it is the fresh two-key object and the name is "undefined".
        var message = $"Stopped {RxRefillJs.Interpolate(replacement, "drugName")}";

        // The IN-MEMORY array, not a re-read of the row.
        return new RxRefillStopMedicationResponse(message, medications);
    }

    /// <summary>
    /// Writes the rebuilt array back, reproducing Node's guarantee that an UPDATE always happens.
    ///
    /// <para>Prisma issues <c>update({ data: { medications: meds } })</c> unconditionally, so
    /// <c>updatedAt</c> moves even when the serialized array is identical — which is exactly what
    /// the NaN-index no-op path produces, and what resuming a never-stopped medication produces.
    /// EF would write nothing there, so <c>MarkModified</c> forces the UPDATE for that case only.
    /// It is scoped to the no-op precisely because it rewrites EVERY column with the value this
    /// request read: on the mutating path, letting EF write just <c>medications</c> and
    /// <c>updated_at</c> keeps the column-level fidelity Prisma has, and avoids clobbering a
    /// concurrent edit to an unrelated column.</para>
    /// </summary>
    /// <param name="prescription">The tracked row.</param>
    /// <param name="medications">The rebuilt array.</param>
    private void Persist(Prescription prescription, JsonArray medications)
    {
        var json = RxRefillJs.Serialize(medications);
        if (json == prescription.Medications)
        {
            prescriptions.MarkModified(prescription);
            return;
        }

        prescription.ReplaceMedications(json);
    }
}

// ── PUT /api/prescriptions/{id}/resume-medication ────────────────────────────

/// <param name="MedicationIndex">
/// NOT validated for presence — the mirror of stop-medication's opening check simply is not there
/// (compare L652-L654 with L699). Non-nullable <see cref="JsonElement"/> because the difference
/// between an ABSENT key and an explicit <c>null</c> is OBSERVABLE on this route and on no other:
/// against an empty medications array, <c>undefined</c> slips past both bounds comparisons into the
/// 500 while <c>null</c> coerces to 0 and takes the <c>400 "Invalid medication index"</c>. A
/// nullable <c>JsonElement?</c> collapses both onto C# null and loses that; a typed <c>int</c>
/// loses the whole matrix.
/// </param>
public sealed record RxRefillResumeMedicationBody(JsonElement MedicationIndex);

/// <param name="PrescriptionId">The raw <c>:id</c> route segment.</param>
/// <param name="CallerUserId">
/// <c>req.user.id</c>. Must be the visit's patient; the prescribing physician gets 403.
/// </param>
/// <param name="Body">
/// The parsed body, or null when none was sent — which is the 500, not a 400.
/// </param>
public sealed record RxRefillResumeMedicationCommand(
    string PrescriptionId,
    string CallerUserId,
    RxRefillResumeMedicationBody? Body) : IRequest<RxRefillResumeMedicationResponse>;

/// <summary>
/// Port of <c>PUT /api/prescriptions/{id}/resume-medication</c> (prescriptions.js:694-729). The
/// patient un-stops a medication: <c>stoppedAt</c> and <c>stoppedByPatient</c> are REMOVED from the
/// element — absent keys, never nulls — and the rebuilt array comes back.
///
/// <para>Gate order: 404 (row), 403 (not the visit's patient), 400 (index out of bounds). There is
/// no required-field 400 and no status gate.</para>
///
/// <para>QUIRK 3, the throwing half. Node's write is
/// <c>const { stoppedAt, stoppedByPatient, ...cleanMed } = meds[medicationIndex]</c> (L715), and
/// DESTRUCTURING is not spreading: <c>undefined</c> and <c>null</c> throw a TypeError where an
/// object spread would quietly yield <c>{}</c>. So every index that reaches that line without
/// resolving to a real element becomes <c>500 {"error":"Failed to resume medication"}</c> —
/// an absent index, a non-numeric string, a fractional index, and a <c>null</c> against a
/// NON-empty array. stop-medication answers 200 for the same bodies. Reproduced below by raising
/// the TypeError's analogue and letting the file guard log it and name the 500, which is exactly
/// what Node's outer catch does.</para>
///
/// <para>audit-1 correction, encoded in <see cref="RxRefillResumeMedicationResponse"/>: no 200 on
/// this route can carry <c>medications: []</c>. With a non-array stored value every numeric index
/// fails the bounds test and every non-numeric one throws, so the empty array is unreachable —
/// unlike stop-medication, which reaches it and writes it.</para>
///
/// <para>No side effects: resuming does not re-create reminders (stop never deleted them), and
/// resuming an element that was never stopped is a legal 200 no-op.</para>
/// </summary>
public sealed class RxRefillResumeMedicationHandler(
    IPrescriptionsDbContext dbContext,
    IPrescriptionStore prescriptions,
    IVisitDirectory visits,
    IAppLogger<RxRefillResumeMedicationHandler> logger)
    : IRequestHandler<RxRefillResumeMedicationCommand, RxRefillResumeMedicationResponse>
{
    private const string LogMessage = "[Rx] Resume medication error";
    private const string FailureError = "Failed to resume medication";

    public Task<RxRefillResumeMedicationResponse> Handle(
        RxRefillResumeMedicationCommand request, CancellationToken cancellationToken = default)
        => RxRefillPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<RxRefillResumeMedicationResponse> HandleCore(
        RxRefillResumeMedicationCommand request, CancellationToken cancellationToken)
    {
        // Deliberately NO required check — see the type's remarks. `default` is
        // JsonValueKind.Undefined, which is the JS `undefined` that slips through to the 500.
        var medicationIndex = request.Body?.MedicationIndex ?? default;

        var prescription = await prescriptions.GetForUpdateAsync(request.PrescriptionId, cancellationToken)
            ?? throw RxErrors.NotFound("Prescription not found");

        var visit = await visits.GetAsync(prescription.VisitId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Visit '{prescription.VisitId}' could not be resolved for prescription "
                + $"'{prescription.Id}'; Node dereferences a null visit and answers its named 500.");

        if (visit.PatientUserId != request.CallerUserId)
            throw RxErrors.Forbidden("Not your prescription");

        var medications = RxRefillJs.CloneArray(prescription.Medications);

        // `null >= 0` is TRUE, so a null index against an EMPTY array is the one input that takes
        // this 400 rather than the 500 below.
        var numericIndex = RxRefillJs.ToNumber(medicationIndex);
        if (numericIndex < 0 || numericIndex >= medications.Count)
            throw RxErrors.BadRequest("Invalid medication index");

        var key = RxRefillJs.ToPropertyKey(medicationIndex);
        var slot = RxRefillJs.Read(medications, key);

        // The destructuring TypeError: `undefined` (a non-index or out-of-array key) and a JSON
        // `null` element both throw here, where stop-medication's object spread does not. A plain
        // exception rather than RxErrors.ServerError so the file guard LOGS it under Node's own log
        // message before naming the 500 — an AppException would pass through unlogged.
        //
        // `medicationIndex: "length"` also lands here, by a different route than Node takes but to
        // the same wire result. In Node `meds["length"]` is the array LENGTH, so the destructuring
        // at prescriptions.js:715 SUCCEEDS (a number has no own enumerable properties, giving
        // `cleanMed = {}`) and it is the assignment at :716 that throws RangeError through
        // ArraySetLength. Either way the route answers 500 {"error":"Failed to resume medication"},
        // which is what this throw produces — so `RxRefillJs.Write`'s own "length" guard is
        // unreachable from this handler. The logged reason differs; the response does not.
        if (!slot.Exists || slot.Value is null)
        {
            throw new InvalidOperationException(
                $"medicationIndex resolved to property '{key}', which is not an element of the "
                + "medications array; Node destructures undefined here and throws a TypeError.");
        }

        // `{ stoppedAt, stoppedByPatient, ...cleanMed }` — the rest pattern is the same own-property
        // copy an object spread makes, minus the two named keys. Surviving keys keep their order,
        // and the two flags are REMOVED rather than nulled.
        var cleaned = RxRefillJs.Spread(slot);
        cleaned.Remove("stoppedAt");
        cleaned.Remove("stoppedByPatient");

        RxRefillJs.Write(medications, key, cleaned);

        Persist(prescription, medications);
        await dbContext.SaveChangesAsync(cancellationToken);

        // `Resumed ${meds[medicationIndex].drugName}`, computed AFTER the replacement — so an
        // element that carried only the two stop flags yields "Resumed undefined" with a 200.
        var message = $"Resumed {RxRefillJs.Interpolate(cleaned, "drugName")}";

        return new RxRefillResumeMedicationResponse(message, medications);
    }

    /// <summary>
    /// Writes the rebuilt array back. Node's UPDATE is unconditional, so <c>updatedAt</c> moves even
    /// when the cleaned element is identical to the original — the common "resume something that was
    /// never stopped" case — which EF would otherwise skip entirely. <c>MarkModified</c> covers only
    /// that no-op, keeping the mutating path's write column-scoped exactly as Prisma's is.
    /// </summary>
    /// <param name="prescription">The tracked row.</param>
    /// <param name="medications">The rebuilt array.</param>
    private void Persist(Prescription prescription, JsonArray medications)
    {
        var json = RxRefillJs.Serialize(medications);
        if (json == prescription.Medications)
        {
            prescriptions.MarkModified(prescription);
            return;
        }

        prescription.ReplaceMedications(json);
    }
}
