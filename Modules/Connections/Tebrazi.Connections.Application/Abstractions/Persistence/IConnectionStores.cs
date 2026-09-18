using Tebrazi.Connections.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Persistence;

namespace Tebrazi.Connections.Application.Abstractions.Persistence;

/// <summary>The Connections module's unit of work. Handlers inject THIS, never the concrete context.</summary>
public interface IConnectionsDbContext : IDbContext;

/// <summary>
/// The filter behind the three branches of <c>GET /api/connections</c> (connections.js:55-74,
/// :81-92, :95-111) and the physician sweep in <c>GET /patient-summaries</c> (:1048-1051).
///
/// <para>A NULL member is not applied, matching how the Node handler spreads its <c>where</c>
/// object key by key. Pass null, not <c>""</c>: ASP.NET binds a present-but-valueless query key
/// (<c>?status=</c>) to the empty string, and every test below is <c>is not null</c>, so
/// <c>""</c> would filter on the empty value and return nothing where Node returns everything.
/// Map <c>""</c> to null with <c>ConnJs.Truthy(...)</c> in the handler.</para>
/// </summary>
/// <param name="PhysicianUserId">Exact equality on <c>physician_user_id</c>. A USER id.</param>
/// <param name="PatientUserId">Exact equality on <c>patient_user_id</c>. A USER id.</param>
/// <param name="Status">
/// Exact equality on <c>status</c>.
///
/// <para>⚠ Node applies <c>where.status = req.query.status</c> RAW (connections.js:40) — the
/// string is never validated against the enum. In Postgres an unknown value raises a Prisma
/// validation error and the route answers its 500. A caller here must therefore parse the query
/// value itself and decide what an unparseable one does; this filter only speaks
/// <see cref="ConnectionStatus"/>.</para>
/// </param>
/// <param name="SubprofileIdIsNull">
/// When true, adds <c>subprofile_id IS NULL</c> — the physician branch of <c>GET /</c> only
/// (connections.js:82), which is what hides dependant edges from "My Patients" so a family of
/// four shows as one row. When null, no predicate is added; <b>false is treated as null</b>,
/// because no Node query asks for "subprofile is NOT null" through this path
/// (<see cref="IDoctorPatientConnectionStore.ListAcceptedSubprofileIdsAsync"/> covers that case).
/// </param>
/// <param name="PatientUserIdIn">
/// Restricts to a SET of patient user ids. This is how the staff branch's
/// <c>patientUser: { displayName: { contains, mode: 'insensitive' } }</c> (connections.js:59-63)
/// is reproduced: <c>users</c> lives in the Identity context, so the handler resolves matching
/// user ids through <c>IIdentityDirectory.SearchUsersAsync</c> first and passes them here.
///
/// <para><b>An EMPTY collection is a real filter meaning "no user matched the name", and returns
/// nothing.</b> Treating it as "unfiltered" would list the physician's whole patient book for a
/// search term that matched nobody.</para>
/// </param>
public sealed record ConnectionFilter(
    string? PhysicianUserId = null,
    string? PatientUserId = null,
    ConnectionStatus? Status = null,
    bool? SubprofileIdIsNull = null,
    IReadOnlyCollection<string>? PatientUserIdIn = null);

/// <summary>
/// One row of the connection-status enrichment <c>GET /api/connections/search</c> performs
/// (connections.js:497-505). Deliberately not the entity: the Node <c>select</c> is exactly these
/// three columns.
/// </summary>
/// <param name="PhysicianUserId">The physician end of the edge.</param>
/// <param name="PatientUserId">The patient end of the edge.</param>
/// <param name="Status">The edge's status, which becomes <c>connectionStatus</c> on the wire.</param>
public sealed record ConnectionEdge(
    string PhysicianUserId,
    string PatientUserId,
    ConnectionStatus Status);

/// <summary>
/// Reads and writes over <c>doctor_patient_connections</c>.
///
/// <para><b>No method here filters a soft-delete column, because there is none.</b> The Prisma
/// model has no <c>deletedAt</c> field (schema.prisma:507-530) and the string never occurs in
/// <c>connections.js</c>. Both delete routes issue a HARD <c>prisma…delete</c>
/// (connections.js:385, :427). See the comment on <c>ConnectionsDbContext</c>.</para>
/// </summary>
public interface IDoctorPatientConnectionStore
{
    /// <summary>
    /// Tracked single row by id, for a handler about to modify or delete it. A bare lookup with no
    /// ownership predicate — <c>PUT /{id}/accept</c> (connections.js:245),
    /// <c>PUT /{id}/reject</c> (:282) and <c>DELETE /{id}</c> (:406) all read first and check the
    /// caller afterwards, which is why 404 precedes 403 on all three.
    /// </summary>
    /// <param name="id">The connection's own id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DoctorPatientConnection?> GetForUpdateAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The whole filtered set, ordered <c>created_at</c> DESC — which is
    /// <c>orderBy: { createdAt: 'desc' }</c> on all three branches of <c>GET /</c>
    /// (connections.js:73, :91, :110).
    ///
    /// <para><c>GET /patient-summaries</c> uses the same method with
    /// <c>new ConnectionFilter(PhysicianUserId: caller, Status: ACCEPTED)</c>; its Node query has
    /// no <c>orderBy</c> and it does not matter, because the handler immediately reduces the rows
    /// to a DISTINCT set of patient ids (connections.js:1053).</para>
    /// </summary>
    /// <param name="filter">The predicate set. See <see cref="ConnectionFilter"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DoctorPatientConnection>> ListAsync(
        ConnectionFilter filter, CancellationToken ct = default);

    /// <summary>
    /// The <c>findFirst</c> keyed on the full triple, with <paramref name="subprofileId"/>
    /// compared as a VALUE — null matches only rows whose <c>subprofile_id</c> is NULL, never
    /// "any subprofile".
    ///
    /// <para>Three call sites, each relying on exactly that:</para>
    /// <list type="bullet">
    /// <item><c>POST /request</c> (connections.js:189-195) —
    /// <c>subprofileId: subprofileId || null</c>, so an absent body field probes the parent
    /// edge.</item>
    /// <item><c>POST /assign-subprofile</c> — called twice: once with
    /// <c>subprofileId: null, status: ACCEPTED</c> to prove the parent connection exists
    /// (:329-331), then once with the real subprofile id and no status to detect a duplicate
    /// assignment (:337-339).</item>
    /// <item><c>DELETE /unassign-subprofile/{subprofileId}/{physicianUserId}</c> (:380-382).</item>
    /// <item><c>POST /connect-by-pin</c> (:1638-1640) — explicitly <c>subprofileId: null</c>, so a
    /// patient who is connected only for a dependant still gets a parent edge created.</item>
    /// </list>
    ///
    /// <para>There is no ordering: the composite unique key makes at most one row match.</para>
    /// </summary>
    /// <param name="physicianUserId">The physician end. A USER id.</param>
    /// <param name="patientUserId">The patient end. A USER id.</param>
    /// <param name="subprofileId">The dependant, or null for the parent edge. Compared as a value.</param>
    /// <param name="status">Narrows to one status. Null applies no status predicate.</param>
    /// <param name="tracked">
    /// True returns a TRACKED entity for a handler that will mutate or delete it (the
    /// re-request branch of <c>POST /request</c>, and unassign). False returns an untracked read.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<DoctorPatientConnection?> FindPairAsync(
        string physicianUserId,
        string patientUserId,
        string? subprofileId,
        ConnectionStatus? status = null,
        bool tracked = false,
        CancellationToken ct = default);

    /// <summary>
    /// The LOOSER pair lookup: <c>{ physicianUserId, patientUserId }</c> with <b>no</b>
    /// <c>subprofileId</c> key at all, so it matches the parent edge OR any dependant edge —
    /// whichever the database returns first.
    ///
    /// <para>Deliberately separate from <see cref="FindPairAsync"/>, because the difference is
    /// wire-visible. <c>POST /add-by-phone</c> (connections.js:556-558) and
    /// <c>POST /link-account</c> (:939-941) omit the key, so a physician who is connected only to
    /// the patient's CHILD is reported as "already connected" to the parent and no parent edge is
    /// created. <c>POST /connect-by-pin</c> passes <c>subprofileId: null</c> and therefore does
    /// create one. Reproduce each route's own choice.</para>
    ///
    /// <para>Untracked and unordered; the caller only reads <c>Status</c> and existence. Ordered
    /// by <c>created_at</c> ascending so the "whichever row" is at least deterministic here,
    /// where Postgres leaves it to physical order.</para>
    /// </summary>
    /// <param name="physicianUserId">The physician end. A USER id.</param>
    /// <param name="patientUserId">The patient end. A USER id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DoctorPatientConnection?> FindAnyPairAsync(
        string physicianUserId, string patientUserId, CancellationToken ct = default);

    /// <summary>
    /// The id-is-actually-a-user-id fallback in <c>DELETE /api/connections/{id}</c>
    /// (connections.js:411-418): when no connection has that id, look for an edge joining the
    /// CALLER to the user whose id was passed, in EITHER direction.
    ///
    /// <para>Matches <c>OR: [{ physicianUserId: caller, patientUserId: other },
    /// { patientUserId: caller, physicianUserId: other }]</c> with no subprofile key, so it can
    /// return a dependant edge. Tracked, because the caller deletes the row it gets. Ordered by
    /// <c>created_at</c> ascending for determinism; Node leaves it to physical order.</para>
    /// </summary>
    /// <param name="callerUserId">The authenticated caller's USER id.</param>
    /// <param name="otherUserId">The id from the route, treated as the other party's USER id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DoctorPatientConnection?> FindForUpdateByEitherSideAsync(
        string callerUserId, string otherUserId, CancellationToken ct = default);

    /// <summary>
    /// The enrichment sweep behind <c>GET /api/connections/search</c> (connections.js:497-505):
    /// every edge joining <paramref name="lookupUserId"/> to any of
    /// <paramref name="counterpartUserIds"/>, in either direction, projected to three columns.
    ///
    /// <para><paramref name="lookupUserId"/> is the PHYSICIAN's user id on the staff path, not the
    /// staff member's — connections.js:496 computes <c>actingAsPhysicianId || userId</c> precisely
    /// so a receptionist sees the doctor's connection badges.</para>
    ///
    /// <para>An EMPTY <paramref name="counterpartUserIds"/> returns an empty list. Note the Node
    /// handler then matches each user against the FIRST edge whose either end is that user
    /// (:508), which is why this returns both ends rather than a keyed dictionary — the pairing
    /// logic, quirks included, stays in the handler.</para>
    /// </summary>
    /// <param name="lookupUserId">The acting user — physician user id on the staff path.</param>
    /// <param name="counterpartUserIds">The ids from the user search. Empty returns empty.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ConnectionEdge>> ListEdgesWithAsync(
        string lookupUserId,
        IReadOnlyCollection<string> counterpartUserIds,
        CancellationToken ct = default);

    /// <summary>
    /// The dependant edges a physician holds, grouped by patient — the
    /// <c>{ physicianUserId, patientUserId, subprofileId: { not: null }, status: 'ACCEPTED' }</c>
    /// half of <c>GET /patient-summaries</c>'s <c>familyCount</c> (connections.js:1079-1082).
    ///
    /// <para>Batched across every connected patient instead of the per-patient query Node issues
    /// inside <c>Promise.allSettled</c>, so a physician with 200 patients makes one round trip
    /// rather than 200. The handler unions each patient's set with the visited-subprofile set from
    /// <c>IVisitDirectory</c> and takes the size, exactly as :1089 does.</para>
    ///
    /// <para>A patient with no dependant edges is ABSENT from the dictionary, not present with an
    /// empty list — read it with <c>GetValueOrDefault</c>. Empty
    /// <paramref name="patientUserIds"/> returns an empty dictionary.</para>
    /// </summary>
    /// <param name="physicianUserId">The physician's USER id.</param>
    /// <param name="patientUserIds">The patients to cover. Empty returns empty.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ListAcceptedSubprofileIdsAsync(
        string physicianUserId,
        IReadOnlyCollection<string> patientUserIds,
        CancellationToken ct = default);

    void Add(DoctorPatientConnection connection);

    /// <summary>
    /// HARD delete — <c>prisma.doctorPatientConnection.delete</c>, which is what both
    /// <c>DELETE /{id}</c> (connections.js:427) and
    /// <c>DELETE /unassign-subprofile/…</c> (:385) issue. There is no soft-delete column on this
    /// model, so there is no softer option; removing the row revokes the physician's access to the
    /// chart.
    /// </summary>
    /// <param name="connection">A TRACKED entity, from <see cref="GetForUpdateAsync"/>,
    /// <see cref="FindForUpdateByEitherSideAsync"/> or <see cref="FindPairAsync"/> with
    /// <c>tracked: true</c>.</param>
    void Remove(DoctorPatientConnection connection);
}

/// <summary>
/// Reads and writes over <c>connection_pins</c> — the five-minute pairing codes behind
/// <c>POST /generate-pin</c> and <c>POST /connect-by-pin</c>.
///
/// <para><b>Not the clinic staff pin.</b> <c>staff_pins</c> belongs to the Clinics module
/// (<c>StaffPinCommands.cs</c>) and unlocks a shared terminal. Different table, different
/// purpose.</para>
///
/// <para>"Redeemable" throughout means <c>usedAt IS NULL AND expiresAt &gt; now</c>, the filter
/// both Node routes apply verbatim (connections.js:1549-1550, :1561, :1621-1622). The comparison
/// is strictly greater-than, which is what makes the force-expire write effective.</para>
/// </summary>
public interface IConnectionPinStore
{
    /// <summary>
    /// Every redeemable pin a physician currently holds, TRACKED so the caller can
    /// <c>ForceExpire</c> them. Reproduces the <c>updateMany</c> at connections.js:1546-1553 as a
    /// load-then-write, because the entity carries the expiry rule.
    /// </summary>
    /// <param name="physicianUserId">The pin owner's USER id.</param>
    /// <param name="now">The instant "expired" is measured against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ConnectionPin>> ListRedeemableForUpdateAsync(
        string physicianUserId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// True when SOME redeemable pin already carries this code, anywhere in the table — the
    /// uniqueness probe inside <c>POST /generate-pin</c>'s generation loop
    /// (connections.js:1560-1562). Note the check is GLOBAL, not per physician: two doctors cannot
    /// hold the same live code, which is what lets <c>connect-by-pin</c> resolve a bare four-digit
    /// string to one physician.
    /// </summary>
    /// <param name="pin">The candidate code.</param>
    /// <param name="now">The instant "expired" is measured against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> AnyRedeemableAsync(string pin, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// The redemption lookup for <c>POST /connect-by-pin</c> (connections.js:1618-1624), TRACKED
    /// so the caller can <c>MarkUsed</c> it.
    ///
    /// <para>Matches on the code alone plus the redeemable window — there is no physician or
    /// clinic predicate, so the code IS the credential. The comparison is ordinal and
    /// case-sensitivity is irrelevant for four digits, but the column is text and a body value of
    /// <c>"0042"</c> will simply not match a generated pin, because generation never produces a
    /// leading zero.</para>
    ///
    /// <para>Ordered by <c>created_at</c> DESC so the NEWEST live pin wins if two ever collide.
    /// Node's <c>findFirst</c> has no <c>orderBy</c> and would take physical order; the collision
    /// is unreachable while <see cref="AnyRedeemableAsync"/> is honoured, and a deterministic
    /// choice is safer than the planner's.</para>
    /// </summary>
    /// <param name="pin">The code the patient typed.</param>
    /// <param name="now">The instant "expired" is measured against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConnectionPin?> FindRedeemableForUpdateAsync(
        string pin, DateTime now, CancellationToken ct = default);

    void Add(ConnectionPin pin);
}
