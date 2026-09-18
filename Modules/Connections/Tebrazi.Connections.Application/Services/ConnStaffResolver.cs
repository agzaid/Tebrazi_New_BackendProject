using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Connections.Application.Services;

/// <summary>
/// How far the staff-context resolution got. <b>Every route maps these to a different wire
/// body</b>, which is why the resolver reports an outcome instead of throwing.
/// </summary>
public enum ConnStaffOutcome
{
    /// <summary>No clinic id was supplied — neither in the body nor in <c>X-Clinic-Id</c>.</summary>
    NoClinicId,

    /// <summary>No ACTIVE <c>clinic_staff</c> row joins this user to that clinic.</summary>
    NotStaff,

    /// <summary>The clinic id does not resolve to a clinic.</summary>
    ClinicNotFound,

    /// <summary>The clinic resolves but its physician profile does not — Node's <c>clinic?.physician?.userId</c> miss.</summary>
    NoClinicPhysician,

    /// <summary>Resolved. <c>PhysicianUserId</c> is set.</summary>
    Resolved
}

/// <param name="Outcome">How far resolution got.</param>
/// <param name="PhysicianUserId">
/// The clinic physician's USER id, set only when <see cref="Outcome"/> is
/// <see cref="ConnStaffOutcome.Resolved"/> and null otherwise.
/// </param>
public sealed record ConnStaffResolution(ConnStaffOutcome Outcome, string? PhysicianUserId)
{
    /// <summary>True when a physician user id was resolved.</summary>
    public bool IsResolved => Outcome == ConnStaffOutcome.Resolved && PhysicianUserId is not null;
}

/// <summary>
/// The staff fallback, which <c>connections.js</c> spells out SEVEN times in slightly different
/// words (connections.js:45-78, :143-157, :455-470, :626-639, :766-783, :821-835, :1518-1543).
/// The shape is always the same three steps:
///
/// <list type="number">
/// <item>find an ACTIVE <c>clinic_staff</c> row for (caller, clinicId);</item>
/// <item>load the clinic and read <c>clinic.physician.userId</c>;</item>
/// <item>act as that physician.</item>
/// </list>
///
/// <para><b>What differs per route is only what happens when a step FAILS</b>, and that difference
/// is on the wire. Four routes fall through silently to a non-staff behaviour; three answer a
/// specific status. So this service resolves and REPORTS; it never throws, and the handler owns
/// the mapping. The table below is the whole of it — do not invent a shared error policy.</para>
///
/// <list type="table">
/// <listheader><term>Route</term><description>On failure</description></listheader>
/// <item><term><c>GET /</c> (:45)</term><description>falls through to the userType branches — a
/// staff caller then sees their OWN (empty) patient list.</description></item>
/// <item><term><c>POST /request</c> (:144)</term><description>falls through; the physician/patient
/// pairing test at :182 then answers
/// <c>400 {"error":"Connections must be between a physician and a patient"}</c>.</description></item>
/// <item><term><c>GET /search</c> (:456)</term><description>falls through; a non-physician caller
/// keeps <c>userType</c> and therefore searches for PHYSICIANS instead of patients.</description></item>
/// <item><term><c>GET /clinic-patients</c> (:821)</term><description>every failure ends in
/// <c>200 []</c> (:836).</description></item>
/// <item><term><c>POST /create-patient</c> (:627)</term><description>NoClinicId →
/// <c>400 "clinicId is required for staff"</c>; NotStaff → <c>403 "Not authorized"</c>;
/// ClinicNotFound or NoClinicPhysician → <c>404 "Clinic physician not found"</c>.</description></item>
/// <item><term><c>POST /clinic-patients</c> (:768)</term><description>NoClinicId →
/// <c>400 "clinicId is required for staff"</c>; NotStaff → <c>403 "Not authorized"</c>;
/// ClinicNotFound → <c>404 "Clinic not found"</c> — a DIFFERENT literal from the route above.
/// NoClinicPhysician is unreachable in Node: it dereferences <c>clinic.physician.userId</c>
/// unguarded (:781) and a null physician is a TypeError caught by the route's own
/// <c>500 "Failed to create patient file"</c>. Reproduce that, not a 404.</description></item>
/// <item><term><c>POST /generate-pin</c> (:1518)</term><description>NoClinicId →
/// <c>403 "Only physicians or clinic staff can generate PINs"</c>; NotStaff →
/// <c>403 "Not authorized for this clinic"</c>; ClinicNotFound or NoClinicPhysician →
/// <c>404 "Clinic physician not found"</c>.</description></item>
/// </list>
///
/// <para><b>The gate differs too.</b> Six routes enter the staff path on
/// <c>req.user.userType !== 'PHYSICIAN'</c>; <c>POST /generate-pin</c> enters it on the ABSENCE of
/// a physician PROFILE row (:1522-1523), so a user whose claim says PHYSICIAN but who has no
/// profile takes the staff path there and the physician path everywhere else. The gate stays in
/// the handler; this service is only reached once the handler has decided to try.</para>
///
/// <para>Note the two-hop physician lookup. <c>IClinicDirectory</c> exposes the clinic's
/// <c>PhysicianId</c>, which is a physician-PROFILE id, and every one of these routes needs the
/// owner's USER id — so the second hop through
/// <see cref="IIdentityDirectory.GetPhysicianAsync"/> is what Node's
/// <c>include: { physician: { select: { userId: true } } }</c> does in one query.</para>
/// </summary>
public sealed class ConnStaffResolver(
    IClinicDirectory clinics,
    IIdentityDirectory identity)
{
    /// <summary>
    /// Runs the three steps and reports where they stopped. Never throws for a resolution failure;
    /// an infrastructure fault propagates to the caller's own named 500 guard.
    /// </summary>
    /// <param name="userId">The authenticated caller's USER id.</param>
    /// <param name="clinicId">
    /// The clinic id, already resolved by the caller as <c>body.clinicId || X-Clinic-Id</c> (or
    /// <c>query.clinicId || X-Clinic-Id</c> on the two GETs). Null or empty yields
    /// <see cref="ConnStaffOutcome.NoClinicId"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ConnStaffResolution> ResolveAsync(
        string userId, string? clinicId, CancellationToken ct = default)
    {
        if (ConnJs.Truthy(clinicId) is not { } resolvedClinicId)
            return new ConnStaffResolution(ConnStaffOutcome.NoClinicId, null);

        // `prisma.clinicStaff.findFirst({ where: { userId, clinicId, isActive: true } })`.
        if (!await clinics.IsActiveStaffAsync(userId, resolvedClinicId, ct))
            return new ConnStaffResolution(ConnStaffOutcome.NotStaff, null);

        var clinic = await clinics.GetClinicAsync(resolvedClinicId, ct);
        if (clinic is null)
            return new ConnStaffResolution(ConnStaffOutcome.ClinicNotFound, null);

        // ClinicSummary.PhysicianId is the physician PROFILE id; the routes all want the USER id.
        var physician = await identity.GetPhysicianAsync(clinic.PhysicianId, ct);
        if (physician is null)
            return new ConnStaffResolution(ConnStaffOutcome.NoClinicPhysician, null);

        return new ConnStaffResolution(ConnStaffOutcome.Resolved, physician.UserId);
    }
}
