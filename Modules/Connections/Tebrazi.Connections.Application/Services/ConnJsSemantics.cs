using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tebrazi.SharedKernel.Exceptions;

namespace Tebrazi.Connections.Application.Services;

/// <summary>
/// The JavaScript coercion rules <c>connections.js</c> leans on, in one place.
///
/// <para>Published here rather than re-declared per use-case file because FIVE handler files are
/// written into one namespace in parallel, and a second <c>internal static class ConnJs</c> in a
/// sibling file is a build error nobody sees until the solution compiles. The Prescriptions module
/// learned this and published <c>RxJs</c> for the same reason; that class is <c>public</c> but
/// lives in another assembly which this one does not reference, so it is not reachable from
/// here — see docs/connections-surface.md §9.</para>
/// </summary>
public static class ConnJs
{
    /// <summary>
    /// JavaScript truthiness for a value that arrives as a string: returns null for null AND for
    /// the empty string, so <c>if (statusFilter)</c> ports as
    /// <c>ConnJs.Truthy(status) is { } s</c>.
    ///
    /// <para>Needed because ASP.NET binds a present-but-valueless query key (<c>?status=</c>) to
    /// <c>""</c>, and an <c>is not null</c> store filter would then match the empty value and
    /// return nothing where Node returns everything.</para>
    /// </summary>
    /// <param name="value">The raw query-string or body value.</param>
    public static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// JavaScript truthiness for a JSON body value. <c>false</c>, <c>0</c>, <c>""</c>,
    /// <c>null</c> and an ABSENT key are falsy; <b>every object and array is truthy, including
    /// <c>{}</c> and <c>[]</c></b>.
    /// </summary>
    /// <param name="value">The bound JSON value. <see cref="JsonValueKind.Undefined"/> is an absent key.</param>
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
    /// The value when it is a JSON STRING, and null for every other kind — including a truthy
    /// non-string. Used where Node would call a string method (<c>.trim()</c>, <c>.replace()</c>,
    /// <c>.length</c>) on the value, which is a TypeError for a number or an object and therefore
    /// lands in the route's own 500.
    /// </summary>
    /// <param name="value">The bound JSON value.</param>
    public static string? AsString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// <c>value?.trim() || null</c> — the shape <c>POST /clinic-patients</c> applies to
    /// <c>phone</c>, <c>email</c> and <c>notes</c> (connections.js:790-797). Trims, then maps the
    /// empty result to null so it is stored as SQL NULL rather than "".
    ///
    /// <para>.NET's <see cref="string.Trim()"/> and JavaScript's <c>String.prototype.trim</c> agree
    /// on every character a clinic form can produce; they differ only on a handful of exotic
    /// code points, which is recorded rather than modelled.</para>
    /// </summary>
    /// <param name="value">The raw value.</param>
    public static string? TrimToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// <c>phone.replace(/[\s\-]/g, '')</c> — the normalization
    /// <c>POST /api/connections/add-by-phone</c> applies before its lookup and echoes back in
    /// <c>{ found: false, phone }</c> (connections.js:539).
    ///
    /// <para>It strips WHITESPACE and ASCII HYPHEN-MINUS only. A leading <c>+</c>, parentheses and
    /// dots all survive, so <c>"+20 (10) 123-4567"</c> normalizes to <c>"+20(10)1234567"</c> and
    /// will not match a stored <c>"+20101234567"</c>. That is the live behaviour, not an
    /// oversight to improve: the echoed value is on the wire and the client re-sends it to
    /// <c>create-patient</c>.</para>
    ///
    /// <para>The regex <c>\s</c> in JavaScript covers Unicode whitespace plus the line
    /// terminators, which is what <see cref="char.IsWhiteSpace(char)"/> matches here.</para>
    /// </summary>
    /// <param name="phone">The raw phone string. Must not be null — the route's truthiness gate runs first.</param>
    public static string NormalizePhone(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);

        var builder = new StringBuilder(phone.Length);
        foreach (var character in phone)
        {
            if (char.IsWhiteSpace(character) || character == '-') continue;
            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>phone.replace(/[^0-9]/g, '')</c> — a DIFFERENT normalization, used only to build the
    /// placeholder email in <c>POST /create-patient</c>:
    /// <c>patient-&lt;digits&gt;@tebrazi.local</c> (connections.js:646).
    ///
    /// <para>Keeps ASCII digits and nothing else. Deliberately separate from
    /// <see cref="NormalizePhone"/>: confusing the two changes the generated email address, which
    /// becomes the patient's permanent login identifier.</para>
    ///
    /// <para>ASCII digits only — Arabic-Indic digits (٠-٩) do NOT match JavaScript's
    /// <c>[^0-9]</c> class and are stripped, so <see cref="char.IsAsciiDigit(char)"/> is the right
    /// test and <c>char.IsDigit</c> would be wrong.</para>
    /// </summary>
    /// <param name="phone">The raw phone string.</param>
    public static string DigitsOnly(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);

        var builder = new StringBuilder(phone.Length);
        foreach (var character in phone)
        {
            if (char.IsAsciiDigit(character)) builder.Append(character);
        }

        return builder.ToString();
    }
}

/// <summary>
/// The physician invite link and its token, shared verbatim by
/// <c>POST /api/connections/invite-email</c> (connections.js:981-982) and
/// <c>GET /api/connections/invite-link</c> (:1014-1020), plus the QR pairing URL from
/// <c>GET /api/connections/qr-code</c> (:1798-1799).
///
/// <para>Registered as a singleton by <c>AddConnectionsModule</c>, constructed from the
/// configuration key <c>Client:Url</c> (environment variable <c>Client__Url</c>) — the port's
/// equivalent of Node's <c>process.env.CLIENT_URL</c>. The key does not appear in
/// <c>appsettings.json</c>; an unset value falls back exactly as Node does.</para>
///
/// <para><b>⚠ The two fallbacks are DIFFERENT and that is not a typo in the Node source.</b> The
/// invite routes fall back to port <b>5174</b> and the QR route to port <b>5173</b>. Both are
/// reproduced. When <c>Client:Url</c> IS set the difference disappears, because both read the same
/// variable.</para>
/// </summary>
/// <param name="configuredBaseUrl">
/// The configured client origin, or null. An empty string is treated as unset, matching
/// <c>process.env.CLIENT_URL || '…'</c>.
/// </param>
public sealed class ConnClientUrls(string? configuredBaseUrl)
{
    /// <summary>Node's fallback for the two invite routes (connections.js:982, :1019).</summary>
    public const string InviteFallbackBaseUrl = "http://localhost:5174";

    /// <summary>Node's fallback for the QR route (connections.js:1798) — a DIFFERENT port.</summary>
    public const string QrFallbackBaseUrl = "http://localhost:5173";

    private readonly string? _configured = ConnJs.Truthy(configuredBaseUrl);

    /// <summary>The origin the invite links are built on.</summary>
    public string InviteBaseUrl => _configured ?? InviteFallbackBaseUrl;

    /// <summary>The origin the QR pairing link is built on.</summary>
    public string QrBaseUrl => _configured ?? QrFallbackBaseUrl;

    /// <summary>
    /// <c>sha256("physician-invite-" + userId)</c> rendered as LOWERCASE hex and truncated to the
    /// first 16 characters (connections.js:981, :1014).
    ///
    /// <para>It is a deterministic function of the physician's id and nothing else — no secret, no
    /// salt, no expiry — so it is an identifier, not a credential. The signup URL carries the raw
    /// <c>physician</c> id beside it anyway. Do not "harden" it: the token is echoed to the client
    /// as <c>token</c> and appears in links already sent by email.</para>
    /// </summary>
    /// <param name="physicianUserId">The inviting physician's USER id.</param>
    /// <returns>16 lowercase hex characters, WITHOUT the <c>dr-</c> prefix.</returns>
    public static string InviteToken(string physicianUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicianUserId);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"physician-invite-{physicianUserId}"));

        return Convert.ToHexStringLower(digest)[..16];
    }

    /// <summary>
    /// <c>`dr-${token}`</c> — the value <c>GET /invite-link</c> returns as its <c>token</c> field
    /// (connections.js:1025) and the one embedded in the <c>ref</c> query parameter.
    /// </summary>
    /// <param name="physicianUserId">The inviting physician's USER id.</param>
    public static string PrefixedInviteToken(string physicianUserId)
        => $"dr-{InviteToken(physicianUserId)}";

    /// <summary>
    /// <c>`${baseUrl}/signup?ref=dr-${token}&amp;physician=${userId}`</c> (connections.js:982,
    /// :1020). Built by string concatenation exactly as Node does — the id is NOT URL-encoded
    /// there, and encoding it here would change the bytes of every invite email.
    /// </summary>
    /// <param name="physicianUserId">The inviting physician's USER id.</param>
    public string SignupUrl(string physicianUserId)
        => $"{InviteBaseUrl}/signup?ref={PrefixedInviteToken(physicianUserId)}&physician={physicianUserId}";

    /// <summary>
    /// <c>`${baseUrl}/connect/${userId}`</c> — the payload encoded into the QR image and echoed as
    /// <c>connectUrl</c> (connections.js:1799). Note it uses <see cref="QrBaseUrl"/>, with its own
    /// fallback port.
    /// </summary>
    /// <param name="physicianUserId">The physician whose QR code this is.</param>
    public string ConnectUrl(string physicianUserId) => $"{QrBaseUrl}/connect/{physicianUserId}";
}

/// <summary>
/// The error bodies of <c>connections.js</c>, as kernel exceptions.
///
/// <para><b>Every single error body in that file is a bare <c>{ "error": "…" }</c></b> — 1,818
/// lines, 25 routes, and not one <c>message</c> or <c>details</c> key anywhere. That is why
/// nothing here reaches for <see cref="ValidationException"/> or <see cref="ConflictException"/>:
/// both prepend a generic label ("Validation failed" / "Conflict") and push the real text into a
/// second <c>message</c> key, which is not the shape the React client matches on. The 409s in this
/// module — "Already connected", "Connection request already pending" — are bare bodies and must
/// be raised with <see cref="Conflict"/>, never with <c>ConflictException</c>.</para>
///
/// <para>Published in the shared surface rather than copied per file, because five handler files
/// in one namespace cannot each declare a <c>ConnErrors</c>.</para>
/// </summary>
public static class ConnErrors
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
    /// 409 with a bare <c>{ "error": message }</c>. Note this is NOT
    /// <see cref="ConflictException"/>, which would emit
    /// <c>{"error":"Conflict","message":"…"}</c> — a body no route in this file produces.
    /// </summary>
    /// <param name="message">The exact literal from the Node route.</param>
    public static BusinessException Conflict(string message) => Node(message, 409);

    /// <summary>
    /// 500 with a bare <c>{ "error": message }</c> — each route's OWN named failure literal
    /// ("Failed to list connections", "Search failed", …), which the generic middleware body would
    /// not reproduce. Throw this from a per-file catch-all guard, and rethrow
    /// <see cref="AppException"/> untouched so deliberate 400/403/404/409s keep their bodies.
    /// </summary>
    /// <param name="message">The exact literal from the Node route's catch block.</param>
    public static BusinessException ServerError(string message) => Node(message, 500);
}

/// <summary>
/// JavaScript's <c>Date</c> parsing for the one place this module needs it —
/// <c>dateOfBirth ? new Date(dateOfBirth) : null</c> in <c>POST /create-patient</c>
/// (connections.js:695) and <c>POST /clinic-patients</c> (:792).
/// </summary>
public static class ConnDates
{
    /// <summary>
    /// Parses a client-supplied date the way the two create routes need it: an ISO-8601 string
    /// becomes a UTC <see cref="DateTime"/>, anything unparseable becomes null.
    ///
    /// <para><b>Node does not degrade to null — it produces an Invalid Date</b>, which Prisma then
    /// rejects, so the route answers its own 500. Returning null here would silently store no date
    /// where Node fails the request, so the CALLER must treat null-from-a-non-empty-input as that
    /// 500 rather than as "not supplied". Use
    /// <c>ConnDates.TryParse(raw, out var value)</c> and branch on the bool, not on the value.</para>
    /// </summary>
    /// <param name="value">The raw string, or null when the key was absent or falsy.</param>
    /// <param name="parsed">The parsed instant in UTC, or null.</param>
    /// <returns>
    /// True when <paramref name="value"/> was absent/falsy (nothing to parse, and
    /// <paramref name="parsed"/> is null) OR parsed cleanly. False when a non-empty value failed
    /// to parse, which is the route's 500.
    /// </returns>
    public static bool TryParse(string? value, out DateTime? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(value)) return true;

        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }
}
