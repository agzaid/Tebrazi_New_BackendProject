using System.Globalization;
using System.Text.Json;
using Tebrazi.SharedKernel.Exceptions;

namespace Tebrazi.Prescriptions.Application.Services;

/// <summary>
/// The JavaScript coercion rules <c>prescriptions.js</c> leans on, in one place.
///
/// <para>Published here rather than re-declared per use-case file because four handler files are
/// written into one namespace in parallel and each of them needs at least the truthiness test.
/// The Appointments and Visits modules each grew four private copies of these (<c>SlotJs</c>,
/// <c>AssistantJs</c>, <c>ApptReadJsValues</c>, <c>VisitAiJsValues</c>) and every copy is
/// <c>internal</c> to its own assembly, so none of them is reachable from here — see
/// docs/prescriptions-surface.md §12.</para>
/// </summary>
public static class RxJs
{
    /// <summary>
    /// JavaScript truthiness for a query-string or body value that arrives as a string:
    /// returns null for null AND for the empty string, so <c>if (visitId)</c> ports as
    /// <c>RxJs.Truthy(visitId) is { } id</c>.
    ///
    /// <para>Needed because ASP.NET binds a present-but-valueless query key
    /// (<c>?visitId=</c>) to <c>""</c>, and an <c>is not null</c> store filter would then match
    /// the empty id and return nothing where Node returns everything.</para>
    /// </summary>
    public static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// JavaScript truthiness for a JSON value. <c>false</c>, <c>0</c>, <c>""</c>, <c>null</c> and
    /// an absent key are falsy; <b>every object and array is truthy, including <c>{}</c> and
    /// <c>[]</c></b>.
    /// </summary>
    public static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => false,
        JsonValueKind.Null => false,
        JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetString() is { Length: > 0 },
        JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
        _ => true
    };

    /// <summary>
    /// <inheritdoc cref="IsTruthy(JsonElement)"/>
    /// <para>A C# null (an absent OR explicitly-null nullable binding) is falsy.</para>
    /// </summary>
    public static bool IsTruthy(JsonElement? value) => value.HasValue && IsTruthy(value.Value);

    /// <summary>
    /// JavaScript's <c>parseInt(value)</c> with NO radix argument, returning the Number it really
    /// produces — a <see cref="double"/>, not an <see cref="int"/>.
    ///
    /// <para>Leading whitespace, then an optional sign, then EITHER a <c>0x</c>/<c>0X</c> prefix
    /// followed by hex digits — V8 auto-detects base 16 when no radix is passed, so
    /// <c>parseInt("0x10")</c> is 16, and <c>-0X1f</c> is -31 — OR a decimal digit PREFIX.
    /// <c>"50abc"</c> is 50, <c>"1e6"</c> is 1 (it stops at the 'e'), <c>"3.9"</c> is 3,
    /// <c>"0x"</c> with no hex digit after it is NaN, <c>"abc"</c> and <c>""</c> are NaN.</para>
    ///
    /// <para><b>Returns a double so that "NaN" stays distinguishable from "a real number too large
    /// for an Int32".</b> <c>parseInt("100000000000")</c> is a finite, TRUTHY Number in Node, and
    /// substituting NaN's fallback for it is the one case where the fallback is wrong. The two
    /// consumers narrow differently and both narrowings are deliberate:
    /// <see cref="ParseInt(string?)"/> for a value Prisma must see as an Int, and a saturating
    /// clamp on <c>GET /interaction-history</c>'s <c>take</c>.</para>
    /// </summary>
    /// <param name="value">The raw query-string or body value.</param>
    /// <returns>The parsed Number, or null for JavaScript's NaN.</returns>
    public static double? ParseIntNumber(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        var i = 0;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;

        var negative = false;
        if (i < value.Length && (value[i] == '+' || value[i] == '-'))
        {
            negative = value[i] == '-';
            i++;
        }

        // The no-radix hex prefix. `parseInt` strips `0x`/`0X` and reads base 16 from there.
        if (i + 1 < value.Length
            && value[i] == '0'
            && (value[i + 1] == 'x' || value[i + 1] == 'X'))
        {
            i += 2;
            var hexStart = i;
            double hex = 0;

            while (i < value.Length && Uri.IsHexDigit(value[i]))
            {
                hex = (hex * 16) + Convert.ToInt32(value[i].ToString(), 16);
                i++;
            }

            // `parseInt("0x")` — a prefix with no digit after it is NaN, not 0.
            if (i == hexStart) return null;

            return negative ? -hex : hex;
        }

        var digitsStart = i;
        while (i < value.Length && char.IsAsciiDigit(value[i])) i++;

        if (i == digitsStart) return null;

        // Accumulated as a double on purpose: the digit run may exceed Int64, and JS would still
        // yield a finite (if imprecisely rounded) Number rather than NaN.
        double magnitude = 0;
        for (var d = digitsStart; d < i; d++) magnitude = (magnitude * 10) + (value[d] - '0');

        return negative ? -magnitude : magnitude;
    }

    /// <summary>
    /// JavaScript's <c>parseInt</c> over a string, narrowed to the Int32 a Prisma <c>Int</c> column
    /// can hold. <c>"50abc"</c> is 50, <c>"1e6"</c> is 1, <c>"3.9"</c> is 3, <c>"0x10"</c> is 16,
    /// <c>"abc"</c> and <c>""</c> are null (NaN).
    ///
    /// <para><b>A value outside Int32 also returns null here</b>, which is not JavaScript's answer
    /// but is the right narrowing for its one consumer: <c>PUT /{id}/dispense</c> feeds the result
    /// straight into Prisma's <c>decrement</c> on an <c>Int</c> column, where Node's out-of-range
    /// Number fails the query exactly as its NaN does. A consumer that needs the true Number must
    /// call <see cref="ParseIntNumber(string?)"/> instead — see
    /// <c>GET /interaction-history</c>.</para>
    ///
    /// <para>Superficially like <c>PageRequest.Parse</c> in the kernel, which keeps its scanner
    /// private, but NOT interchangeable: <c>parseInt(req.query.limit) || 20</c> on
    /// <c>GET /interaction-history</c> has no <c>Math.max</c> and no cap, and a negative value is
    /// meaningful there.</para>
    /// </summary>
    /// <param name="value">The raw query-string or body value.</param>
    /// <returns>The parsed prefix, or null for JavaScript's NaN and for an out-of-Int32 value.</returns>
    public static int? ParseInt(string? value)
    {
        var parsed = ParseIntNumber(value);

        return parsed is >= int.MinValue and <= int.MaxValue ? (int)parsed.Value : null;
    }

    /// <summary>
    /// JavaScript's <c>parseInt</c> over a JSON value, which is what
    /// <c>PUT /{id}/dispense</c> does to each <c>inventoryItems[].quantity</c>
    /// (prescriptions.js:471) — the field is documented as <c>number|string</c> and is never
    /// validated.
    ///
    /// <para>A number is stringified first, exactly as <c>parseInt</c> does, so <c>1.9</c> is 1
    /// and <c>-5</c> is -5. A string takes <see cref="ParseInt(string?)"/>. Booleans, objects,
    /// arrays, null and an absent key are all NaN, i.e. null — matching JS for every case except
    /// a one-element numeric array, where <c>String([5])</c> would give JS a 5; that input is not
    /// reachable from the client and is recorded as a divergence rather than modelled.</para>
    /// </summary>
    /// <returns>The parsed integer, or null for JavaScript's NaN.</returns>
    public static int? ParseInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => ParseInt(value.GetString()),

        // Shortest round-trip formatting, which is also what String(number) produces in V8 for
        // every value a clinic actually sends.
        JsonValueKind.Number => value.TryGetDouble(out var number)
            ? ParseInt(number.ToString("R", CultureInfo.InvariantCulture))
            : null,

        _ => null
    };
}

/// <summary>
/// The error bodies of <c>prescriptions.js</c>, as kernel exceptions.
///
/// <para><b>Every single error body in that file is a bare <c>{ "error": "…" }</c></b> — there is
/// no <c>message</c> key anywhere, and no <c>details</c>. That is why nothing here reaches for
/// <see cref="ValidationException"/> or <see cref="ConflictException"/>: both prepend a generic
/// label ("Validation failed" / "Conflict") and push the real text into a second <c>message</c>
/// key, which is not the shape the React client matches on.</para>
///
/// <para>Published in the shared surface rather than copied per file, because four handler files
/// in one namespace cannot each declare an <c>RxErrors</c>.</para>
/// </summary>
public static class RxErrors
{
    /// <summary>
    /// A bare <c>{ "error": message }</c> at an arbitrary status.
    /// <see cref="BusinessException"/> drops the <c>message</c> key when it equals the error
    /// label, which is what produces the single-key body.
    /// </summary>
    /// <param name="message">The exact literal from the Node route.</param>
    /// <param name="statusCode">The exact status from the Node route.</param>
    public static BusinessException Node(string message, int statusCode)
        => new(message, message, statusCode);

    /// <summary>400 with a bare <c>{ "error": message }</c>.</summary>
    /// <param name="message">The exact literal from the Node route.</param>
    public static BusinessException BadRequest(string message) => Node(message, 400);

    /// <summary>403 with a bare <c>{ "error": message }</c>.</summary>
    /// <param name="message">The exact literal from the Node route.</param>
    public static ForbiddenException Forbidden(string message) => new(message);

    /// <summary>404 with a bare <c>{ "error": message }</c>.</summary>
    /// <param name="message">The exact literal from the Node route.</param>
    public static NotFoundException NotFound(string message) => new(message);

    /// <summary>
    /// 500 with a bare <c>{ "error": message }</c> — each route's OWN named failure literal
    /// ("Failed to sign prescription", "Failed to dispense prescription", …), which the generic
    /// middleware body would not reproduce. Throw this from a per-file catch-all guard, and
    /// rethrow <see cref="AppException"/> untouched so deliberate 400/403/404s keep their bodies.
    /// </summary>
    /// <param name="message">The exact literal from the Node route's catch block.</param>
    public static BusinessException ServerError(string message) => Node(message, 500);
}
