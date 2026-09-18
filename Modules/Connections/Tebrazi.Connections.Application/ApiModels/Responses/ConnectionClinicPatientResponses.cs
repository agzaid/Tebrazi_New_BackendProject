using System.Text.Json.Serialization;

namespace Tebrazi.Connections.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response records for the WALK-IN CHART group, ported from
//  server/src/routes/connections.js:
//
//      POST   /api/connections/clinic-patients        (L747-L807) -> 201
//      GET    /api/connections/clinic-patients        (L813-L849) -> 200, a BARE ARRAY
//      DELETE /api/connections/clinic-patients/{id}   (L860-L893) -> 200
//
//  The rows these three serialize belong to `clinic_patients`, which is owned by the CLINICS
//  module. Nothing here re-declares that entity: the handlers read `ClinicPatientRecord` off
//  `IClinicPatientDirectory` and project it, once per endpoint, into the shape that endpoint
//  emits.
//
//  -- The chart's key order ---------------------------------------------------
//  `res.json(patient)` and `res.json(patients)` serialize Prisma rows with no `select`, so the
//  body is every ClinicPatient SCALAR in PRISMA DECLARATION ORDER (schema.prisma:569-596):
//
//      id, physicianUserId, clinicId, name, phone, email, dateOfBirth, gender, nationalId,
//      bloodType, notes, allergies, chronicConditions, linkedUserId, linkedAt, isActive,
//      lastVisitDate, createdAt, updatedAt
//
//  Note `notes` sits BEFORE `allergies` and `chronicConditions` - the schema groups it with the
//  clinical-context block, not with the free-text tail. RECORD DECLARATION ORDER IS THE JSON KEY
//  ORDER (the host's naming policy is camelCase, Program.cs:47), so nothing below may be
//  reordered.
//
//  -- Why the create row and the list row are SEPARATE records ---------------
//  They differ by exactly one key. The list query carries
//  `include: { _count: { select: { visits: true } } }` (L841) and the create does not (L785-L800),
//  so only the list elements end in `_count`. A single shared chart record would have to emit that
//  key on both - `DefaultIgnoreCondition = JsonIgnoreCondition.Never` (Program.cs:48) means a null
//  would serialize as `"_count": null` rather than vanish - and the create body would gain a key
//  Node never sends. Hence two records, per the module rule that every endpoint owns its own DTO.
//
//  -- What is deliberately NOT here ------------------------------------------
//  * No `createdBy` / `updatedBy`. Those two columns exist on the .NET table and not on Prisma's,
//    and `ClinicPatientRecord` already omits them so a handler cannot reach them.
//  * No `nationalId` WRITE path, even though the column is emitted. `POST /clinic-patients`
//    destructures ten keys from the body (L750) and `nationalId` is not one of them, so the value
//    ConnectionsPage.jsx:381 sends is silently dropped and the column comes back null on a fresh
//    chart. The key is on the wire; the value never is.
//  * No `visits` or `appointments` arrays. The list `include` asks for the COUNT only.
//  * No pagination envelope. `GET /clinic-patients` is a bare array - `r.data || []` at
//    ConnectionsPage.jsx:185, VisitsPage.jsx:203 and AppointmentsPage.jsx:220 all index it
//    directly.
//
//  -- DateTime note, true of the whole port ----------------------------------
//  Node emits `2026-09-11T00:00:00.000Z` and System.Text.Json writes `2026-09-11T00:00:00Z`. That
//  affects `dateOfBirth`, `linkedAt`, `lastVisitDate`, `createdAt` and `updatedAt` here exactly as
//  it affects every other timestamp in the port; it is open decision #1 in docs/PORT-STATUS.md and
//  is deliberately NOT worked around per endpoint.
//
//  `updatedAt` carries a second, narrower gap: Prisma's `@updatedAt` is non-null and stamped on
//  create, while `MutableEntity` leaves it null until the first modification - so a chart that has
//  just been created serializes `"updatedAt": null` here and a timestamp in Node. Recorded on
//  `ClinicPatientRecord` and repeated here because it is visible on the 201 body.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The chart as <c>POST /api/connections/clinic-patients</c> returns it, nested under
/// <c>patient</c> (connections.js:802).
///
/// <para>Every scalar of the created Prisma row, in schema order, and nothing else. Five of them
/// can only ever hold their defaults on this route because it never writes them:
/// <c>nationalId</c> (not destructured from the body), <c>linkedUserId</c> and <c>linkedAt</c>
/// (set by <c>POST /link-account</c>, which is out of scope), <c>lastVisitDate</c> (written when a
/// visit closes) and <c>isActive</c>, which the route hard-codes to <c>true</c>.</para>
/// </summary>
/// <param name="Id">The new chart id.</param>
/// <param name="PhysicianUserId">
/// The resolved owner's USER id - the caller on the physician path, the clinic's physician on the
/// staff path. Never the receptionist who made the call.
/// </param>
/// <param name="ClinicId">
/// The clinic the chart is filed under, or <b>null</b>. Null is a normal outcome on the physician
/// path: <c>physician?.clinics?.[0]?.id || null</c> (connections.js:764) leaves it null for a
/// physician with no clinic and for one with no physician profile at all.
/// </param>
/// <param name="Name">The submitted name, trimmed (<c>name.trim()</c>, connections.js:789).</param>
/// <param name="Phone">
/// <c>phone?.trim() || null</c> - trimmed, and the empty result stored as null rather than "".
/// </param>
/// <param name="Email">
/// <c>email?.trim() || null</c>. Never validated as an address and never checked for duplicates:
/// unlike <c>POST /create-patient</c>, this route has no "already exists" probe, so two charts may
/// carry the same email.
/// </param>
/// <param name="DateOfBirth">The parsed date, or null when the key was absent or falsy.</param>
/// <param name="Gender">
/// The <c>Gender</c> member NAME (<c>MALE</c>, <c>FEMALE</c>, <c>OTHER</c>) or null. A string
/// outside that set is not stored - Node hands it straight to a Prisma enum column, which rejects
/// it into the route's own 500.
/// </param>
/// <param name="NationalId">
/// Always null on a chart created through this route: the body key is never read. Emitted because
/// Node's unselective <c>res.json(patient)</c> emits it.
/// </param>
/// <param name="BloodType">
/// <c>bloodType || null</c>. Free text in the schema, NOT an enum - untrimmed and unvalidated, so
/// <c>"  O+  "</c> is stored with its spaces while <c>phone</c> beside it is trimmed.
/// </param>
/// <param name="Notes">
/// <c>notes?.trim() || null</c>. The free-text clinical note;
/// <c>PUT /clinic-patients/{id}/notes</c> would edit it and is out of scope.
/// </param>
/// <param name="Allergies">
/// Always an array, never null - <c>Array.isArray(allergies) ? allergies : []</c>
/// (connections.js:795), so a string, an object or an absent key all produce <c>[]</c>.
/// </param>
/// <param name="ChronicConditions">Same rule as <paramref name="Allergies"/>.</param>
/// <param name="LinkedUserId">Always null here; set only by <c>POST /link-account</c>.</param>
/// <param name="LinkedAt">Always null here.</param>
/// <param name="IsActive">Hard-coded <c>true</c> by the route (connections.js:798).</param>
/// <param name="LastVisitDate">Always null on a fresh chart.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">
/// <b>Null on a freshly created chart in this port and a timestamp in Node</b> - see the file
/// header.
/// </param>
public sealed record ConnClinicPatientCreatedChart(
    string Id,
    string PhysicianUserId,
    string? ClinicId,
    string Name,
    string? Phone,
    string? Email,
    DateTime? DateOfBirth,
    string? Gender,
    string? NationalId,
    string? BloodType,
    string? Notes,
    IReadOnlyList<string> Allergies,
    IReadOnlyList<string> ChronicConditions,
    string? LinkedUserId,
    DateTime? LinkedAt,
    bool IsActive,
    DateTime? LastVisitDate,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>
/// <c>POST /api/connections/clinic-patients</c> -> <b>201</b> (connections.js:802).
///
/// <para>Three keys in this order: <c>success</c>, <c>patient</c>, <c>message</c>. The client
/// reads <c>res.data.patient.name</c> (ConnectionsPage.jsx:380) and
/// <c>res.data.patient?.name</c> (AssistantDashboardPage.jsx:294) and ignores the other two, but
/// all three are on the wire.</para>
/// </summary>
/// <param name="Success">
/// The literal <c>true</c>. No path through this route returns <c>false</c> - every failure is a
/// status code with a bare <c>{ error }</c> body instead.
/// </param>
/// <param name="Patient">The created chart. Never null.</param>
/// <param name="Message">
/// <c>`${patient.name} added successfully`</c> - built from the STORED name, so it carries the
/// trimmed value rather than whatever the client sent.
/// </param>
public sealed record ConnClinicPatientCreateResponse(
    bool Success,
    ConnClinicPatientCreatedChart Patient,
    string Message);

/// <summary>
/// The <c>_count</c> object Prisma appends to each element of <c>GET /clinic-patients</c>
/// (connections.js:841).
/// </summary>
/// <param name="Visits">
/// EVERY visit row pointing at this chart. No status filter and no <c>deletedAt</c> filter, so
/// cancelled, archived and soft-deleted visits are all counted. A chart with none reports
/// <c>0</c>, never a missing key.
/// </param>
public sealed record ConnClinicPatientVisitCounts(int Visits);

/// <summary>
/// One element of <c>GET /api/connections/clinic-patients</c> (connections.js:844) - the same
/// nineteen scalars as <see cref="ConnClinicPatientCreatedChart"/> with <c>_count</c> appended.
///
/// <para>Prisma places an <c>include</c>d relation AFTER every scalar, so <c>_count</c> is the
/// last key. The property name is spelled explicitly because the camelCase policy would otherwise
/// emit <c>count</c>, which is not what the client's <c>cp._count?.visits</c> reads.</para>
///
/// <para>The array is scoped to ONE physician and to <c>isActive: true</c>, ordered by
/// <c>name</c> ascending (connections.js:838-842). Inactive charts are invisible here, and nothing
/// in <c>connections.js</c> ever sets <c>isActive</c> to false - the column is written
/// <c>true</c> at creation and never touched again, so in practice the filter excludes
/// nothing.</para>
/// </summary>
/// <param name="Id">
/// The chart id. The client uses it for <c>DELETE /clinic-patients/{id}</c>
/// (ConnectionsPage.jsx:1177) and as a visit target.
/// </param>
/// <param name="PhysicianUserId">
/// The owning physician's USER id - always the caller's own id on the physician path, and the
/// clinic physician's on the staff path.
/// </param>
/// <param name="ClinicId">
/// The clinic the chart is filed under, or null. <b>Not a filter:</b> the query scopes by
/// physician only, so a physician working at two clinics sees both clinics' charts in one array,
/// and so does a receptionist who resolved that physician through one of them.
/// </param>
/// <param name="Name">The chart's name. Also the sort key.</param>
/// <param name="Phone">Nullable.</param>
/// <param name="Email">Nullable.</param>
/// <param name="DateOfBirth">Nullable.</param>
/// <param name="Gender">The <c>Gender</c> member name, or null.</param>
/// <param name="NationalId">Nullable. No route in this group writes it.</param>
/// <param name="BloodType">Free text, nullable.</param>
/// <param name="Notes">
/// Free-text clinical notes, <b>returned in full to every caller who can reach the list</b> -
/// including a receptionist who resolved the physician through the staff fallback. Node applies no
/// projection here, so narrowing it would change the wire.
/// </param>
/// <param name="Allergies">Free-text list. Empty, never null.</param>
/// <param name="ChronicConditions">Free-text list. Empty, never null.</param>
/// <param name="LinkedUserId">The Tebrazi account this chart was matched to, or null.</param>
/// <param name="LinkedAt">When it was matched, or null.</param>
/// <param name="IsActive">Always <c>true</c> - it is the query's own filter.</param>
/// <param name="LastVisitDate">Denormalised marker, written when a visit closes.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">Nullable in this port; see the file header.</param>
/// <param name="Count">The <c>_count</c> object. Never null.</param>
public sealed record ConnClinicPatientListItem(
    string Id,
    string PhysicianUserId,
    string? ClinicId,
    string Name,
    string? Phone,
    string? Email,
    DateTime? DateOfBirth,
    string? Gender,
    string? NationalId,
    string? BloodType,
    string? Notes,
    IReadOnlyList<string> Allergies,
    IReadOnlyList<string> ChronicConditions,
    string? LinkedUserId,
    DateTime? LinkedAt,
    bool IsActive,
    DateTime? LastVisitDate,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    [property: JsonPropertyName("_count")] ConnClinicPatientVisitCounts Count);

/// <summary>
/// <c>DELETE /api/connections/clinic-patients/{id}</c> -> <b>200</b> (connections.js:888).
///
/// <para>Two keys, and <b>the deleted row is never echoed</b> - the name in
/// <paramref name="Message"/> is the only trace of it. The client ignores the body entirely
/// (ConnectionsPage.jsx:1177 awaits the call and refetches), so this exists for parity.</para>
/// </summary>
/// <param name="Success">The literal <c>true</c>.</param>
/// <param name="Message">
/// <c>`${patient.name} deleted successfully`</c>, built from the chart that was loaded by the
/// ownership check BEFORE the deletion ran.
/// </param>
public sealed record ConnClinicPatientDeleteResponse(
    bool Success,
    string Message);
