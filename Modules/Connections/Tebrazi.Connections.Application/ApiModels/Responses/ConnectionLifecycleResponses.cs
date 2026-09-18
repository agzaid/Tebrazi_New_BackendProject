namespace Tebrazi.Connections.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response records for the LINK LIFECYCLE group of server/src/routes/connections.js:
//
//      GET    /api/connections                   (L31-L114)    -> 200, a BARE ARRAY
//      POST   /api/connections/request           (L131-L226)   -> 201 create | 200 re-send
//      PUT    /api/connections/{id}/accept       (L240-L271)   -> 200
//      PUT    /api/connections/{id}/reject       (L277-L296)   -> 200
//      DELETE /api/connections/{id}              (L401-L433)   -> 200
//      GET    /api/connections/patient-summaries (L1042-L1131) -> 200, an OBJECT KEYED BY PATIENT ID
//
//  THE NINE SCALARS. Every response below that carries a connection row opens with the same nine
//  columns in PRISMA DECLARATION ORDER (schema.prisma:508-516):
//
//      id, physicianUserId, patientUserId, subprofileId, status, initiatedBy,
//      connectedAt, createdAt, updatedAt
//
//  Record declaration order IS the JSON key order, so nothing may be reordered, and whatever each
//  route appends — message, physicianUser, patientUser, subprofile — must stay after them, exactly
//  as Prisma places an `include` after the scalars.
//
//  `createdBy` and `updatedBy` are NOT Prisma columns. They exist on the .NET entity because
//  MutableEntity audits every row, and they are deliberately absent from every record here — a
//  client diffing the two backends must not see two extra keys.
//
//  SEVEN ROW-CARRYING RECORDS FOR SIX ROUTES, AND NO SHARED BASE. That is the rule in
//  ConnectionResponseConventions, and it earns its keep here more than anywhere else in the
//  module, because this group alone emits four different shapes of the same row:
//
//      GET / staff + physician branches   -> scalars + patientUser{id,displayName,email,phone}
//                                                    + subprofile{id,name,relation,dateOfBirth,gender}
//      GET / patient branch               -> scalars + physicianUser{id,displayName,email,phone,
//                                                          physicianProfile{specialty,verified}}
//                                                    + subprofile{id,name,relation}   <- NARROWER
//      POST /request (201)                -> scalars + physicianUser{displayName,email}
//                                                    + patientUser{displayName,email}  <- NO id
//      /request re-send, /accept, /reject -> scalars + message
//
//  The two `subprofile` widths are the trap: the staff and physician branches select `dateOfBirth`
//  and `gender` (connections.js:69-71, :87-89) and the patient branch does not (:106-108). A single
//  shared dependant DTO would silently add two keys to "My Doctors", and neither the build nor the
//  client would fail.
// ═════════════════════════════════════════════════════════════════════════════

// ── GET /api/connections — the physician-facing branches ─────────────────────

/// <summary>
/// The <c>patientUser</c> object of the staff and physician branches of <c>GET /api/connections</c>
/// — <c>select: { id, displayName, email, phone }</c> (connections.js:66-67, :84-85).
///
/// <para>The relation is a required FK, so this object is never null on the wire; only
/// <c>email</c> and <c>phone</c> can be.</para>
/// </summary>
/// <param name="Id">The patient's USER id — what the "My Patients" cards link to.</param>
/// <param name="DisplayName">The patient's display name.</param>
/// <param name="Email">The patient's email, or null.</param>
/// <param name="Phone">The patient's phone, or null.</param>
public sealed record ConnLinkListPatientUser(
    string Id,
    string DisplayName,
    string? Email,
    string? Phone);

/// <summary>
/// The WIDE <c>subprofile</c> object — <c>select: { id, name, relation, dateOfBirth, gender }</c>
/// (connections.js:69-71 and :87-89). The staff and physician branches ONLY.
///
/// <para>Null whenever <c>subprofileId</c> is null, which is every parent edge. The physician
/// branch filters those out (<c>subprofileId: null</c>, :82), so on that branch the key is always
/// null; the staff branch does not filter, so a receptionist viewing the same clinic sees
/// dependant edges, with this object populated, that the doctor's own list hides. That asymmetry
/// is the contract, not an oversight.</para>
/// </summary>
/// <param name="Id">The dependant's id.</param>
/// <param name="Name">The dependant's name.</param>
/// <param name="Relation">SELF, SPOUSE, CHILD, PARENT, SIBLING or OTHER — a string, never an enum.</param>
/// <param name="DateOfBirth">The dependant's date of birth, or null.</param>
/// <param name="Gender">MALE, FEMALE or OTHER as a string, or null.</param>
public sealed record ConnLinkListWideDependant(
    string Id,
    string Name,
    string Relation,
    DateTime? DateOfBirth,
    string? Gender);

/// <summary>
/// One element of <c>GET /api/connections</c> as the STAFF branch (connections.js:56-73) and the
/// PHYSICIAN branch (:80-91) emit it. The two branches differ only in their WHERE clause — the
/// physician branch adds <c>subprofileId: null</c>, the staff branch adds the patient-name search
/// — so they genuinely share one element shape, and that is the only sharing in this file.
/// </summary>
/// <param name="Id">The connection id.</param>
/// <param name="PhysicianUserId">The physician end. A USER id.</param>
/// <param name="PatientUserId">The patient end. A USER id.</param>
/// <param name="SubprofileId">The dependant this edge is for, or null for the parent edge.</param>
/// <param name="Status">PENDING, ACCEPTED, REJECTED or REMOVED, as a string.</param>
/// <param name="InitiatedBy">The USER id that created the edge. Not necessarily either party.</param>
/// <param name="ConnectedAt">When the edge was last accepted, or null. Never cleared once set.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">Last modification. Non-null in Node; see the file header.</param>
/// <param name="PatientUser">The patient account. Never null.</param>
/// <param name="Subprofile">The dependant, WIDE shape, or null for a parent edge.</param>
public sealed record ConnLinkListPhysicianViewItem(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    ConnLinkListPatientUser PatientUser,
    ConnLinkListWideDependant? Subprofile);

// ── GET /api/connections — the patient-facing branch ─────────────────────────

/// <summary>
/// The <c>physicianProfile</c> object nested inside <c>physicianUser</c> on the patient branch —
/// <c>select: { specialty, verified }</c> (connections.js:101-103). Two keys, and no <c>id</c>.
///
/// <para>Null when the physician end of the edge has no <c>physician_profiles</c> row. Node's
/// nested include returns <c>null</c> in exactly that case rather than dropping the key, so the
/// key is always present.</para>
/// </summary>
/// <param name="Specialty">The physician's specialty.</param>
/// <param name="Verified">Whether the licence has been verified.</param>
public sealed record ConnLinkListPhysicianProfile(
    string Specialty,
    bool Verified);

/// <summary>
/// The <c>physicianUser</c> object of the patient branch —
/// <c>select: { id, displayName, email, phone, physicianProfile: { specialty, verified } }</c>
/// (connections.js:98-104). This is what the client's "My Doctors" cards read.
/// </summary>
/// <param name="Id">The physician's USER id, not their profile id.</param>
/// <param name="DisplayName">The physician's display name.</param>
/// <param name="Email">The physician's email, or null.</param>
/// <param name="Phone">The physician's phone, or null.</param>
/// <param name="PhysicianProfile">Specialty and verification, or null when there is no profile row.</param>
public sealed record ConnLinkListPhysicianUser(
    string Id,
    string DisplayName,
    string? Email,
    string? Phone,
    ConnLinkListPhysicianProfile? PhysicianProfile);

/// <summary>
/// The NARROW <c>subprofile</c> object — <c>select: { id, name, relation }</c>
/// (connections.js:106-108). The patient branch only.
///
/// <para><b>It is deliberately three keys, not five.</b> The staff and physician branches select
/// <c>dateOfBirth</c> and <c>gender</c> as well; this one does not, and merging the two records
/// would put a dependant's date of birth onto the patient's own "My Doctors" payload where Node
/// never puts it.</para>
/// </summary>
/// <param name="Id">The dependant's id.</param>
/// <param name="Name">The dependant's name.</param>
/// <param name="Relation">SELF, SPOUSE, CHILD, PARENT, SIBLING or OTHER, as a string.</param>
public sealed record ConnLinkListNarrowDependant(
    string Id,
    string Name,
    string Relation);

/// <summary>
/// One element of <c>GET /api/connections</c> as the PATIENT branch emits it
/// (connections.js:93-111).
///
/// <para><b>This branch is the <c>else</c>, not a PATIENT test.</b> Anyone whose <c>userType</c>
/// is not PHYSICIAN — PATIENT, RECEPTIONIST, STAFF, or an unrecognised claim value — lands here
/// once the staff fallback has declined, and is scoped to their OWN user id as the PATIENT end. A
/// receptionist with no clinic header therefore gets their personal doctor list, in practice an
/// empty array.</para>
///
/// <para>Unlike the physician branch there is <b>no <c>subprofileId: null</c> filter</b>, so a
/// patient connected to one doctor for themselves and for two children gets three elements, three
/// cards and three different <c>subprofile</c> values. Do not collapse them.</para>
/// </summary>
/// <param name="Id">The connection id.</param>
/// <param name="PhysicianUserId">The physician end. A USER id.</param>
/// <param name="PatientUserId">The patient end — always the caller on this branch.</param>
/// <param name="SubprofileId">The dependant this edge is for, or null for the caller themselves.</param>
/// <param name="Status">PENDING, ACCEPTED, REJECTED or REMOVED, as a string.</param>
/// <param name="InitiatedBy">The USER id that created the edge.</param>
/// <param name="ConnectedAt">When the edge was last accepted, or null.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">Last modification.</param>
/// <param name="PhysicianUser">The doctor's account plus their profile. Never null.</param>
/// <param name="Subprofile">The dependant, NARROW shape, or null for the caller's own edge.</param>
public sealed record ConnLinkListPatientViewItem(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    ConnLinkListPhysicianUser PhysicianUser,
    ConnLinkListNarrowDependant? Subprofile);

// ── POST /api/connections/request ────────────────────────────────────────────

/// <summary>
/// The two party objects of the <b>201</b> create body —
/// <c>physicianUser: { select: { displayName, email } }</c> and
/// <c>patientUser: { select: { displayName, email } }</c> (connections.js:218-221).
///
/// <para><b>There is no <c>id</c> key.</b> This is the only place in the module where a party
/// object omits it, so a client reading <c>connection.patientUser.id</c> off this response gets
/// <c>undefined</c> from Node and must get nothing here either.</para>
/// </summary>
/// <param name="DisplayName">The party's display name.</param>
/// <param name="Email">The party's email, or null.</param>
public sealed record ConnLinkRequestParty(
    string DisplayName,
    string? Email);

/// <summary>
/// <c>POST /api/connections/request</c> → <b>201</b> (connections.js:207-224), the branch that
/// INSERTS a new edge.
///
/// <para><c>status</c> is always the literal <c>"PENDING"</c> and <c>connectedAt</c> is always
/// null: this is the one route in the module that creates a row needing the other side's consent.
/// <c>initiatedBy</c> is the CALLER even on the staff path, where <c>physicianUserId</c> is the
/// clinic's doctor rather than the caller (:216) — a receptionist's id therefore appears on an
/// edge they are not a party to, and <c>PUT /{id}/accept</c>'s "Cannot accept your own request"
/// check reads exactly that value.</para>
///
/// <para>There is no <c>message</c> key here. The sibling re-send branch has one and answers 200,
/// so the client distinguishes the two by status code and the 201 is load-bearing.</para>
/// </summary>
/// <param name="Id">The new connection id.</param>
/// <param name="PhysicianUserId">The physician end. A USER id.</param>
/// <param name="PatientUserId">The patient end. A USER id.</param>
/// <param name="SubprofileId">From the body's <c>subprofileId || null</c>.</param>
/// <param name="Status">Always <c>"PENDING"</c>.</param>
/// <param name="InitiatedBy">The caller's USER id.</param>
/// <param name="ConnectedAt">Always null on this path.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">Last modification.</param>
/// <param name="PhysicianUser">The physician's display name and email. No id.</param>
/// <param name="PatientUser">The patient's display name and email. No id.</param>
public sealed record ConnLinkRequestCreatedResponse(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    ConnLinkRequestParty PhysicianUser,
    ConnLinkRequestParty PatientUser);

/// <summary>
/// <c>POST /api/connections/request</c> → <b>200</b> (connections.js:200-205), the branch that
/// REVIVES a previously REJECTED edge.
///
/// <para>The bare nine scalars plus <c>message</c>, and <b>no party objects at all</b> — the
/// Prisma <c>update</c> on this path carries no <c>include</c>, so the shape is narrower than the
/// 201 sitting next to it. A shared record would have handed this branch two objects Node does not
/// send.</para>
///
/// <para><b><c>connectedAt</c> survives the revival.</b> The update writes <c>status</c> and
/// <c>initiatedBy</c> and nothing else (:201), so an edge that was once accepted, then rejected,
/// then re-requested goes back to PENDING still carrying the timestamp saying when it was
/// accepted. That looks wrong and is the contract;
/// <see cref="Tebrazi.Connections.Domain.Entities.DoctorPatientConnection.Reopen"/> reproduces it
/// by not touching the column.</para>
/// </summary>
/// <param name="Id">The existing connection id.</param>
/// <param name="PhysicianUserId">The physician end. A USER id.</param>
/// <param name="PatientUserId">The patient end. A USER id.</param>
/// <param name="SubprofileId">Unchanged by this path.</param>
/// <param name="Status">Always <c>"PENDING"</c>.</param>
/// <param name="InitiatedBy">Overwritten with the caller's USER id.</param>
/// <param name="ConnectedAt">NOT cleared. Whatever the row already carried.</param>
/// <param name="CreatedAt">The ORIGINAL creation time, not the re-send time.</param>
/// <param name="UpdatedAt">Last modification.</param>
/// <param name="Message">Always <c>"Connection request re-sent"</c>.</param>
public sealed record ConnLinkRequestResentResponse(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string Message);

// ── PUT /api/connections/{id}/accept ─────────────────────────────────────────

/// <summary>
/// <c>PUT /api/connections/{id}/accept</c> → 200 (connections.js:265-270).
///
/// <para>The nine scalars plus <c>message</c>, with no <c>include</c>: the accepting client gets
/// back ids, not the names it already had on screen.</para>
///
/// <para><c>status</c> is always <c>"ACCEPTED"</c> and <c>connectedAt</c> is always a fresh,
/// non-null timestamp — the route's precondition is <c>status === 'PENDING'</c>, so this is the
/// only path to ACCEPTED in this group. <b>The assignment is unconditional</b>
/// (<c>data: { status, connectedAt: new Date() }</c>, :268), not a <c>||</c> keep. That matters
/// because <c>/reject</c> leaves the old value standing, so a later re-accept must MOVE the
/// timestamp rather than preserve a stale one.</para>
/// </summary>
/// <param name="Id">The connection id.</param>
/// <param name="PhysicianUserId">The physician end. A USER id.</param>
/// <param name="PatientUserId">The patient end. A USER id.</param>
/// <param name="SubprofileId">Unchanged by this route.</param>
/// <param name="Status">Always <c>"ACCEPTED"</c>.</param>
/// <param name="InitiatedBy">Unchanged — still the OTHER party, never the accepter.</param>
/// <param name="ConnectedAt">Freshly stamped. Never null on this response.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">Last modification.</param>
/// <param name="Message">Always <c>"Connection accepted"</c>.</param>
public sealed record ConnLinkAcceptResponse(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string Message);

// ── PUT /api/connections/{id}/reject ─────────────────────────────────────────

/// <summary>
/// <c>PUT /api/connections/{id}/reject</c> → 200 (connections.js:290-295).
///
/// <para>Identical in shape to <see cref="ConnLinkAcceptResponse"/> and deliberately a separate
/// record, because the invariants are opposite and one shared record would let an edit to either
/// route silently reshape the other.</para>
///
/// <para><b>There is NO status precondition on this route</b> and no "cannot reject your own
/// request" gate, so either party can reject an already-ACCEPTED edge, an already-REJECTED one, or
/// one they initiated themselves. And the update writes <c>status</c> ALONE (:293):
/// <b><c>connectedAt</c> is left standing</b>, so this response routinely carries a non-null
/// "when we connected" on a rejected edge. Both facts look like bugs and are the contract.</para>
/// </summary>
/// <param name="Id">The connection id.</param>
/// <param name="PhysicianUserId">The physician end. A USER id.</param>
/// <param name="PatientUserId">The patient end. A USER id.</param>
/// <param name="SubprofileId">Unchanged by this route.</param>
/// <param name="Status">Always <c>"REJECTED"</c>.</param>
/// <param name="InitiatedBy">Unchanged.</param>
/// <param name="ConnectedAt">NOT cleared. Non-null whenever the edge had ever been accepted.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">Last modification.</param>
/// <param name="Message">Always <c>"Connection rejected"</c>.</param>
public sealed record ConnLinkRejectResponse(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string Message);

// ── DELETE /api/connections/{id} ─────────────────────────────────────────────

/// <summary>
/// <c>DELETE /api/connections/{id}</c> → 200 (connections.js:428).
///
/// <para>One key. The row is HARD deleted — <c>prisma.doctorPatientConnection.delete</c>, not a
/// status write — so there is nothing left to echo and no <c>id</c> for the client to reconcile
/// against. That is a real difference from the rest of this port, where
/// <c>DELETE /api/visits/{id}</c> only archives: the model carries no <c>deletedAt</c> column at
/// all, so removing the row is the only disconnection there is, and it revokes the physician's
/// access to the chart.</para>
/// </summary>
/// <param name="Message">Always <c>"Connection removed"</c>.</param>
public sealed record ConnLinkDeleteResponse(string Message);

// ── GET /api/connections/patient-summaries ───────────────────────────────────

/// <summary>
/// One patient's clinical context on the physician dashboard — the VALUE of one key of
/// <c>GET /api/connections/patient-summaries</c> (connections.js:1108-1118).
///
/// <para><b>The response is an OBJECT KEYED BY PATIENT USER ID, not an array</b>, so the handler
/// returns <c>IReadOnlyDictionary&lt;string, ConnLinkPatientSummary&gt;</c> and the client indexes
/// it directly. A caller with no physician profile row, or with no ACCEPTED connections, gets
/// <c>200 {}</c> — never <c>[]</c>, never a 403. Note that <c>JsonSerializerOptions</c> sets
/// <c>PropertyNamingPolicy</c> but NOT <c>DictionaryKeyPolicy</c>, so the user ids stay verbatim
/// while these members camel-case, which is exactly what Node emits.</para>
///
/// <para>Every count here is emitted for every connected patient, including one with no visits at
/// all — zero rather than absent. Build the object from the connection list, never from the keys
/// the batched cross-module reads happen to return.</para>
/// </summary>
/// <param name="LastVisitDate">
/// The most recent visit this physician recorded for this patient, or null. <b>No status
/// filter</b>, so an IN_PROGRESS or CANCELLED visit can be the "last" one.
/// </param>
/// <param name="LastComplaint">
/// That visit's <c>chiefComplaint</c>, or null. Node writes
/// <c>lastVisit?.chiefComplaint || null</c>, so an EMPTY STRING becomes null rather than
/// <c>""</c>.
/// </param>
/// <param name="VisitCount">Every visit this physician recorded for this patient. No status filter.</param>
/// <param name="ConditionsCount">
/// Chronic conditions reached THROUGH A DEPENDANT of this patient. A condition attached directly
/// to the patient profile is not counted, and there is no <c>isActive</c> filter, so a resolved
/// condition still counts.
/// </param>
/// <param name="MedsCount">
/// Current medications by the same dependant-only path and with no active filter — a discontinued
/// medication still counts. The key is <c>medsCount</c>, not <c>medicationsCount</c>.
/// </param>
/// <param name="FamilyCount">
/// The SIZE of the union of two sets: dependants this physician holds an ACCEPTED edge for, and
/// dependants this physician has actually seen in a visit. It already excludes SELF, because a
/// SELF visit carries a null <c>subprofileId</c> and the edge query requires a non-null one.
/// </param>
/// <param name="HasOverdueFollowUp">Whether a due follow-up row exists. Node's <c>!!overdueFollowUp</c>.</param>
/// <param name="FollowUpDate">That row's follow-up date, or null. Non-null exactly when the flag is true.</param>
/// <param name="UnreadMessages">
/// Messages this patient sent this physician that are still unread. <b>Always 0 in this port</b> —
/// the Messages module is not ported and publishes no directory port. A recorded divergence, not
/// an omission.
/// </param>
public sealed record ConnLinkPatientSummary(
    DateTime? LastVisitDate,
    string? LastComplaint,
    int VisitCount,
    int ConditionsCount,
    int MedsCount,
    int FamilyCount,
    bool HasOverdueFollowUp,
    DateTime? FollowUpDate,
    int UnreadMessages);
