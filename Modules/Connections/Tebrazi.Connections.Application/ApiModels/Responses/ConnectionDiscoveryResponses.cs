namespace Tebrazi.Connections.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response shapes for the DISCOVERY group of server/src/routes/connections.js —
//  finding people, creating them, and inviting them:
//
//      GET  /api/connections/search             (connections.js:443-527)
//      POST /api/connections/add-by-phone       (connections.js:529-615)
//      POST /api/connections/create-patient     (connections.js:617-735)
//      POST /api/connections/invite-email       (connections.js:969-1009)
//      GET  /api/connections/invite-link        (connections.js:1011-1040)
//      GET  /api/connections/search-physicians  (NO NODE ROUTE — see the file header of
//                                                ConnectionDiscoveryUseCases.cs)
//
//  Every record here is prefixed `ConnDiscover`, per ConnectionResponseConventions.DiscoverPrefix
//  and docs/connections-surface.md §9: FIVE handler files compile into this one namespace and two
//  of them declaring the same record name is a build error none of the five authors can see.
//
//  DECLARATION ORDER IS THE WIRE ORDER. System.Text.Json writes record properties in declaration
//  order, and each record below is ordered to match the Node object literal it ports key for key.
//  Reordering a parameter is a silent wire change.
//
//  There is deliberately NO shared connection/patient/user DTO. Six endpoints here emit five
//  incompatible shapes of "a person", and add-by-phone alone emits three shapes of its own
//  response depending on what it found.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One hit from <c>GET /api/connections/search</c> (connections.js:507-510).
///
/// <para>Node builds this as <c>{ ...u, connectionStatus }</c> — the WHOLE selected user object
/// spread, then one key appended. The six spread keys are exactly the Prisma <c>select</c> at
/// :484-491 and are reproduced here in that order, which is the order they reach the wire.</para>
///
/// <para>The search is a case-insensitive CONTAINS over <c>displayName</c>, <c>email</c> and
/// <c>phone</c>, narrowed to <c>active: true</c> and to ONE user type — PATIENT when the caller
/// acts as a physician, PHYSICIAN otherwise — and capped at <c>take: 10</c>. There is no
/// "exclude users I am already connected to" clause and no "exclude myself" clause: a physician
/// searching their own name misses only because the type filter excludes them, and an
/// already-connected patient comes back WITH a non-null <see cref="ConnectionStatus"/> rather
/// than being filtered out. That is what lets the client render an "Already connected" badge.</para>
/// </summary>
/// <param name="Id">The matched user's id.</param>
/// <param name="DisplayName">The matched user's display name.</param>
/// <param name="Email">
/// Nullable — phone-first patients created by <c>create-patient</c> carry a synthetic
/// <c>patient-&lt;digits&gt;@tebrazi.local</c> address, but the column itself is nullable.
/// </param>
/// <param name="Phone">Nullable.</param>
/// <param name="UserType">The enum member NAME, e.g. "PATIENT" — always the searched-for type.</param>
/// <param name="CreatedAt">
/// Account creation. Selected by the Node query and therefore on the wire; no client reads it.
/// </param>
/// <param name="ConnectionStatus">
/// <c>conn?.status || null</c> (connections.js:509) — the status of the FIRST edge in the
/// enrichment sweep whose EITHER end is this user, or null when there is none. Serialized as the
/// enum member name ("PENDING", "ACCEPTED", "REJECTED"). Emitted as an explicit <c>null</c>, never
/// omitted: the host sets <c>DefaultIgnoreCondition = Never</c> and the client tests
/// <c>u.connectionStatus === 'ACCEPTED'</c>.
/// </param>
public sealed record ConnDiscoverSearchItem(
    string Id,
    string DisplayName,
    string? Email,
    string? Phone,
    string UserType,
    DateTime CreatedAt,
    string? ConnectionStatus);

/// <summary>
/// One hit from <c>GET /api/connections/search-physicians</c>.
///
/// <para><b>⚠ This endpoint has NO route registration in connections.js.</b> The client calls it —
/// <c>client/src/components/PatientOnboardingWizard.jsx:68</c> — and live Node answers the global
/// 404, which that call site swallows with <c>catch { setSearchResults([]) }</c>, so the wizard's
/// "find your doctor" step silently does nothing today. The decision recorded in
/// docs/connections-surface.md §11.4 is to IMPLEMENT it rather than reproduce the 404, exactly as
/// was done for <c>GET /api/visits/inbox</c>: the client already handles this shape and cannot be
/// broken by a correct one.</para>
///
/// <para>The shape is <see cref="ConnDiscoverSearchItem"/> plus the three keys the wizard renders,
/// with <see cref="Id"/> and <see cref="UserId"/> BOTH set to the physician's USER id so that the
/// call site's two fallbacks — <c>doc.userId || doc.id</c> for the connect button (:301) and
/// <c>doc.id</c> for the React key (:280) — land on the same value. The wizard also reads
/// <c>doc.user?.displayName || doc.displayName</c> (:294); there is no nested <c>user</c> object
/// here, so the fallback half is what renders.</para>
/// </summary>
/// <param name="Id">The physician's USER id. Same value as <see cref="UserId"/> — see above.</param>
/// <param name="UserId">The physician's USER id, the value the connect button posts.</param>
/// <param name="DisplayName">The physician's display name, rendered as "Dr. {displayName}".</param>
/// <param name="Email">Nullable.</param>
/// <param name="Phone">Nullable.</param>
/// <param name="UserType">Always "PHYSICIAN" — the search narrows to that type.</param>
/// <param name="CreatedAt">Account creation, carried for symmetry with the sibling endpoint.</param>
/// <param name="Specialty">
/// From the physician PROFILE row, or null when the user has no profile. The client renders
/// <c>doc.specialty || 'General Practice'</c>, so null is a supported value.
/// </param>
/// <param name="Verified">From the physician PROFILE row; <c>false</c> when there is no profile.</param>
/// <param name="ConnectionStatus">
/// The caller's existing edge with this physician, or null. Same matcher as
/// <see cref="ConnDiscoverSearchItem.ConnectionStatus"/>.
/// </param>
public sealed record ConnDiscoverPhysicianSearchItem(
    string Id,
    string UserId,
    string DisplayName,
    string? Email,
    string? Phone,
    string UserType,
    DateTime CreatedAt,
    string? Specialty,
    bool Verified,
    string? ConnectionStatus);

/// <summary>
/// The patient account echoed by both non-empty branches of <c>POST /add-by-phone</c> — the five
/// columns of the Prisma <c>select</c> at connections.js:548, in that order.
///
/// <para>Declared once rather than twice because the two branches emit the byte-identical object
/// from the same variable (:560, :581). It is NOT shared with <c>create-patient</c>'s
/// <see cref="ConnDiscoverCreatedPatient"/>, which is four keys and has no
/// <see cref="ProfilePictureUrl"/>.</para>
/// </summary>
/// <param name="Id">The patient user's id.</param>
/// <param name="DisplayName">The patient's display name.</param>
/// <param name="Email">Nullable.</param>
/// <param name="Phone">
/// The STORED phone, not the normalized one the request supplied — the two differ whenever the
/// caller typed spaces or hyphens.
/// </param>
/// <param name="ProfilePictureUrl">Nullable.</param>
public sealed record ConnDiscoverPatientAccount(
    string Id,
    string DisplayName,
    string? Email,
    string? Phone,
    string? ProfilePictureUrl);

/// <summary>
/// <c>POST /add-by-phone</c> when no active PATIENT account holds that number
/// (connections.js:553) — <c>200 { found: false, phone }</c>, and <b>no <c>patient</c> key at
/// all</b>. The client branches on <c>found</c> and opens its create-patient form.
/// </summary>
/// <param name="Found">Always false on this branch.</param>
/// <param name="Phone">
/// The NORMALIZED number (<c>ConnJs.NormalizePhone</c> — whitespace and ASCII hyphens stripped, a
/// leading <c>+</c> and any parentheses kept), echoed so the client can prefill its form with it.
/// This value is on the wire and is re-sent to <c>create-patient</c>.
/// </param>
public sealed record ConnDiscoverAddByPhoneNotFoundResponse(
    bool Found,
    string Phone);

/// <summary>
/// <c>POST /add-by-phone</c> when an edge already joins this physician to that patient
/// (connections.js:558-564) — <c>200</c>, and <b>no connection is created</b>.
///
/// <para>⚠ The probe behind this branch omits the <c>subprofileId</c> key entirely
/// (<c>FindAnyPairAsync</c>, :556), so it matches the parent edge OR any DEPENDANT edge. A
/// physician connected only to this patient's child is told "already connected" here and never
/// gets a parent edge — while <c>connect-by-pin</c>, which probes with an explicit
/// <c>subprofileId: null</c>, does create one. Both are the contract; see
/// docs/connections-surface.md §10.6.</para>
/// </summary>
/// <param name="Found">Always true on this branch.</param>
/// <param name="AlreadyConnected">Always true on this branch.</param>
/// <param name="Patient">The matched account.</param>
/// <param name="ConnectionStatus">
/// The EXISTING edge's status as its enum member name — it can be PENDING or REJECTED, not only
/// ACCEPTED, because the probe filters on neither.
/// </param>
/// <param name="Message">
/// <c>`${patient.displayName} is already in your patient list`</c> — interpolated from the stored
/// display name and shown to the physician verbatim.
/// </param>
public sealed record ConnDiscoverAddByPhoneExistingResponse(
    bool Found,
    bool AlreadyConnected,
    ConnDiscoverPatientAccount Patient,
    string ConnectionStatus,
    string Message);

/// <summary>
/// The two-key connection stub <c>POST /add-by-phone</c> returns after creating an edge
/// (connections.js:602). <b>Only <c>id</c> and <c>status</c></b> — not the whole row, unlike
/// <c>connect-by-pin</c>, which nests the entire connection under the same <c>connection</c> key.
/// </summary>
/// <param name="Id">The new connection's id.</param>
/// <param name="Status">Always "ACCEPTED" — this route inserts an accepted edge directly.</param>
public sealed record ConnDiscoverAddedConnection(
    string Id,
    string Status);

/// <summary>
/// <c>POST /add-by-phone</c> after creating the edge (connections.js:598-604) — <c>200</c>, not
/// 201, even though a row was written.
/// </summary>
/// <param name="Found">Always true on this branch.</param>
/// <param name="AlreadyConnected">
/// Always FALSE on this branch, and present rather than omitted — the client distinguishes the
/// three branches on <c>found</c> and <c>alreadyConnected</c> together.
/// </param>
/// <param name="Patient">The matched account.</param>
/// <param name="Connection">The new edge's id and status, and nothing else.</param>
/// <param name="Message"><c>`${patient.displayName} added to your patient list`</c>.</param>
public sealed record ConnDiscoverAddByPhoneCreatedResponse(
    bool Found,
    bool AlreadyConnected,
    ConnDiscoverPatientAccount Patient,
    ConnDiscoverAddedConnection Connection,
    string Message);

/// <summary>
/// The four user columns <c>POST /create-patient</c> echoes (connections.js:727-731). They come
/// from the CREATED row, so <see cref="Email"/> is the synthetic
/// <c>patient-&lt;digits&gt;@tebrazi.local</c> address whenever the request supplied none, and
/// <see cref="Phone"/> is <c>phone || null</c> — the RAW body value, never normalized by this
/// route.
/// </summary>
/// <param name="Id">The new user's id.</param>
/// <param name="DisplayName">The <c>name</c> from the request body, stored verbatim.</param>
/// <param name="Email">The account email — real or synthetic, and the patient's permanent login.</param>
/// <param name="Phone">The raw body phone, or null.</param>
public sealed record ConnDiscoverCreatedPatient(
    string Id,
    string DisplayName,
    string? Email,
    string? Phone);

/// <summary>
/// <c>POST /api/connections/create-patient</c> (connections.js:726-734) —
/// <c>200 { success, patient, message }</c>.
///
/// <para><b>200, not 201</b>, although the route writes four rows: a <c>users</c> row, a
/// <c>patient_profiles</c> row, a SELF <c>family_subprofiles</c> row and an ACCEPTED
/// <c>doctor_patient_connections</c> row. Neither the connection nor the profile appears in the
/// body — the client refetches its patient list.</para>
/// </summary>
/// <param name="Success">Always true; a failure is an error body, not <c>success: false</c>.</param>
/// <param name="Patient">The four echoed user columns.</param>
/// <param name="Message">Always "Patient created and connected successfully".</param>
public sealed record ConnDiscoverCreatePatientResponse(
    bool Success,
    ConnDiscoverCreatedPatient Patient,
    string Message);

/// <summary>
/// <c>POST /api/connections/invite-email</c> (connections.js:1000) —
/// <c>200 { success: true, message: "Invitation sent" }</c>.
///
/// <para><b>EVERY send outcome answers this body</b>, including a failed one. Node's
/// <c>sendEmail</c> never rejects: <c>server/src/services/emailService.js</c> try/catches each step
/// of transport construction (:26-64), always reaches a console fallback (:60-65), and wraps the
/// whole send in a <c>try</c> that returns <c>{ success: false, error }</c> at :140-143 — the
/// Resend branch returning it directly at :109 without even throwing. The route discards that
/// object and returns <c>{ success: true }</c> unconditionally (:1000). A real Resend outage,
/// a refused SMTP connection and an unconfigured transport are therefore indistinguishable on the
/// wire, and the handler wraps <c>IEmailSender.SendAsync</c> in a swallow-all to keep it that way.
/// The route's <c>500 {"error":"Failed to send invitation"}</c> is reachable only through the
/// caller's own <c>findUnique</c> at :974-977.</para>
/// </summary>
/// <param name="Success">Always true — including when the mail transport failed.</param>
/// <param name="Message">Always "Invitation sent".</param>
public sealed record ConnDiscoverInviteEmailResponse(
    bool Success,
    string Message);

/// <summary>
/// <c>GET /api/connections/invite-link</c> (connections.js:1022-1026).
///
/// <para>Pure computation — no row is written and the token is a deterministic function of the
/// physician's own id, so two calls a year apart return identical bytes.</para>
/// </summary>
/// <param name="InviteUrl">
/// <c>`${CLIENT_URL || 'http://localhost:5174'}/signup?ref=dr-${token}&amp;physician=${userId}`</c>.
/// The id is NOT URL-encoded, matching the Node concatenation.
/// </param>
/// <param name="PhysicianName">
/// <c>physician?.displayName || 'Doctor'</c> — note the fallback here is <b>"Doctor"</b>, while the
/// sibling <c>invite-email</c> route's subject line falls back to <b>"Your Doctor"</b>. Two
/// different literals in two adjacent handlers; both are reproduced.
/// </param>
/// <param name="Token">
/// <c>`dr-${token}`</c> — WITH the prefix, where the 16 hex characters inside
/// <see cref="InviteUrl"/>'s <c>ref</c> parameter are the same value. It carries no secret and no
/// expiry: it is <c>sha256("physician-invite-" + userId)</c> truncated, i.e. an identifier derived
/// from an id that the same URL also carries in clear.
/// </param>
public sealed record ConnDiscoverInviteLinkResponse(
    string InviteUrl,
    string PhysicianName,
    string Token);

/// <summary>
/// A party object on <c>POST /api/connections</c>'s 201 body — <c>displayName</c> and
/// <c>email</c> and nothing else.
///
/// <para>Its own type rather than a reuse of <c>ConnLinkRequestParty</c>: that record belongs to
/// <c>POST /request</c> and the two routes are free to diverge. See
/// <see cref="ConnectionResponseConventions"/>.</para>
/// </summary>
/// <param name="DisplayName">The party's display name.</param>
/// <param name="Email">The party's email, or null.</param>
public sealed record ConnDiscoverConnectParty(
    string DisplayName,
    string? Email);

/// <summary>
/// <c>POST /api/connections</c> → <b>201</b>. <b>Node has no such route</b> — see
/// <c>ConnDiscoverConnectHandler</c> for why it is implemented rather than 404'd.
///
/// <para>The key set is <c>POST /request</c>'s 201 body deliberately: the two routes create the
/// same row through the same domain factory, and a client that already parses one parses the
/// other. <c>status</c> is always <c>"PENDING"</c>, <c>connectedAt</c> always null,
/// <c>subprofileId</c> always null.</para>
///
/// <para>The sole caller — <c>client/src/components/PatientOnboardingWizard.jsx:76</c> — never
/// reads the body at all (<c>await api.post('/connections', …)</c> inside a bare
/// <c>try { } catch { }</c>), so nothing on the wire today depends on these keys. They are chosen
/// to match the sibling route rather than invented.</para>
/// </summary>
/// <param name="Id">The new connection id.</param>
/// <param name="PhysicianUserId">The physician end, from the body's <c>physicianUserId</c>.</param>
/// <param name="PatientUserId">The patient end — always the CALLER.</param>
/// <param name="SubprofileId">Always null: this route has no dependant parameter.</param>
/// <param name="Status">Always <c>"PENDING"</c>.</param>
/// <param name="InitiatedBy">The caller's USER id, i.e. the patient.</param>
/// <param name="ConnectedAt">Always null on this path.</param>
/// <param name="CreatedAt">Row creation.</param>
/// <param name="UpdatedAt">Last modification.</param>
/// <param name="PhysicianUser">The physician's display name and email. No id.</param>
/// <param name="PatientUser">The patient's display name and email. No id.</param>
public sealed record ConnDiscoverConnectResponse(
    string Id,
    string PhysicianUserId,
    string PatientUserId,
    string? SubprofileId,
    string Status,
    string InitiatedBy,
    DateTime? ConnectedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    ConnDiscoverConnectParty PhysicianUser,
    ConnDiscoverConnectParty PatientUser);
