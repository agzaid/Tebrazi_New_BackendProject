using Tebrazi.Connections.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Connections.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response records for the two DEPENDANT-EDGE endpoints, ported from
//  server/src/routes/connections.js:
//
//      POST   /api/connections/assign-subprofile                               (L310-L369) -> 201
//      DELETE /api/connections/unassign-subprofile/{subprofileId}/{physicianUserId}
//                                                                              (L375-L391) -> 200
//
//  They are each other's inverse and they are NOT symmetric on the wire: the assign returns the
//  whole created row with two embedded objects, the unassign returns a bare { message }. Node
//  never echoes the deleted row.
//
//  -- The assign body, key by key --------------------------------------------
//  `res.status(201).json(connection)` (L364) serializes a Prisma `create` with an `include`, so
//  the body is the nine DoctorPatientConnection scalars in PRISMA DECLARATION ORDER
//  (schema.prisma:507-517) - id, physicianUserId, patientUserId, subprofileId, status,
//  initiatedBy, connectedAt, createdAt, updatedAt - followed by the two included relations in
//  include order (L353-L361): `physicianUser`, then `subprofile`.
//
//  RECORD DECLARATION ORDER IS THE JSON KEY ORDER. Nothing below may be reordered, and the two
//  objects must stay last.
//
//  -- What is deliberately NOT here ------------------------------------------
//  * No `patientUser`. The include selects the physician side only, even though the caller IS the
//    patient - the client already knows who it is.
//  * `physicianUser` is THREE keys: { id, displayName, physicianProfile: { specialty } }
//    (L354-L358). No email, no phone, no `verified`. This is a NARROWER projection than the one
//    `GET /` serves on its patient branch, which adds email, phone and `verified` - hence a
//    separate record rather than a shared physician DTO. See docs/connections-surface.md 10.12.
//  * `physicianProfile` is ONE key: { specialty }. Not the profile id, not the licence number.
//  * `subprofile` is THREE keys: { id, name, relation } (L360). No dateOfBirth and no gender,
//    unlike the staff and physician branches of `GET /`.
//  * No `message` key on either route's success body - assign has none at all, unassign has
//    NOTHING BUT one.
//  * No `createdBy` / `updatedBy`. Those columns exist on the .NET table and not on Node's, and
//    they must never reach the wire.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The <c>physicianProfile</c> object nested inside <c>physicianUser</c> on
/// <c>POST /api/connections/assign-subprofile</c> — and it is <b>one key</b>.
/// <c>physicianProfile: { select: { specialty: true } }</c> (connections.js:357).
///
/// <para><b>The whole object is null when the physician user has no profile row.</b>
/// <c>User.physicianProfile</c> is an optional one-to-one in Prisma, so
/// <c>"physicianProfile": null</c> is a body this route really can produce — this route never
/// verifies that the assignee is a physician at all, it only requires an ACCEPTED parent edge, and
/// nothing stops that edge from naming a user with no profile.</para>
///
/// <para><c>specialty</c> itself is a required column, so it is non-null whenever the object
/// exists.</para>
/// </summary>
public sealed record ConnSubprofileAssignPhysicianProfile(
    string Specialty);

/// <summary>
/// The <c>physicianUser</c> object of <c>POST /api/connections/assign-subprofile</c>
/// (connections.js:354-359).
///
/// <para>Exactly three keys, in this order: <c>id</c>, <c>displayName</c>,
/// <c>physicianProfile</c>. Do not widen it to match <c>GET /</c>'s physician projection — that one
/// carries <c>email</c>, <c>phone</c> and <c>verified</c> as well, and the client's doctor cards
/// read different fields from each.</para>
///
/// <para><c>displayName</c> is modelled as nullable purely as a .NET safety valve.
/// <c>physicianUser</c> is a REQUIRED relation in Prisma, so Node's include can never produce a
/// missing user — the foreign key would have refused the insert. In this port the users table
/// lives in another module's context and the lookup is a separate read, so a user that has
/// vanished between the parent edge being accepted and this call resolves to null. Emitting
/// <c>null</c> there is preferred over failing a request whose row is otherwise valid; see the
/// handler.</para>
/// </summary>
public sealed record ConnSubprofileAssignPhysicianUser(
    string Id,
    string? DisplayName,
    ConnSubprofileAssignPhysicianProfile? PhysicianProfile);

/// <summary>
/// The <c>subprofile</c> object of <c>POST /api/connections/assign-subprofile</c>
/// (connections.js:360): <c>{ id, name, relation }</c>.
///
/// <para><b>Never null on this route.</b> The handler has already 404'd on a subprofile the caller
/// does not own, so by the time the row is created the dependant is known to exist — unlike the
/// generic connection row, where <c>subprofileId</c> is nullable and the object would be too.</para>
///
/// <para><c>relation</c> stays a STRING on the wire — <c>SELF | SPOUSE | CHILD | PARENT |
/// SIBLING | OTHER</c> — and there is <b>no <c>dateOfBirth</c> and no <c>gender</c></b>. The staff
/// and physician branches of <c>GET /</c> select those two extra fields; this route does not,
/// which is why the dependant projection is not shared.</para>
/// </summary>
public sealed record ConnSubprofileAssignSubprofile(
    string Id,
    string Name,
    string Relation);

/// <summary>
/// <c>POST /api/connections/assign-subprofile</c> → <b>201</b> (connections.js:344-364).
///
/// <para><b>The row is created ACCEPTED, with no handshake.</b> The patient is already connected
/// to this doctor for themselves — that ACCEPTED parent edge is the route's precondition — so
/// extending the link to a dependant needs no second consent. <c>status</c> is therefore always
/// the literal <c>"ACCEPTED"</c> and <c>connectedAt</c> is always a fresh timestamp, never null
/// (L349-L351).</para>
///
/// <para><b><c>initiatedBy</c> is always the PATIENT's user id</b> (L350), never the physician's,
/// because this route is only ever called by the account holder. <c>patientUserId</c> is the same
/// value. Contrast <c>create-patient</c>, which records the physician's id even when staff called
/// it.</para>
///
/// <para><b><c>subprofileId</c> is never null here</b>, which is what distinguishes this row from
/// the parent edge it was derived from. The composite unique key is
/// <c>(physicianUserId, patientUserId, subprofileId)</c>, so this is a genuinely NEW row that
/// coexists with the parent edge — the assign does not move, mutate or reuse it.</para>
///
/// <para>The status is <b>201</b>, and the client's <c>ConnectionsPage</c> / <c>FamilyPage</c>
/// discard the body entirely and re-fetch, so every key below is fidelity rather than a live
/// dependency. It still has to be right: the same pages read these shapes from <c>GET /</c>.</para>
/// </summary>
public sealed record ConnSubprofileAssignResponse(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    ConnSubprofileAssignPhysicianUser PhysicianUser,
    ConnSubprofileAssignSubprofile Subprofile)
{
    /// <summary>
    /// Projects the saved row plus the two lookups the Prisma <c>include</c> performed inline.
    /// Call it only AFTER <c>SaveChangesAsync</c>, so <c>createdAt</c> and <c>updatedAt</c> carry
    /// the stamps <c>BaseDbContext</c> applies — Prisma's <c>@updatedAt</c> is set on create too,
    /// so <c>updatedAt</c> is non-null on this path in both backends.
    /// </summary>
    public static ConnSubprofileAssignResponse From(
        DoctorPatientConnection connection,
        UserSummary? physicianUser,
        PhysicianSummary? physicianProfile,
        SubprofileSummary subprofile)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(subprofile);

        return new ConnSubprofileAssignResponse(
            connection.Id,
            connection.PhysicianUserId,
            connection.PatientUserId,
            connection.SubprofileId,
            connection.Status.ToString(),
            connection.InitiatedBy,
            connection.ConnectedAt,
            connection.CreatedAt,
            connection.UpdatedAt,
            new ConnSubprofileAssignPhysicianUser(
                connection.PhysicianUserId,
                physicianUser?.DisplayName,
                physicianProfile is null
                    ? null
                    : new ConnSubprofileAssignPhysicianProfile(physicianProfile.Specialty)),
            new ConnSubprofileAssignSubprofile(
                subprofile.Id,
                subprofile.Name,
                subprofile.Relation));
    }
}

/// <summary>
/// <c>DELETE /api/connections/unassign-subprofile/{subprofileId}/{physicianUserId}</c> → 200
/// (connections.js:386): <c>res.json({ message: 'Doctor unassigned from family member' })</c>.
///
/// <para><b>One key, and it is not <c>success</c>.</b> Several routes in this router answer
/// <c>{ success, ... }</c>; this one does not, and the deleted row is not echoed. The literal is
/// fixed and is never interpolated with the doctor's or the dependant's name — the client supplies
/// its own toast text.</para>
///
/// <para>Its own record rather than a shared acknowledgement type, because <c>DELETE /{id}</c> in
/// the sibling link group answers a DIFFERENT literal through the same shape, and one shared
/// record would let an edit to either silently change the other.</para>
/// </summary>
public sealed record ConnSubprofileUnassignResponse(
    string Message)
{
    /// <summary>The one literal this route can answer (connections.js:386), verbatim.</summary>
    public const string UnassignedMessage = "Doctor unassigned from family member";

    /// <summary>The route's only success body.</summary>
    public static ConnSubprofileUnassignResponse Unassigned { get; } = new(UnassignedMessage);
}
