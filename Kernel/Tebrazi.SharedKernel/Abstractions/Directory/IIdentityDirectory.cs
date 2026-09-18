namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The published read port onto identity data. Modules that need to resolve a user or a
/// physician depend on THIS — never on Identity's DbContext, entities or stores.
///
/// This is the boundary that failed in KACCC, where five of six modules injected the concrete
/// <c>IdentityDbContext</c> and queried its tables directly, which is what made the module
/// separation fictional. Keeping the port read-only is deliberate: identity is written only by
/// the Identity module's own use cases.
/// </summary>
public interface IIdentityDirectory
{
    Task<UserSummary?> GetUserAsync(string userId, CancellationToken ct = default);

    /// <param name="userType">
    /// Narrows the lookup. Email is unique only per (email, userType), so omitting this can
    /// match either of a dual physician/patient pair.
    /// </param>
    Task<UserSummary?> GetUserByEmailAsync(string email, string? userType = null, CancellationToken ct = default);

    /// <summary>
    /// The user's <c>users.current_organization_id</c> column, or null when the user does not
    /// exist or the column is null.
    ///
    /// <para><b>This is NOT the JWT <c>organizationId</c> claim and the two routinely differ.</b>
    /// The claim is <c>memberships[0].organization.id</c> — the FIRST organization membership,
    /// frozen at login (server/src/routes/auth.js:140-147, and
    /// <c>Modules/Identity/…/Login/LoginHandler.cs</c> computing <c>primaryOrganization</c> the
    /// same way). This column is the user's CURRENT organization, re-read per request, and it
    /// changes when they switch tenants. <c>GET /auth/me</c> returns this column under the key
    /// <c>organizationId</c> (auth.js:228), which is where the confusion comes from.</para>
    ///
    /// <para>It is on the port rather than on <see cref="UserSummary"/> because adding a positional
    /// member to that record would touch every construction site in eight modules; the column has
    /// exactly one cross-module consumer today —
    /// <c>POST /api/connections/create-patient</c>, which copies the PHYSICIAN's value onto the new
    /// patient (connections.js:663-666, 676).</para>
    /// </summary>
    /// <param name="userId">The user whose current organization is wanted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The column value, or null.</returns>
    Task<string?> GetCurrentOrganizationIdAsync(string userId, CancellationToken ct = default);

    /// <summary>Resolved in one query — never call <see cref="GetUserAsync"/> in a loop.</summary>
    Task<IReadOnlyDictionary<string, UserSummary>> GetUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct = default);

    /// <summary>The physician profile owned by a user, or null if they are not a physician.</summary>
    Task<PhysicianSummary?> GetPhysicianByUserIdAsync(string userId, CancellationToken ct = default);

    /// <summary>Physician profiles by their own ids, for joining onto <c>clinics.physician_id</c>.</summary>
    Task<IReadOnlyDictionary<string, PhysicianSummary>> GetPhysiciansAsync(
        IReadOnlyCollection<string> physicianProfileIds, CancellationToken ct = default);

    /// <summary>
    /// One physician profile by its OWN id, not the owner's user id.
    ///
    /// Visits, Appointments and Prescriptions all need this to turn the <c>physician_id</c> they
    /// store into the user id a notification is addressed to. It exists so those callers stop
    /// writing <c>GetPhysiciansAsync([id])</c> and indexing the result.
    /// </summary>
    Task<PhysicianSummary?> GetPhysicianAsync(
        string physicianProfileId, CancellationToken ct = default);

    /// <summary>
    /// The full physician rows the appointment queue serializes — every profile scalar, not the
    /// six-field summary. Separate from <see cref="GetPhysiciansAsync"/> so the common path
    /// keeps reading only what it needs.
    /// </summary>
    Task<IReadOnlyDictionary<string, PhysicianDetail>> GetPhysicianDetailsAsync(
        IReadOnlyCollection<string> physicianProfileIds, CancellationToken ct = default);

    /// <summary>
    /// The user's subscription tier ("FREE", "CLINIC", ...), which gates AI usage limits.
    /// Null when no such user exists.
    /// </summary>
    Task<string?> GetSubscriptionTierAsync(string userId, CancellationToken ct = default);

    // ── Added for the Connections module ─────────────────────────────────────

    /// <summary>
    /// One user by PHONE — <c>POST /api/connections/add-by-phone</c>'s
    /// <c>findFirst({ phone, userType: 'PATIENT', active: true })</c> (connections.js:542-549).
    ///
    /// <para><c>users.phone</c> IS uniquely indexed in the Prisma schema (schema.prisma:106), so
    /// at most one row can match; the narrowing arguments are still applied, because a matched row
    /// that is the wrong type or deactivated must come back as "not found" and send the client to
    /// its create-patient form rather than to an inactive account.</para>
    ///
    /// <para>The phone is compared VERBATIM. The caller normalizes first —
    /// <c>ConnJs.NormalizePhone</c> strips whitespace and hyphens only — and this method must not
    /// normalize again, because a second pass would silently match rows the Node lookup misses.</para>
    /// </summary>
    /// <param name="phone">The already-normalized phone string.</param>
    /// <param name="userType">Narrows to one user type, e.g. "PATIENT". Null applies no filter.</param>
    /// <param name="activeOnly">True adds <c>active = true</c>, matching the Node query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<UserSummary?> GetUserByPhoneAsync(
        string phone, string? userType = null, bool activeOnly = false, CancellationToken ct = default);

    /// <summary>
    /// The user search behind <c>GET /api/connections/search</c> (connections.js:474-493) and the
    /// display-name narrowing on the staff branch of <c>GET /api/connections</c>
    /// (connections.js:59-63).
    ///
    /// <para>Two callers with two different shapes, which is why every knob lives on
    /// <see cref="UserSearchFilter"/> rather than in the signature. See that record for the two
    /// configurations and for the ordering divergence.</para>
    /// </summary>
    /// <param name="filter">The search configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching users, empty list when there are none. Never null.</returns>
    Task<IReadOnlyList<UserSearchRow>> SearchUsersAsync(
        UserSearchFilter filter, CancellationToken ct = default);

    /// <summary>
    /// The duplicate probe in <c>POST /api/connections/create-patient</c>
    /// (connections.js:649-651): <c>findFirst({ OR: [{ email }, ...(phone ? [{ phone }] : [])] })</c>.
    ///
    /// <para><b>No <c>userType</c> and no <c>active</c> filter</b> — a physician account or a
    /// deactivated account holding that email or phone is a collision too, and the route answers
    /// <c>409 "A patient with this email or phone already exists. Search for them instead."</c></para>
    ///
    /// <para><paramref name="phone"/> null or empty drops the phone arm entirely, reproducing the
    /// conditional spread. <paramref name="email"/> is always present at the one call site,
    /// because the route synthesizes <c>patient-&lt;digits&gt;@tebrazi.local</c> when the body has
    /// none.</para>
    /// </summary>
    /// <param name="email">The email to probe. Never null at the live call site.</param>
    /// <param name="phone">The phone to probe, or null to omit that arm.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsByEmailOrPhoneAsync(
        string email, string? phone, CancellationToken ct = default);
}

/// <summary>
/// The configuration for <see cref="IIdentityDirectory.SearchUsersAsync"/>.
///
/// <para>The two live configurations:</para>
/// <list type="bullet">
/// <item><c>GET /api/connections/search</c> (connections.js:474-493) —
/// <c>new UserSearchFilter(q, MatchDisplayName: true, MatchEmail: true, MatchPhone: true,
/// UserType: targetType, ActiveOnly: true, Take: 10)</c>. <c>targetType</c> is "PATIENT" when the
/// caller acts as a physician and "PHYSICIAN" otherwise.</item>
/// <item><c>GET /api/connections</c>, staff branch (connections.js:59-63) —
/// <c>new UserSearchFilter(search, MatchDisplayName: true)</c> and nothing else: no email or
/// phone arm, no type filter, no <c>active</c> filter and NO take, because Node expresses it as a
/// relation filter on the connection query rather than as a user search. The ids that come back
/// go into <c>ConnectionFilter.PatientUserIdIn</c>.</item>
/// </list>
///
/// <para>⚠ <b>Ordering is a recorded divergence.</b> Neither Node query has an <c>orderBy</c>, so
/// PostgreSQL returns physical order — and on the search path that is OBSERVABLE, because
/// <c>take: 10</c> means physical order decides WHICH ten users come back, not merely their
/// sequence. The implementation orders by <c>created_at</c> ascending then <c>id</c>, which is the
/// closest deterministic equivalent for a mostly-append-only table. Two backends can therefore
/// return different tenths of a large result set. See docs/connections-surface.md §10.</para>
/// </summary>
/// <param name="Query">
/// The raw search term. Applied as a case-insensitive CONTAINS — Prisma's
/// <c>{ contains: q, mode: 'insensitive' }</c>, which is a substring match anywhere in the value,
/// not a prefix. Callers gate on their own minimum length; this record does not.
/// </param>
/// <param name="MatchDisplayName">Include <c>display_name</c> in the OR. True at both call sites.</param>
/// <param name="MatchEmail">Include <c>email</c> in the OR.</param>
/// <param name="MatchPhone">Include <c>phone</c> in the OR.</param>
/// <param name="UserType">
/// Narrows to one user type ("PATIENT", "PHYSICIAN"). Null applies no filter.
/// </param>
/// <param name="ActiveOnly">True adds <c>active = true</c>.</param>
/// <param name="Take">
/// Caps the result. Null returns everything matching — which the staff-branch caller needs,
/// because a name filter that silently kept only the first ten patients would hide connections
/// Node lists.
/// </param>
public sealed record UserSearchFilter(
    string Query,
    bool MatchDisplayName = true,
    bool MatchEmail = false,
    bool MatchPhone = false,
    string? UserType = null,
    bool ActiveOnly = false,
    int? Take = null);

/// <summary>
/// One search hit. These six columns are exactly the Node <c>select</c> at
/// connections.js:484-491, and the search endpoint spreads the whole object into its response
/// before appending <c>connectionStatus</c> — so adding a field here adds it to the wire.
/// </summary>
/// <param name="Id">The user's id.</param>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="Email">Nullable — phone-first patients have none.</param>
/// <param name="Phone">Nullable.</param>
/// <param name="UserType">The enum member NAME, e.g. "PATIENT".</param>
/// <param name="CreatedAt">
/// Account creation. Selected by the Node query and therefore emitted; no caller reads it.
/// </param>
public sealed record UserSearchRow(
    string Id,
    string DisplayName,
    string? Email,
    string? Phone,
    string UserType,
    DateTime CreatedAt);

/// <summary>
/// The narrow WRITE port onto <c>users</c>, published by Identity because Identity owns the table.
/// It exists for exactly one endpoint: <c>POST /api/connections/create-patient</c>
/// (connections.js:617-735), where a physician or receptionist creates a patient account for
/// somebody who has never used the app.
///
/// <para>Kept deliberately tiny, like <see cref="IOrganizationMembershipWriter"/>. A broad
/// "identity write" port would recreate the cross-module coupling these ports exist to prevent,
/// and <see cref="IIdentityDirectory"/> stays read-only.</para>
///
/// <para>It commits through the IDENTITY unit of work, so a Connections handler that calls this
/// and then saves its own context has made two commits, not one. Node wraps none of
/// <c>create-patient</c> in a transaction either — it issues four sequential <c>create</c> calls
/// (:668, :681, :690, :701) and a failure at any point leaves the earlier rows behind. The port
/// reproduces that ordering rather than inventing an atomicity Node does not have.</para>
/// </summary>
public interface IPatientAccountProvisioner
{
    /// <summary>
    /// Creates a PATIENT user with a random password and commits, returning the new row.
    ///
    /// <para>Reproduces connections.js:656-678: a 16-byte random hex password is bcrypt-hashed at
    /// cost 10 and then DISCARDED — it is never returned, logged or emailed, and the patient
    /// claims the account through the password-reset flow. The hashing and the random generation
    /// happen inside the implementation precisely so no caller is tempted to keep the plaintext.</para>
    ///
    /// <para>The row is created with <c>role = USER</c> and <c>userType = PATIENT</c>, both
    /// hard-coded at the Node call site.</para>
    /// </summary>
    /// <param name="displayName">
    /// The patient's name, from the request body's <c>name</c>. Required.
    /// </param>
    /// <param name="email">
    /// The account email. Never null at the live call site: the route synthesizes
    /// <c>patient-&lt;digits&gt;@tebrazi.local</c> from the phone when the body has none
    /// (connections.js:646), and that synthetic address becomes the patient's permanent login.
    /// </param>
    /// <param name="phone">The patient's phone, or null. Node writes <c>phone || null</c>.</param>
    /// <param name="currentOrganizationId">
    /// Copied from the PHYSICIAN's <c>currentOrganizationId</c> (connections.js:676), so the new
    /// patient lands in the clinic's tenant. Null when the physician has none.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created user.</returns>
    Task<UserSummary> CreatePatientAccountAsync(
        string displayName,
        string email,
        string? phone,
        string? currentOrganizationId,
        CancellationToken ct = default);
}

public sealed record UserSummary(
    string Id,
    string? Email,
    string DisplayName,
    string? Phone,
    string? ProfilePictureUrl,
    string Role,
    string UserType,
    bool Active);

public sealed record PhysicianSummary(
    string Id,
    string UserId,
    string LicenseNumber,
    string Specialty,
    bool Verified,
    string DisplayName);

/// <summary>
/// Every PhysicianProfile scalar plus the owner's display name. The appointment queue includes
/// the whole profile row, so this carries fields no other caller wants —
/// <see cref="ScratchpadNotes"/> in particular is the physician's private text, and it reaches
/// the client only because the Node include is unselective.
/// </summary>
public sealed record PhysicianDetail(
    string Id,
    string UserId,
    string LicenseNumber,
    string Specialty,
    string? Qualifications,
    string? Bio,
    string? ScratchpadNotes,
    int? YearsOfExperience,
    bool Verified,
    DateTime? VerifiedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string DisplayName);
