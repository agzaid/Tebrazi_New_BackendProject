namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The published read port onto patient-owned records, supplied by Patients.
///
/// Visits, Appointments and Prescriptions all embed a dependant's name and relation in their
/// responses, and the clinical endpoints need the patient's allergies, conditions and reported
/// medications as AI context. None of them may touch the Patients context to get it.
/// </summary>
public interface IPatientDirectory
{
    Task<SubprofileSummary?> GetSubprofileAsync(string subprofileId, CancellationToken ct = default);

    /// <summary>Resolved in one query, for a page of visits or appointments.</summary>
    Task<IReadOnlyDictionary<string, SubprofileSummary>> GetSubprofilesAsync(
        IReadOnlyCollection<string> subprofileIds, CancellationToken ct = default);

    /// <summary>
    /// The dependant's clinical bundle, used as context for the SOAP and interaction prompts.
    /// Conditions and medications are filtered to ACTIVE ones only, matching the Node query.
    /// </summary>
    Task<SubprofileClinicalContext?> GetSubprofileClinicalContextAsync(
        string subprofileId, CancellationToken ct = default);

    /// <summary>
    /// The dependant ids on a patient account, resolved through their PatientProfile.
    ///
    /// Ordered by id so the result is deterministic. The Node equivalent uses an unordered
    /// <c>findFirst</c>, so on a multi-dependant account the two backends can pick a different
    /// row — see docs/PORT-STATUS.md.
    /// </summary>
    Task<IReadOnlyList<string>> ListSubprofileIdsByUserAsync(
        string patientUserId, CancellationToken ct = default);

    /// <summary>
    /// Active reported-medication names across a set of dependants, batched. Only the names
    /// cross the boundary — the full rows stay inside Patients.
    ///
    /// <para><b>"Active" means the <c>isActive</c> COLUMN, and deliberately NOT the soft-delete
    /// state.</b> Node's two reads — prescriptions.js:742-745 and :792-795 — are
    /// <c>findMany({ where: { subprofileId, isActive: true } })</c> with NO <c>deletedAt</c>
    /// predicate, while <c>model CurrentMedication</c> does carry <c>deletedAt</c>
    /// (schema.prisma:493). A soft-deleted-but-<c>isActive</c> row is therefore RETURNED by Node
    /// and pushed into <c>existingDrugs</c>, so the implementation must ignore the
    /// <c>PatientsDbContext</c> soft-delete query filter. This is wire-visible, not merely
    /// prompt-visible: <c>POST /api/prescriptions/check-interactions</c> echoes the assembled list
    /// back as <c>drugsChecked</c> (prescriptions.js:856), and dropping one name can also flip the
    /// fewer-than-two short-circuit at :804 into
    /// <c>"Only 1 active medication found — no interactions to check"</c>.</para>
    /// </summary>
    /// <param name="subprofileIds">The dependants to read; an empty set returns an empty list.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListActiveMedicationNamesAsync(
        IReadOnlyCollection<string> subprofileIds, CancellationToken ct = default);

    // ── Added for the Connections module ─────────────────────────────────────

    /// <summary>
    /// The <c>patient_profiles.id</c> owned by a user, or null when they have no profile row —
    /// <c>prisma.patientProfile.findUnique({ where: { userId } })</c>, which
    /// <c>POST /api/connections/assign-subprofile</c> runs before anything else
    /// (connections.js:320-321).
    ///
    /// <para>Null there is <b>400</b>, not 404: <c>{"error":"Patient profile not found"}</c>. The
    /// id is then compared against <see cref="SubprofileSummary.PatientProfileId"/> from
    /// <see cref="GetSubprofileAsync"/> to reproduce the route's
    /// <c>findFirst({ id: subprofileId, patientProfileId: profile.id })</c> ownership test — a
    /// mismatch is <b>404</b> <c>{"error":"Family member not found"}</c>, so a patient cannot
    /// assign a doctor to somebody else's dependant.</para>
    /// </summary>
    /// <param name="patientUserId">The account holder's USER id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> GetPatientProfileIdAsync(string patientUserId, CancellationToken ct = default);

    /// <summary>
    /// The <c>conditionsCount</c> and <c>medsCount</c> of
    /// <c>GET /api/connections/patient-summaries</c> (connections.js:1069-1074), batched across
    /// every connected patient instead of the two queries per patient Node issues inside
    /// <c>Promise.allSettled</c>.
    ///
    /// <para><b>Both counts reach the rows through a SUBPROFILE only</b> — the Node filter is
    /// <c>{ subprofile: { patientProfile: { userId: pid } } }</c>, so a condition or medication
    /// attached DIRECTLY to the <c>PatientProfile</c> (both tables allow either parent, and
    /// <c>patientProfileId</c> is the other one) is NOT counted. That is the contract; widening it
    /// would inflate every card on the physician dashboard.</para>
    ///
    /// <para><b>There is no <c>isActive</c> filter on either count</b>, unlike
    /// <see cref="ListActiveMedicationNamesAsync"/>. An inactive chronic condition and a
    /// discontinued medication both count. Do not add one.</para>
    ///
    /// <para>The <c>PatientsDbContext</c> soft-delete query filters DO apply, and that is correct
    /// rather than an oversight: Node HARD-deletes these rows, so <c>deletedAt</c> is never set on
    /// a live Node row and the filter is what reproduces the hard delete. Same reasoning as the
    /// three Patients aggregate endpoints — see docs/PORT-STATUS.md.</para>
    ///
    /// <para>A patient with neither is ABSENT from the dictionary, not present as
    /// <c>(0, 0)</c> — read it with <c>GetValueOrDefault</c>, whose default already carries two
    /// zeros. An empty <paramref name="patientUserIds"/> returns an empty dictionary.</para>
    /// </summary>
    /// <param name="patientUserIds">The patients to cover, keyed by USER id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, SubprofileClinicalCounts>> CountSubprofileClinicalItemsAsync(
        IReadOnlyCollection<string> patientUserIds, CancellationToken ct = default);
}

/// <param name="ConditionsCount">Chronic conditions on the patient's dependants, active or not.</param>
/// <param name="MedicationsCount">Reported medications on the patient's dependants, active or not.</param>
public readonly record struct SubprofileClinicalCounts(int ConditionsCount, int MedicationsCount);

/// <summary>
/// The narrow WRITE port onto <c>patient_profiles</c> and <c>family_subprofiles</c>, published by
/// Patients because Patients owns both tables. It exists for one endpoint:
/// <c>POST /api/connections/create-patient</c> (connections.js:680-698), where a clinic creates a
/// patient account for somebody who has never used the app and must give them a profile and a
/// SELF dependant so the rest of the product has something to hang records on.
///
/// <para>Deliberately tiny, like <see cref="IOrganizationMembershipWriter"/> and
/// <see cref="IPatientAccountProvisioner"/>. <see cref="IPatientDirectory"/> stays read-only.</para>
///
/// <para>It commits through the PATIENTS unit of work, so the Node route's four sequential
/// creates become four commits across three modules rather than one transaction. Node has no
/// transaction here either — a failure at any of connections.js:668, :681, :690 or :701 leaves the
/// earlier rows behind — so the port reproduces the ordering rather than inventing atomicity the
/// original does not have.</para>
/// </summary>
public interface IPatientProfileProvisioner
{
    /// <summary>
    /// Creates the patient's profile and its SELF dependant in one commit, and returns the new
    /// <c>patient_profiles.id</c>.
    ///
    /// <para>Reproduces connections.js:681-698. The profile carries only <c>address</c> and
    /// <c>whatsappNumber</c> — every other profile column is left at its default, including
    /// <c>dateOfBirth</c> and <c>gender</c>, which the route writes onto the SELF SUBPROFILE
    /// instead and not onto the profile.</para>
    /// </summary>
    /// <param name="patientUserId">The user the profile belongs to. Required.</param>
    /// <param name="address">From the body, already through <c>address || null</c>.</param>
    /// <param name="whatsappNumber">From the body, already through <c>whatsappNumber || null</c>.</param>
    /// <param name="selfName">
    /// The SELF dependant's name, which is the SAME <c>name</c> value the user account was created
    /// with (connections.js:693) — not a separate field.
    /// </param>
    /// <param name="dateOfBirth">
    /// Parsed from the body's <c>dateOfBirth</c>, or null when absent. An UNPARSEABLE non-empty
    /// value must be turned into the route's own 500 by the caller, because Node's
    /// <c>new Date(x)</c> yields an Invalid Date that Prisma rejects — see
    /// <c>ConnDates.TryParse</c>.
    /// </param>
    /// <param name="gender">
    /// Parsed from the body's <c>gender</c>, or null. Node writes <c>gender || null</c> RAW into a
    /// Prisma enum column, so an unrecognised string is a Prisma error and therefore the route's
    /// 500 — the caller must not silently drop it to null.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new <c>patient_profiles.id</c>.</returns>
    Task<string> CreateProfileWithSelfSubprofileAsync(
        string patientUserId,
        string? address,
        string? whatsappNumber,
        string selfName,
        DateTime? dateOfBirth,
        string? gender,
        CancellationToken ct = default);
}

public sealed record SubprofileSummary(
    string Id,
    string PatientProfileId,
    string Name,
    string Relation,
    DateTime? DateOfBirth,
    string? Gender,
    string? BloodType,
    bool IsActive);

public sealed record SubprofileClinicalContext(
    string Id,
    string Name,
    string Relation,
    IReadOnlyList<AllergyItem> Allergies,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<MedicationItem> Medications);

public sealed record AllergyItem(string Allergen, string? Severity, string? Reaction);

public sealed record MedicationItem(string DrugName, string? Dosage, string? Frequency);
