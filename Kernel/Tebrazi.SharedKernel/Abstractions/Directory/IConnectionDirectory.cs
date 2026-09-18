namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The published read port onto <c>doctor_patient_connections</c>, supplied by Connections.
///
/// <para>It exists because the doctor-patient edge is read from outside the module that owns it.
/// <c>GET /api/patients/dashboard</c> projects its <c>doctors[]</c> array and
/// <c>stats.totalDoctors</c> straight off this table (patients.js:702-726, :855), and
/// <c>GET /api/patients/family/{patientUserId}</c> uses the existence of an ACCEPTED edge as the
/// authorization test that lets a physician see a patient's dependants (patients.js:162-172).
/// Neither may touch the Connections context to do it.</para>
///
/// <para>Read-only by design, matching <see cref="IIdentityDirectory"/> and
/// <see cref="IClinicPatientDirectory"/>: edges are written only by the Connections module's own
/// use cases, because creating one grants a physician access to a chart.</para>
/// </summary>
public interface IConnectionDirectory
{
    /// <summary>
    /// The patient's ACCEPTED edges, newest connection first — the exact query behind
    /// <c>GET /api/patients/dashboard</c>'s <c>doctors[]</c> (patients.js:702-717):
    /// <c>where: { patientUserId, status: 'ACCEPTED' }, orderBy: { connectedAt: 'desc' }</c>.
    ///
    /// <para><b>Only status is filtered.</b> There is no <c>subprofileId: null</c> clause, so a
    /// patient connected to one doctor for themselves AND for two children gets THREE rows, each
    /// rendering as its own dashboard card with a different <c>forMember</c>. Do not collapse
    /// them.</para>
    ///
    /// <para><b>The row carries IDS, not names</b>, matching <c>PatientVisitRow</c> and
    /// <c>PatientAppointmentRow</c>. The Connections context owns these two tables only — there is
    /// no <c>users</c>, <c>physician_profiles</c> or <c>family_subprofiles</c> table to join — so
    /// the caller finishes each row itself:</para>
    /// <list type="bullet">
    /// <item><c>name</c> ← <see cref="IIdentityDirectory.GetUsersAsync"/> on
    /// <see cref="PatientDoctorRow.PhysicianUserId"/>, then <c>DisplayName</c>.</item>
    /// <item><c>specialty</c> and <c>verified</c> ←
    /// <see cref="IIdentityDirectory.GetPhysicianByUserIdAsync"/>, with Node's fallbacks
    /// <c>|| 'General'</c> and <c>|| false</c> applied for a user with no physician profile
    /// (patients.js:722-723).</item>
    /// <item><c>forMember</c> ← the dependant's NAME when
    /// <see cref="PatientDoctorRow.SubprofileId"/> is set, else the literal <c>"Self"</c>
    /// (patients.js:725). Patients owns <c>family_subprofiles</c>, so it resolves that one
    /// in-module rather than through a port.</item>
    /// </list>
    ///
    /// <para><c>stats.totalDoctors</c> comes out of this same list and nothing else: it is
    /// <c>rows.Select(r =&gt; r.PhysicianUserId).Distinct().Count()</c>, reproducing
    /// <c>new Set(connections.map(c =&gt; c.physicianUser.id)).size</c> (patients.js:855). It is
    /// NOT <c>rows.Count</c> — the family case above is exactly where the two differ.</para>
    ///
    /// <para>⚠ <b>NULL ordering differs between the two databases.</b> Postgres sorts NULLs FIRST
    /// under <c>DESC</c>, SQL Server sorts them LAST, so an ACCEPTED edge with no
    /// <c>connectedAt</c> lands at the opposite end of the array. Every accepting code path stamps
    /// the column, so the case is only reachable through direct data edits; the implementation
    /// takes SQL Server's natural order rather than emulating Postgres.</para>
    /// </summary>
    /// <param name="patientUserId">The patient account holder's USER id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching edges, empty list when there are none. Never null.</returns>
    Task<IReadOnlyList<PatientDoctorRow>> ListAcceptedDoctorsForPatientAsync(
        string patientUserId, CancellationToken ct = default);

    /// <summary>
    /// True when an ACCEPTED edge joins this physician to this patient — the
    /// <c>findFirst({ physicianUserId, patientUserId, status: 'ACCEPTED' })</c> existence test at
    /// patients.js:162-168, which decides whether a physician may list the patient's family
    /// members.
    ///
    /// <para>Both arguments are USER ids. There is no <c>subprofileId</c> clause, so a physician
    /// connected only for one dependant passes this test and sees the WHOLE family — that is the
    /// Node behaviour, and narrowing it here would silently revoke access the live backend
    /// grants.</para>
    /// </summary>
    /// <param name="physicianUserId">The requesting physician's USER id.</param>
    /// <param name="patientUserId">The patient account holder's USER id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> HasAcceptedConnectionAsync(
        string physicianUserId, string patientUserId, CancellationToken ct = default);
}

/// <summary>
/// One ACCEPTED edge, projected for the patient dashboard. Three columns, because three is what
/// the dashboard's <c>doctors[]</c> element needs that this table actually holds — everything else
/// on that element is resolved by the caller through other ports.
/// </summary>
/// <param name="PhysicianUserId">
/// The doctor's USER id, which becomes the element's <c>id</c> and is the key for the display
/// name, specialty and verified flag.
/// </param>
/// <param name="SubprofileId">
/// The dependant this edge covers, or null for the account holder's own edge. Drives
/// <c>forMember</c>: a name when set, the literal <c>"Self"</c> when not.
/// </param>
/// <param name="ConnectedAt">
/// When the edge was accepted, emitted verbatim as <c>connectedAt</c>. Nullable because the column
/// is, even on an ACCEPTED row.
/// </param>
public sealed record PatientDoctorRow(
    string PhysicianUserId,
    string? SubprofileId,
    DateTime? ConnectedAt);
