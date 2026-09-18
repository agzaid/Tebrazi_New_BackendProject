namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The published read port onto clinic-owned patient charts (<c>clinic_patients</c>). Visits and
/// Appointments both carry a <c>clinic_patient_id</c> and must render the chart's name and
/// contact details, but neither may touch the Clinics context to do it.
///
/// Read-only by design, matching <see cref="IIdentityDirectory"/>: charts are written only by
/// the Clinics module's own use cases.
/// </summary>
public interface IClinicPatientDirectory
{
    Task<ClinicPatientSummary?> GetAsync(string clinicPatientId, CancellationToken ct = default);

    /// <summary>
    /// Resolved in ONE query. A list of visits or appointments carries many chart ids, and
    /// calling <see cref="GetAsync"/> per row is the N+1 this method exists to prevent.
    /// Ids that do not resolve are simply absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<string, ClinicPatientSummary>> GetManyAsync(
        IReadOnlyCollection<string> clinicPatientIds, CancellationToken ct = default);

    /// <summary>
    /// Moves the chart's <c>last_visit_date</c> forward and commits. The one write this port
    /// exposes, kept deliberately narrow so Visits cannot mutate anything else about a chart.
    ///
    /// Silent when the chart is gone, and never moves the date backwards — an older visit being
    /// backfilled must not overwrite a newer one.
    /// </summary>
    Task TouchLastVisitAsync(
        string clinicPatientId, DateTime visitedAtUtc, CancellationToken ct = default);

    // ── Added for the Connections module ─────────────────────────────────────
    //
    // Three of the 21 Connections endpoints operate on clinic_patients:
    //   POST   /api/connections/clinic-patients        (connections.js:747-807)
    //   GET    /api/connections/clinic-patients        (connections.js:813-849)
    //   DELETE /api/connections/clinic-patients/{id}   (connections.js:860-893)
    //
    // The chart still belongs to Clinics. Rather than give Connections a second ClinicPatient
    // entity — which would put one table in two EF models, the exact leak these ports exist to
    // prevent — the port grew the reads and writes those three routes need. Note that this makes
    // the port no longer read-only, which is why each write is documented with the Node lines it
    // reproduces and nothing broader is exposed.

    /// <summary>
    /// One chart, scoped to its owner — <c>findFirst({ id, physicianUserId })</c>, which
    /// <c>DELETE /clinic-patients/{id}</c> uses as its single ownership gate
    /// (connections.js:866-872). A miss is <c>404
    /// {"error":"Patient file not found or not authorized"}</c>, so "no such chart" and "somebody
    /// else's chart" are deliberately indistinguishable.
    ///
    /// <para><b>The ownership key is <c>physician_user_id</c> and nothing else.</b> No clinic
    /// membership test, so a receptionist calling this endpoint never matches a chart and always
    /// gets the 404 — the delete route has no staff fallback at all, unlike its create and list
    /// siblings.</para>
    /// </summary>
    /// <param name="clinicPatientId">The chart id from the route.</param>
    /// <param name="physicianUserId">The owning physician's USER id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClinicPatientRecord?> GetOwnedRecordAsync(
        string clinicPatientId, string physicianUserId, CancellationToken ct = default);

    /// <summary>
    /// A physician's ACTIVE charts, ordered by <c>name</c> ascending — the whole of
    /// <c>GET /api/connections/clinic-patients</c>'s query
    /// (connections.js:838-842): <c>where: { physicianUserId, isActive: true },
    /// orderBy: { name: 'asc' }</c>.
    ///
    /// <para><b>Deliberately NOT
    /// <c>IClinicPatientStore.ListForPhysicianAsync</c>.</b> That store method takes a clinic id,
    /// returns inactive charts too, and sorts by last-visit-then-name for a different screen.
    /// Using it here would change both the row set and the order the client renders.</para>
    ///
    /// <para>Ordering is by the database collation, which is case-insensitive; Postgres' default
    /// <c>en_US.UTF-8</c> collation orders case-insensitively too, so the two agree for Latin
    /// names. Arabic names sort by code point in both.</para>
    ///
    /// <para>The endpoint also emits <c>_count.visits</c> per chart. That count lives in the
    /// Visits context and is NOT part of this port — resolve it through
    /// <c>IVisitDirectory.CountByClinicPatientAsync</c> and merge in the handler.</para>
    /// </summary>
    /// <param name="physicianUserId">The owning physician's USER id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching charts, empty list when there are none. Never null.</returns>
    Task<IReadOnlyList<ClinicPatientRecord>> ListActiveForPhysicianAsync(
        string physicianUserId, CancellationToken ct = default);

    /// <summary>
    /// Creates a walk-in chart and commits, returning the stored row —
    /// <c>POST /api/connections/clinic-patients</c> (connections.js:785-800), whose 201 body
    /// spreads the whole created record.
    /// </summary>
    /// <param name="draft">The column values. See <see cref="ClinicPatientDraft"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created chart.</returns>
    Task<ClinicPatientRecord> CreateAsync(ClinicPatientDraft draft, CancellationToken ct = default);

    /// <summary>
    /// HARD-deletes the chart row and commits — the last statement of the <c>$transaction</c> at
    /// connections.js:875-886.
    ///
    /// <para><b>It deletes the chart ONLY.</b> The Node transaction first removes the chart's
    /// visits, appointments, patient notes and patient tags; those tables belong to other modules
    /// (or, for <c>patient_notes</c> and <c>patient_tags</c>, to no ported module at all), so the
    /// caller performs those through their own ports BEFORE calling this. See
    /// docs/connections-surface.md §10 for what that costs in atomicity.</para>
    /// </summary>
    /// <param name="clinicPatientId">The chart id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when a row was deleted, false when it was already gone.</returns>
    Task<bool> DeleteAsync(string clinicPatientId, CancellationToken ct = default);
}

/// <summary>
/// Every <c>ClinicPatient</c> scalar, because all three Connections routes that touch a chart put
/// the WHOLE Prisma row on the wire — <c>POST</c> as <c>patient</c>, <c>GET</c> as the array
/// elements. Wider than <see cref="ClinicPatientSummary"/> on purpose: that record exists so
/// Visits and Appointments can render a header without seeing clinical content, and this one
/// exists because Node's <c>res.json(patient)</c> is unselective.
///
/// <para><b>Do not add the port's own audit columns to a response.</b> The .NET table carries
/// <c>created_by</c> and <c>updated_by</c>, which the Prisma model has not; they are absent from
/// this record so a handler cannot accidentally emit them.</para>
/// </summary>
/// <param name="Id">The chart id.</param>
/// <param name="PhysicianUserId">The owning physician's USER id.</param>
/// <param name="ClinicId">The clinic the chart is filed under, or null.</param>
/// <param name="Name">The patient's name, already trimmed.</param>
/// <param name="Phone">Nullable.</param>
/// <param name="Email">Nullable.</param>
/// <param name="DateOfBirth">Nullable.</param>
/// <param name="Gender">The <c>Gender</c> member NAME, or null.</param>
/// <param name="NationalId">Nullable. No Connections route writes it.</param>
/// <param name="BloodType">Free text, nullable.</param>
/// <param name="Notes">Free text, nullable.</param>
/// <param name="Allergies">Free-text list. EMPTY, never null — Prisma defaults it to <c>[]</c>.</param>
/// <param name="ChronicConditions">Free-text list. Empty, never null.</param>
/// <param name="LinkedUserId">The Tebrazi account this chart was matched to, or null.</param>
/// <param name="LinkedAt">When it was matched, or null.</param>
/// <param name="IsActive">Charts are created active; nothing in connections.js deactivates one.</param>
/// <param name="LastVisitDate">Denormalised marker, written when a visit closes.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">
/// Prisma declares <c>@updatedAt</c>, which is non-null there. Nullable here because
/// <c>MutableEntity</c> leaves it null until the first modification, so a freshly created chart
/// serializes it as null in the port and as a timestamp in Node. Recorded in
/// docs/PORT-STATUS.md rather than papered over.
/// </param>
public sealed record ClinicPatientRecord(
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
/// The column values <c>POST /api/connections/clinic-patients</c> writes
/// (connections.js:785-800). Every one of them has already been through the route's own
/// coercion — this record applies none of its own.
/// </summary>
/// <param name="PhysicianUserId">
/// The resolved owner. The caller's own id on the physician path, the clinic's physician on the
/// staff path.
/// </param>
/// <param name="Name">
/// Already <c>name.trim()</c>. The route rejects a blank name with
/// <c>400 {"error":"Patient name is required"}</c> before reaching here.
/// </param>
/// <param name="ClinicId">The resolved clinic, or null. Nullable in the schema.</param>
/// <param name="Phone">Already <c>phone?.trim() || null</c>.</param>
/// <param name="Email">Already <c>email?.trim() || null</c>.</param>
/// <param name="DateOfBirth">Already parsed, or null.</param>
/// <param name="Gender">
/// The <c>Gender</c> member NAME, or null. Node writes <c>gender || null</c> raw into the enum
/// column, so an unrecognised string is the route's 500, not a silent null — the implementation
/// throws for one.
/// </param>
/// <param name="BloodType">Already <c>bloodType || null</c>. Free text, not an enum.</param>
/// <param name="Allergies">
/// Already through <c>Array.isArray(allergies) ? allergies : []</c>, so a string, an object or an
/// absent key all arrive as an empty list.
/// </param>
/// <param name="ChronicConditions">Same rule.</param>
/// <param name="Notes">Already <c>notes?.trim() || null</c>.</param>
public sealed record ClinicPatientDraft(
    string PhysicianUserId,
    string Name,
    string? ClinicId = null,
    string? Phone = null,
    string? Email = null,
    DateTime? DateOfBirth = null,
    string? Gender = null,
    string? BloodType = null,
    IReadOnlyList<string>? Allergies = null,
    IReadOnlyList<string>? ChronicConditions = null,
    string? Notes = null);

/// <summary>
/// The fields other modules are allowed to see. Deliberately narrower than the entity: notes,
/// allergies and conditions are clinical content that belongs to whoever owns the chart, and
/// nothing outside Clinics needs them to render a visit header.
/// </summary>
/// <param name="LinkedUserId">
/// The Tebrazi account this chart was matched to, or null while it is unlinked. A caller that
/// needs the account's own profile follows this into <see cref="IIdentityDirectory"/>.
/// </param>
public sealed record ClinicPatientSummary(
    string Id,
    string PhysicianUserId,
    string? ClinicId,
    string Name,
    string? Phone,
    string? Email,
    DateTime? DateOfBirth,
    string? Gender,
    string? BloodType,
    string? LinkedUserId,
    bool IsActive);
