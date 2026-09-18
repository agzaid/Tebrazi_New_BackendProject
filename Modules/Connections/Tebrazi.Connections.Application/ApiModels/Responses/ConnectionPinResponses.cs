using System.Text.Json.Serialization;
using Tebrazi.Connections.Domain.Entities;

namespace Tebrazi.Connections.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response records for the PIN / QR pairing group, ported from
//  server/src/routes/connections.js:
//
//      POST /api/connections/generate-pin     (L1515-L1596)
//      POST /api/connections/connect-by-pin   (L1608-L1725)
//      GET  /api/connections/qr-code          (L1793-L1816)
//
//  Every name here carries the ConnPin prefix, because five handler files compile into this one
//  namespace (ConnectionResponseConventions.PinPrefix, docs/connections-surface.md §9).
//
//  Record declaration order IS the JSON key order, and the host's naming policy is camelCase
//  (Program.cs:47), so `ExpiresInSeconds` ships as `expiresInSeconds`.
//
//  TWO SERIALIZATION FACTS THAT ARE LOAD-BEARING IN THIS GROUP:
//
//  1. `DefaultIgnoreCondition` is `Never` (Program.cs:48), so a null member is written as
//     `"key": null` unless the member itself says otherwise. Three members below are Node
//     `undefined` — `physicianName`, `physician.name` and `physician.specialty` are all read
//     through an optional chain (`user?.displayName`, `physician?.physicianProfile?.specialty`)
//     and JSON.stringify DROPS an undefined value rather than emitting null. Those three carry
//     [JsonIgnore(WhenWritingNull)]; nothing else here does, because every other null in this
//     group is a real Prisma NULL that Node emits as `null`.
//
//  2. `ConnectionStatus` is written as its member NAME ("ACCEPTED"), because the host registers a
//     JsonStringEnumConverter with no naming policy (Program.cs:50). Do not restate it as a
//     string member "to be safe" — that would lose the compiler's help on the one place this
//     group overrides the value (see ConnPinConnectionRow.Status).
//
//  ⚠ NOT the Clinics staff pin. `staff_pins` is a different table with a different lifetime and a
//  different meaning (Modules/Clinics/.../StaffPinCommands.cs). Nothing here relates to it.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// <c>POST /api/connections/generate-pin</c> → 200 (connections.js:1589-1594).
///
/// <para>Four keys, and two of them are not what their names suggest. See each member.</para>
/// </summary>
/// <param name="Pin">
/// <c>created.pin</c> — four decimal digits as TEXT, read back off the row that was just written
/// rather than off the local variable. Never zero-padded: generation draws uniformly from
/// 1000-9999, so a leading zero is unreachable.
/// </param>
/// <param name="ExpiresAt">
/// <c>created.expiresAt</c> — the absolute five-minute expiry, an instant and not a duration.
/// </param>
/// <param name="PhysicianName">
/// <b>⚠ The CALLER's display name, not the physician's.</b> Node reads
/// <c>prisma.user.findUnique({ where: { id: userId } })</c> at connections.js:1584 where
/// <c>userId</c> is <c>req.user.id</c> — so on the staff path the PIN belongs to the clinic's
/// physician while this key carries the receptionist's name. Reproduced exactly; it is what the
/// client prints beside the code.
///
/// <para>Null here means the user row could not be resolved, which in Node is <c>undefined</c> and
/// therefore an ABSENT key — hence the ignore condition. An empty display name is NOT absent:
/// Node has no <c>||</c> on this value, so <c>""</c> is emitted as <c>""</c>.</para>
/// </param>
/// <param name="ExpiresInSeconds">
/// The hard-coded literal <c>300</c> (connections.js:1593) — <see cref="ConnectionPin.LifetimeSeconds"/>,
/// not a recomputation of <paramref name="ExpiresAt"/> minus now. The client seeds its countdown
/// from it (ConnectionsPage.jsx:317, <c>res.data.expiresInSeconds || 300</c>), so the two values
/// can drift by the round-trip time and that is the live behaviour.
/// </param>
public sealed record ConnPinGenerateResponse(
    string Pin,
    DateTime ExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PhysicianName,
    int ExpiresInSeconds);

/// <summary>
/// The <c>doctor_patient_connections</c> row as <c>POST /api/connections/connect-by-pin</c>
/// serializes it — the nine Prisma scalars in Prisma's own declaration order
/// (schema.prisma:508-517), nested under the <c>connection</c> key.
///
/// <para><b>No relations.</b> Node's create at connections.js:1676-1683 passes no <c>include</c>,
/// and the two "already existed" branches echo the row exactly as <c>findFirst</c> returned it —
/// so unlike <c>POST /api/connections/request</c>'s 201, there is no <c>physicianUser</c> and no
/// <c>patientUser</c> here. The physician's details travel in the sibling
/// <see cref="ConnPinPhysicianCard"/> instead, under two different key names.</para>
///
/// <para><b>CreatedBy / UpdatedBy are deliberately absent.</b> They are columns on the .NET table
/// and not on Node's, so putting them on the wire would add keys the Node backend never sends.</para>
/// </summary>
/// <param name="Id">The connection's own id.</param>
/// <param name="PhysicianUserId">The physician end. A USER id, taken from the redeemed pin.</param>
/// <param name="PatientUserId">The patient end. A USER id — always the caller on this route.</param>
/// <param name="SubprofileId">
/// Always <c>null</c> on a row this route creates: the pairing edge is the account holder's own.
/// An edge found by the "already connected" probe also has a null subprofile, because that probe
/// passes <c>subprofileId: null</c> as a VALUE (connections.js:1639).
/// </param>
/// <param name="Status">
/// <b>The one member this group ever overrides.</b> In the re-accept branch Node answers
/// <c>{ ...existing, status: 'ACCEPTED' }</c> (connections.js:1660) — the row as it was read
/// BEFORE the update, with only this field replaced. So the handler snapshots the row, then
/// rewrites this member with <c>with { Status = ConnectionStatus.ACCEPTED }</c>, leaving
/// <paramref name="ConnectedAt"/> and <paramref name="UpdatedAt"/> at their pre-update values.
/// </param>
/// <param name="InitiatedBy">
/// The PATIENT's user id on a row this route creates (connections.js:1681) — the patient typed the
/// code, so the patient initiated. On an echoed pre-existing row it is whatever created that row.
/// </param>
/// <param name="ConnectedAt">
/// <b>Stale by design in the re-accept branch.</b> The update writes <c>connectedAt: new Date()</c>
/// (connections.js:1657) but the response spreads the pre-update object, so this carries the OLD
/// stamp — null when the edge had never been accepted, or the earlier acceptance instant when it
/// had been accepted and then rejected. The database and the response disagree, and reproducing
/// that is the contract.
/// </param>
/// <param name="CreatedAt">Row creation. Untouched by every branch of this route.</param>
/// <param name="UpdatedAt">
/// Nullable here because <c>MutableEntity</c> declares it so; Prisma's <c>@updatedAt</c> is
/// non-null and <c>BaseDbContext.SaveChangesAsync</c> stamps it on INSERT as well as UPDATE, so a
/// freshly created row carries a value. Like <paramref name="ConnectedAt"/> it is the PRE-update
/// value in the re-accept branch.
/// </param>
public sealed record ConnPinConnectionRow(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    ConnectionStatus Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt)
{
    /// <summary>
    /// Projects the entity onto the wire shape. Call it BEFORE mutating the entity wherever Node
    /// spreads a pre-update object — see <see cref="Status"/>.
    /// </summary>
    /// <param name="connection">The entity, tracked or not.</param>
    /// <returns>The nine scalars, in Prisma's declaration order.</returns>
    public static ConnPinConnectionRow From(DoctorPatientConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new ConnPinConnectionRow(
            connection.Id,
            connection.PhysicianUserId,
            connection.PatientUserId,
            connection.SubprofileId,
            connection.Status,
            connection.InitiatedBy,
            connection.ConnectedAt,
            connection.CreatedAt,
            connection.UpdatedAt);
    }
}

/// <summary>
/// The <c>physician</c> object on the two SUCCESS branches of <c>connect-by-pin</c>
/// (connections.js:1661, :1719-1722).
///
/// <para><b>Two keys, and neither is spelled the way the rest of the port spells it</b> —
/// <c>name</c> rather than <c>displayName</c>, <c>specialty</c> flattened out of
/// <c>physicianProfile</c>. The client renders them as
/// <c>Dr. {pinSuccess.physician.name} — {pinSuccess.physician.specialty}</c>
/// (ConnectionsPage.jsx:707).</para>
///
/// <para>Both members come off optional chains, so an unresolvable user or a user with no
/// physician profile yields <c>undefined</c> and Node DROPS the key. Both therefore carry the
/// ignore condition, and <c>physician: {}</c> is a reachable — if unlikely — body.</para>
/// </summary>
/// <param name="Name">
/// <c>physician?.displayName</c>. Absent when the physician's user row cannot be resolved.
/// </param>
/// <param name="Specialty">
/// <c>physician?.physicianProfile?.specialty</c>. Absent when the user row OR the physician profile
/// cannot be resolved — the chain is two links, so a missing user suppresses this key even if a
/// profile row exists.
/// </param>
public sealed record ConnPinPhysicianCard(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Specialty);

/// <summary>
/// <c>POST /api/connections/connect-by-pin</c> → 200, on the two branches that CHANGE something:
/// the fresh connection (connections.js:1717-1724) and the re-accept of a PENDING or REJECTED edge
/// (:1658-1662).
///
/// <para>Both emit the same three keys in the same order, which is why they share one record.
/// What differs is inside <see cref="Connection"/>: the created row is exactly what was written,
/// the re-accepted row is the PRE-update snapshot with its status overridden. See
/// <see cref="ConnPinConnectionRow.Status"/>.</para>
///
/// <para><b><c>alreadyConnected</c> is ABSENT here</b>, not false. The client tests
/// <c>pinSuccess.alreadyConnected ? … : 'Connected Successfully!'</c> (ConnectionsPage.jsx:703),
/// so absent and false read identically — but the byte contract is absence, and that is what a
/// separate record buys over one nullable member.</para>
/// </summary>
/// <param name="Message">The literal <c>"Connected successfully!"</c>, exclamation mark included.</param>
/// <param name="Connection">The edge, nested. See <see cref="ConnPinConnectionRow"/>.</param>
/// <param name="Physician">
/// The display card. Always present as an object on this branch, even when both of its members are
/// dropped.
/// </param>
public sealed record ConnPinConnectedResponse(
    string Message,
    ConnPinConnectionRow Connection,
    ConnPinPhysicianCard Physician);

/// <summary>
/// <c>POST /api/connections/connect-by-pin</c> → 200, on the branch where an ACCEPTED edge already
/// existed (connections.js:1666-1670).
///
/// <para>Three keys, a DIFFERENT set and a different order from
/// <see cref="ConnPinConnectedResponse"/>: <c>message</c>, <c>alreadyConnected</c>,
/// <c>connection</c> — and <b>no <c>physician</c> key at all</b>, so the client's success panel
/// shows the headline without the "Dr. X — Cardiology" line under it.</para>
///
/// <para><b>The PIN is still burned on this branch.</b> connections.js:1644-1647 marks it used
/// before the status test, so a code redeemed against an existing connection is spent and the
/// patient cannot reuse it.</para>
/// </summary>
/// <param name="Message">The literal <c>"Already connected"</c> — no exclamation mark on this one.</param>
/// <param name="AlreadyConnected">The literal <c>true</c>. Node never sends it as false.</param>
/// <param name="Connection">
/// The existing edge, echoed verbatim as <c>findFirst</c> returned it. Nothing on the row is
/// modified by this branch, so nothing here is stale.
/// </param>
public sealed record ConnPinAlreadyConnectedResponse(
    string Message,
    bool AlreadyConnected,
    ConnPinConnectionRow Connection);

/// <summary>
/// <c>GET /api/connections/qr-code</c> → 200 (connections.js:1809-1812).
///
/// <para><b>It is JSON, not an image.</b> The route name says "qr-code" and the response is an
/// <c>application/json</c> object carrying a data URL — the same class of trap as
/// <c>GET /api/prescriptions/{id}/pdf</c>, which returns <c>text/html</c>. The client assigns
/// <see cref="QrCode"/> straight into an <c>&lt;img src&gt;</c> (ConnectionsPage.jsx:911) and never
/// decodes it.</para>
/// </summary>
/// <param name="QrCode">
/// A <c>data:image/png;base64,…</c> URL, produced here by
/// <c>ConnQrCodeRenderer.RenderPngDataUrl</c> and in Node by the <c>qrcode</c> npm package.
///
/// <para><b>The base64 payload is NOT byte-identical to Node's and cannot be</b> — two encoders
/// choose different mask patterns and emit different PNG chunk layouts. The media type, the
/// <c>data:</c> URL shape and the decoded string are identical, which is the whole of what the
/// client and a scanner observe. Recorded in docs/connections-surface.md §11.7.</para>
/// </param>
/// <param name="ConnectUrl">
/// <c>`${CLIENT_URL || 'http://localhost:5173'}/connect/${userId}`</c> — the string encoded into
/// the image, echoed so the client can offer it as a copyable link.
///
/// <para>⚠ The fallback origin is port <b>5173</b>, where the two invite routes fall back to
/// <b>5174</b>. That difference is in the Node source and is not a typo; see
/// <c>ConnClientUrls</c>.</para>
/// </param>
public sealed record ConnPinQrCodeResponse(
    string QrCode,
    string ConnectUrl);
