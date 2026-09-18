using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Connections.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>DoctorPatientConnection</c> model (table
/// <c>doctor_patient_connections</c>) — the social graph edge between a physician account and a
/// patient account, optionally narrowed to one of the patient's family dependants.
///
/// <para>Either party may initiate; <see cref="InitiatedBy"/> records which, and the ACCEPT route
/// refuses the initiator so a request cannot self-approve. Several routes skip the handshake
/// entirely and insert an already-ACCEPTED row (<c>add-by-phone</c>, <c>create-patient</c>,
/// <c>assign-subprofile</c>, <c>connect-by-pin</c>, <c>link-account</c>) — there is no state
/// machine forcing PENDING first.</para>
///
/// <para><b>The row is the edge and the edge is the permission.</b> A physician's access to a
/// patient's chart is decided by the presence of an ACCEPTED row here, so deleting one revokes
/// clinical access. Both delete routes are HARD deletes: there is no <c>deletedAt</c> column on
/// this model and nothing in <c>connections.js</c> mentions one.</para>
///
/// <para><b>⚠ Cascade safety, carried over from the Prisma model's own comment
/// (schema.prisma:518-520).</b> There, both User relations are <c>onDelete: Cascade</c> and the
/// schema warns: the app only ever SOFT-deletes users (<c>active = false</c>), and hard-deleting
/// a User would destroy every connection and with it every clinical link. This port honours the
/// intent by declaring NO database foreign key at all on
/// <see cref="PhysicianUserId"/>/<see cref="PatientUserId"/>/<see cref="SubprofileId"/> — those
/// rows live in the Identity and Patients contexts, which this module may not map, so there is no
/// cascade path for a hard delete to travel down. The relationship is enforced by the published
/// ports, not by the database. See <c>ConnectionsDbContext</c>.</para>
/// </summary>
public sealed class DoctorPatientConnection : MutableEntity<string>
{
    private DoctorPatientConnection() { }

    /// <summary>The physician side. A USER id, never a physician-profile id.</summary>
    public string PhysicianUserId { get; private set; } = null!;

    /// <summary>The patient side — the account holder, even when the edge is for a dependant.</summary>
    public string PatientUserId { get; private set; } = null!;

    /// <summary>
    /// The family dependant this edge covers, or null for the account holder's own edge.
    ///
    /// <para>Null is a VALUE in the composite unique key, not an absence: one physician/patient
    /// pair may hold one parent edge (null) plus one edge per dependant.</para>
    ///
    /// <para>⚠ <b>PostgreSQL and SQL Server disagree about NULL in a unique index</b>, and this
    /// column is where it shows. Postgres treats NULLs as distinct, so Node's
    /// <c>@@unique([physicianUserId, patientUserId, subprofileId])</c> does NOT actually prevent a
    /// second parent edge for the same pair; SQL Server treats them as equal and would reject it.
    /// The EF configuration therefore FILTERS the unique index to non-null subprofiles and leaves
    /// parent edges unconstrained, which is the behaviour Node has. See
    /// <c>ConnectionConfigurations.cs</c>.</para>
    /// </summary>
    public string? SubprofileId { get; private set; }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.PENDING;

    /// <summary>
    /// The USER id of whoever created or last re-sent the request. Read by
    /// <c>PUT /{id}/accept</c>, which answers <c>400 {"error":"Cannot accept your own request"}</c>
    /// when it equals the caller. Note the staff path writes the STAFF member's id here even
    /// though <see cref="PhysicianUserId"/> is the clinic's physician (connections.js:207, :218).
    /// </summary>
    public string InitiatedBy { get; private set; } = null!;

    /// <summary>
    /// When the edge became ACCEPTED. Null while PENDING.
    ///
    /// <para><b>Never cleared.</b> <c>PUT /{id}/reject</c> writes only <c>status</c>
    /// (connections.js:289-292), so a rejected edge that was once accepted keeps its original
    /// stamp, and a re-request (<see cref="Reopen"/>) keeps it too. The patient dashboard orders
    /// doctors by this column, so the value is wire-visible.</para>
    /// </summary>
    public DateTime? ConnectedAt { get; private set; }

    /// <summary>
    /// The raw factory. Prefer <see cref="CreatePending"/> or <see cref="CreateAccepted"/> — every
    /// route in <c>connections.js</c> creates one of those two shapes and nothing else.
    /// </summary>
    /// <param name="physicianUserId">The physician side's USER id.</param>
    /// <param name="patientUserId">The patient side's USER id.</param>
    /// <param name="initiatedBy">The USER id of whoever is sending the request.</param>
    /// <param name="status">The status to insert with.</param>
    /// <param name="subprofileId">The dependant, or null for the account holder's own edge.</param>
    /// <param name="connectedAt">
    /// The acceptance stamp. Passed explicitly rather than derived from
    /// <paramref name="status"/> because the two are independent columns in Node.
    /// </param>
    public static DoctorPatientConnection Create(
        string physicianUserId,
        string patientUserId,
        string initiatedBy,
        ConnectionStatus status,
        string? subprofileId = null,
        DateTime? connectedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicianUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(patientUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatedBy);

        return new DoctorPatientConnection
        {
            Id = Guid.NewGuid().ToString(),
            PhysicianUserId = physicianUserId,
            PatientUserId = patientUserId,
            SubprofileId = subprofileId,
            Status = status,
            InitiatedBy = initiatedBy,
            ConnectedAt = connectedAt
        };
    }

    /// <summary>
    /// The handshake path: a PENDING edge with no <c>connectedAt</c>, which
    /// <c>POST /api/connections/request</c> inserts (connections.js:212-224).
    /// </summary>
    /// <param name="physicianUserId">The physician side's USER id.</param>
    /// <param name="patientUserId">The patient side's USER id.</param>
    /// <param name="initiatedBy">The caller's USER id — the staff member's, on the staff path.</param>
    /// <param name="subprofileId">The dependant, or null. Node passes <c>subprofileId || null</c>.</param>
    public static DoctorPatientConnection CreatePending(
        string physicianUserId,
        string patientUserId,
        string initiatedBy,
        string? subprofileId = null)
        => Create(physicianUserId, patientUserId, initiatedBy, ConnectionStatus.PENDING, subprofileId);

    /// <summary>
    /// The no-handshake path: an ACCEPTED edge stamped <c>connectedAt = now</c>. Used by
    /// <c>assign-subprofile</c> (connections.js:344-352), <c>add-by-phone</c> (:571-579),
    /// <c>create-patient</c> (:701-709) and <c>connect-by-pin</c> (:1675-1683).
    /// </summary>
    /// <param name="physicianUserId">The physician side's USER id.</param>
    /// <param name="patientUserId">The patient side's USER id.</param>
    /// <param name="initiatedBy">
    /// The USER id recorded as initiator. It is NOT always the caller: <c>create-patient</c>
    /// records the PHYSICIAN's id even when a staff member made the call (connections.js:706).
    /// </param>
    /// <param name="subprofileId">The dependant, or null.</param>
    public static DoctorPatientConnection CreateAccepted(
        string physicianUserId,
        string patientUserId,
        string initiatedBy,
        string? subprofileId = null)
        => Create(
            physicianUserId, patientUserId, initiatedBy,
            ConnectionStatus.ACCEPTED, subprofileId, DateTime.UtcNow);

    /// <summary>
    /// <c>PUT /api/connections/{id}/accept</c> (connections.js:262-265):
    /// <c>{ status: 'ACCEPTED', connectedAt: new Date() }</c>.
    ///
    /// <para>No guard here — the route's own PENDING precondition and its
    /// "cannot accept your own request" test run in the handler, because both produce specific
    /// wire bodies. <c>connectedAt</c> is overwritten unconditionally, so re-accepting moves the
    /// stamp.</para>
    /// </summary>
    public void Accept()
    {
        Status = ConnectionStatus.ACCEPTED;
        ConnectedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// <c>PUT /api/connections/{id}/reject</c> (connections.js:289-292):
    /// <c>{ status: 'REJECTED' }</c> and nothing else.
    ///
    /// <para><b>It does not clear <see cref="ConnectedAt"/></b>, and it has NO status
    /// precondition — an ACCEPTED edge can be rejected straight to REJECTED while keeping the
    /// timestamp that says when it was accepted. That asymmetry with <see cref="Accept"/> is the
    /// contract.</para>
    /// </summary>
    public void Reject() => Status = ConnectionStatus.REJECTED;

    /// <summary>
    /// The re-request branch of <c>POST /api/connections/request</c> (connections.js:205-208):
    /// an existing REJECTED edge is updated to <c>{ status: 'PENDING', initiatedBy: userId }</c>.
    ///
    /// <para>Reachable only from REJECTED — the ACCEPTED and PENDING cases answer 409 before this
    /// point — but no guard is applied here, matching the unconditional Prisma update.
    /// <see cref="ConnectedAt"/> is left alone, so a previously-accepted-then-rejected edge goes
    /// back to PENDING still carrying its old acceptance stamp.</para>
    /// </summary>
    /// <param name="initiatedBy">The re-requesting caller's USER id.</param>
    public void Reopen(string initiatedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatedBy);
        Status = ConnectionStatus.PENDING;
        InitiatedBy = initiatedBy;
    }

    /// <summary>
    /// True when <paramref name="userId"/> is either end of the edge — the
    /// <c>conn.physicianUserId !== userId &amp;&amp; conn.patientUserId !== userId</c> test that
    /// guards accept, reject and delete (connections.js:254, :285, :423).
    /// </summary>
    /// <param name="userId">The caller's USER id.</param>
    public bool InvolvesUser(string userId)
        => PhysicianUserId == userId || PatientUserId == userId;
}

/// <summary>
/// Port of the Prisma <c>ConnectionStatus</c> enum (schema.prisma:1119-1124). Member names and
/// order match exactly; it persists and serializes as its NAME, never as an int.
///
/// <para><b><see cref="REMOVED"/> is declared and never written.</b> No route in
/// <c>connections.js</c> assigns it — disconnection is a hard <c>delete</c>, not a status change.
/// It exists so the column's domain matches Prisma's and so a legacy row carrying it still
/// materialises.</para>
/// </summary>
public enum ConnectionStatus
{
    PENDING,
    ACCEPTED,
    REJECTED,
    REMOVED
}
