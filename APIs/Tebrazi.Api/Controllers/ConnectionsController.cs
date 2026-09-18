using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Tebrazi.Common.Api.Controllers;
using Tebrazi.Common.Api.Security;
using Tebrazi.Connections.Application.ApiModels.Responses;
using Tebrazi.Connections.Application.UseCases;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Api.Controllers;

/// <summary>
/// <c>/api/connections</c> — the port of <c>server/src/routes/connections.js</c>. Nineteen of the
/// twenty-one actions below are registrations in that file; the other two,
/// <see cref="Connect"/> and <see cref="SearchPhysicians"/>, are endpoints the React client calls
/// that Node never implemented, written deliberately on the <c>GET /api/visits/inbox</c> precedent
/// and recorded in PORT-STATUS.md's "Deliberate divergences" table.
///
/// <para>connections.js holds twenty-five registrations; six of them —
/// <c>POST /link-account</c>, <c>GET /clinic-patients/{id}/full-chart</c>,
/// <c>PUT /clinic-patients/{id}/notes</c>, <c>POST /clinic-patients/{id}/tags</c>,
/// <c>DELETE /clinic-patients/{id}/tags/{tagId}</c> and <c>GET /qr-info/{physicianId}</c> — have no
/// handler in this module yet and therefore no action here. Mapping one of them before its use case
/// exists would 500 at runtime, not fail the build.</para>
///
/// <para>ROUTE ORDER. Every literal template is declared before the <c>{id}</c> family at the
/// bottom. ASP.NET prefers a literal over a parameter segment regardless of declaration order, so
/// the ordering is legibility — but the constraints behind it are real, and two are specific to this
/// file:</para>
/// <list type="bullet">
/// <item><b>NEVER add a <c>POST /{id}</c> or a <c>{id}/{action}</c> catch-all.</b> Eight of the
/// twenty-one actions are single-segment literal POSTs (<c>request</c>, <c>assign-subprofile</c>,
/// <c>add-by-phone</c>, <c>create-patient</c>, <c>invite-email</c>, <c>clinic-patients</c>,
/// <c>generate-pin</c>, <c>connect-by-pin</c>) and a parameterised POST would swallow every one of
/// them. <c>PUT {id}/accept</c> and <c>PUT {id}/reject</c> are kept apart by their second literal
/// alone, and so are <c>DELETE clinic-patients/{id}</c> and
/// <c>DELETE unassign-subprofile/{subprofileId}/{physicianUserId}</c>.</item>
/// <item><b><c>POST /</c> is a bare empty template and is NOT <c>POST /request</c>.</b> The two
/// create the same kind of edge from different bodies — <c>physicianUserId</c> here,
/// <c>targetEmail</c> there — and answer different status codes. Confusing them would silently
/// break the onboarding wizard and the Add-Doctor screen together.</item>
/// <item><b>Do NOT constrain <c>{id}</c>.</b> A connection id is unconstrained text and
/// <see cref="Delete"/> deliberately also accepts the OTHER PARTY'S USER id, so a <c>{id:guid}</c>
/// constraint would turn the handlers' own <c>404 {"error":"Connection not found"}</c> into a
/// routing 404 with a different body.</item>
/// </list>
///
/// <para><b><c>X-Clinic-Id</c> IS BOUND HERE, RAW, ON SIX ACTIONS, AND
/// <see cref="IClinicContext"/> IS DELIBERATELY NOT USED.</b> connections.js resolves a clinic seven
/// different ways (docs/connections-surface.md §10.2) and only <c>GET /</c> reads the header FIRST —
/// which is exactly <see cref="IClinicContext"/>'s precedence, so that one route, and only that one,
/// lets its handler inject the kernel service and takes no header parameter here. The other six are
/// body-first or query-first, and two of them (<c>POST /request</c> and <c>POST /generate-pin</c>)
/// do not read <c>?clinicId=</c> at all — a source <see cref="IClinicContext"/> would add, turning a
/// 403 into a successful staff action. The header is therefore bound with
/// <see cref="FromHeaderAttribute"/> under <see cref="HttpClinicContext.HeaderName"/>, the same
/// constant the collapsing implementation reads, so the two cannot drift apart. This is the pattern
/// <c>AppointmentsController.List</c> already establishes.</para>
///
/// <para><b>All nine body-taking actions carry
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>.</b> Nullability is not consulted
/// by MVC's body binder, so without it a <c>Content-Length: 0</c> POST answers
/// <c>400 {"error":"Validation failed"}</c> — a body NO route in connections.js ever produces —
/// where Express hands the handler <c>{}</c> and the route's own 400 is owed. This is a live path,
/// not a theoretical one: <c>client/src/pages/ConnectionsPage.jsx:315</c> posts
/// <c>{ clinicId: undefined }</c> to <c>generate-pin</c>, which axios serializes to <c>{}</c>. Every
/// body binds the Application layer's own record, whose members are raw <c>JsonElement</c>s
/// precisely so an absent, null or wrongly-typed field reaches the handler's guard rather than the
/// binder's.</para>
///
/// <para>Every QUERY parameter binds as <c>string?</c>. Node reads them through JavaScript
/// truthiness, so <c>?clinicId=</c> is an ABSENT filter rather than a filter on the empty string and
/// an unrecognised <c>?status=</c> is a 500 rather than a 400 — an <c>int?</c> or <c>DateTime?</c>
/// binding would answer 400 for input Node hands to the handler.</para>
///
/// <para>Reads resolve the caller through their own injected <see cref="ICurrentUser"/> and take no
/// user id; writes take it explicitly, which is why <see cref="UserId"/> appears only on the writes.
/// Node's <c>cacheMiddleware(15)</c> on <c>GET /</c> is not reproduced
/// (docs/connections-surface.md §10.11).</para>
/// </summary>
[Route("api/connections")]
[Authorize]
public sealed class ConnectionsController(IMediator mediator, ICurrentUser currentUser)
    : BaseApiController(mediator)
{
    private string UserId => currentUser.UserId ?? throw new UnauthorizedException("Invalid token");

    // ── The edge lifecycle: list, create, request, roll-up ───────────────────

    /// <summary>
    /// The tri-persona connection list (connections.js:31-114) — a BARE JSON ARRAY, never an object
    /// and never null, ordered <c>createdAt</c> DESC on all three branches.
    ///
    /// <para>THE ONLY ACTION IN THIS FILE THAT DOES NOT BIND <c>X-Clinic-Id</c> ITSELF. This route
    /// alone resolves it <c>header || query.clinicId</c> (:37), which is precisely
    /// <see cref="IClinicContext"/>'s precedence, so the handler injects the kernel service and the
    /// controller passes only the two filters. A header parameter here would duplicate a value the
    /// handler already has.</para>
    ///
    /// <para>No generic is claimed on the 200: the row shape forks on the branch — the physician and
    /// staff branches emit a WIDER subprofile than the patient branch by two keys — so the handler's
    /// result type is <c>IReadOnlyList&lt;object&gt;</c>.</para>
    ///
    /// <para><c>?search=</c> is applied on the STAFF branch ONLY (:58-62), so a doctor filtering
    /// their own patient list by name gets the unfiltered list back, and <c>?status=</c> reaches
    /// Prisma raw, so an unrecognised value is <c>500 {"error":"Failed to list connections"}</c>
    /// rather than a 400. There is no 4xx on this route at all.</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? search)
        => Payload(await Send(new ConnLinkListQuery(status, search)));

    /// <summary>
    /// <b>An endpoint connections.js does not implement.</b> The patient onboarding wizard posts
    /// <c>{ physicianUserId }</c> here (PatientOnboardingWizard.jsx:76) and Express falls through to
    /// the global 404, so that "Connect" button has never created an edge in production. Implemented
    /// rather than reproduced as a 404, with <c>POST /request</c>'s semantics keyed by
    /// <c>physicianUserId</c> instead of <c>targetEmail</c>.
    ///
    /// <para><b>201, and there is only ONE success path.</b> Unlike <c>POST /request</c> this route
    /// does not revive a REJECTED edge with a 200 — the gates are the 400 for a falsy id, the 404
    /// when the id does not name a PHYSICIAN, then the two 409s, then the insert. The new edge is
    /// PENDING, not ACCEPTED: an ACCEPTED edge would let any patient grant themselves a physician's
    /// chart access with no consent, which no patient-initiated path in connections.js does.</para>
    ///
    /// <para>NOT to be confused with <see cref="RequestConnection"/> below. Both are POSTs on this
    /// controller and this one is the bare template.</para>
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ConnDiscoverConnectResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Connect(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnDiscoverConnectBody? request)
        => CreatedPayload(await Send(new ConnDiscoverConnectCommand(UserId, request)));

    /// <summary>
    /// The entry point for the whole Add-Doctor and Add-Patient flow (connections.js:131-226).
    ///
    /// <para><b>THE STATUS CODE BRANCHES AND IT IS LOAD-BEARING.</b> The handler returns
    /// <see cref="ConnLinkRequestOutcome"/> rather than a body: <b>201</b> with
    /// <see cref="ConnLinkRequestCreatedResponse"/> when it inserts an edge (:226), <b>200</b> with
    /// <see cref="ConnLinkRequestResentResponse"/> when it revives a REJECTED one. The two bodies
    /// differ — the 200 carries a <c>message</c> key the 201 has no equivalent of — and the client
    /// distinguishes the paths by status code. Exactly one of the two payloads is non-null, selected
    /// by <c>Created</c>.</para>
    ///
    /// <para>A repeat request is a 409, not an idempotent 200, and there are two of them (:196-201):
    /// an ACCEPTED edge answers "Already connected" and a PENDING one answers "Connection request
    /// already pending". An unknown <c>targetEmail</c> is
    /// <c>404 "User not found. They must register on Tebrazi first."</c>, and a pairing that is not
    /// physician-to-patient is a 400.</para>
    ///
    /// <para>Clinic precedence is <c>body.clinicId || X-Clinic-Id</c> (:143) — <b>body first</b>,
    /// and <c>?clinicId=</c> is NOT consulted on this route, which is why the header is bound raw
    /// here. The raw <c>userType</c> claim is forwarded unparsed: it is compared as a string against
    /// "PHYSICIAN" and "PATIENT", and the third branch is reached by every value that is neither,
    /// including one the <c>UserType</c> enum does not have.</para>
    /// </summary>
    [HttpPost("request")]
    [ProducesResponseType<ConnLinkRequestCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ConnLinkRequestResentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestConnection(
        [FromHeader(Name = HttpClinicContext.HeaderName)] string? clinicHeaderId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnLinkRequestBody? request)
    {
        var outcome = await Send(new ConnLinkRequestCommand(
            UserId, currentUser.UserType, clinicHeaderId, request));

        return outcome.Created
            ? CreatedPayload(outcome.CreatedBody!)
            : Payload(outcome.ResentBody!);
    }

    /// <summary>
    /// The physician dashboard's clinical roll-up (connections.js:1042-1131).
    ///
    /// <para><b>A BARE DICTIONARY KEYED BY PATIENT USER ID</b> — not an array and not an envelope;
    /// the client indexes it directly. There are no inputs at all: no query string, no pagination
    /// and no clinic header, so the response is a pure function of the caller's identity, and the
    /// only non-200 is this route's own <c>500 {"error":"Failed to load summaries"}</c>.</para>
    /// </summary>
    [HttpGet("patient-summaries")]
    [ProducesResponseType<IReadOnlyDictionary<string, ConnLinkPatientSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PatientSummaries()
        => Payload(await Send(new ConnLinkPatientSummariesQuery()));

    // ── Dependant edges ──────────────────────────────────────────────────────

    /// <summary>
    /// A patient shares one of their family subprofiles with a doctor they are already connected to
    /// (connections.js:310-369). The row is created ACCEPTED immediately, with no handshake and no
    /// notification, because the patient's own accepted parent edge is the consent.
    ///
    /// <para><b>201, NOT 200.</b> connections.js:364 is <c>res.status(201).json(connection)</c>.
    /// This is easy to miss: the four sibling writes in the lifecycle group are all 200s, and the
    /// client discards the body and re-fetches either way, so the wrong code would go
    /// unnoticed.</para>
    ///
    /// <para>Five gates in Node's order, and the order is on the wire: either field falsy → 400;
    /// no <c>patientProfile</c> → <b>400</b> "Patient profile not found" (400, not 404 — the one
    /// status in this handler that reads like a mistake and is not); the dependant missing OR
    /// belonging to someone else → 404 "Family member not found", deliberately indistinguishable as
    /// an enumeration defence; no ACCEPTED PARENT edge with that doctor → 400; the triple already
    /// assigned → 409.</para>
    /// </summary>
    [HttpPost("assign-subprofile")]
    [ProducesResponseType<ConnSubprofileAssignResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignSubprofile(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnSubprofileAssignBody? request)
        => CreatedPayload(await Send(new ConnSubprofileAssignCommand(UserId, request)));

    /// <summary>
    /// Revokes one dependant's edge to one doctor (connections.js:375-390), leaving the parent edge
    /// untouched. 200 with a one-key <c>{ message }</c>; the deleted row is never echoed.
    ///
    /// <para><b>⚠ THE SEGMENT ORDER IS <c>{subprofileId}</c> FIRST, <c>{physicianUserId}</c>
    /// SECOND</b> (:375), and the command's parameter order matches the URL for exactly that reason.
    /// Both are opaque id strings of the same shape, so swapping them compiles, binds, runs, finds
    /// nothing and answers a plausible <c>404 "Assignment not found"</c> forever. The client sends
    /// <c>/unassign-subprofile/${subprofileId}/${physicianUserId}</c> (ConnectionsPage.jsx:254,
    /// FamilyPage.jsx:125).</para>
    ///
    /// <para>The lookup is scoped to the caller as the patient, so "no such assignment" and "not
    /// yours" collapse into the same 404. Two segments under a literal first segment, so this never
    /// competes with <see cref="Delete"/>.</para>
    /// </summary>
    [HttpDelete("unassign-subprofile/{subprofileId}/{physicianUserId}")]
    [ProducesResponseType<ConnSubprofileUnassignResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnassignSubprofile(string subprofileId, string physicianUserId)
        => Payload(await Send(
            new ConnSubprofileUnassignCommand(subprofileId, physicianUserId, UserId)));

    // ── Discovery: search, phone, create, invite ─────────────────────────────

    /// <summary>
    /// People search (connections.js:443-518) — a BARE JSON ARRAY, enriched with the caller's
    /// existing connection state.
    ///
    /// <para><c>?q=</c> shorter than two UTF-16 code units, or absent, is <b>200 with an empty
    /// array</b> (:446-448), never a 400. The only non-200 is this route's own
    /// <c>500 {"error":"Search failed"}</c>.</para>
    ///
    /// <para><b>Clinic precedence here is <c>?clinicId= || X-Clinic-Id</c> (:455) — QUERY
    /// FIRST</b>, the opposite of <c>GET /</c>. The axios interceptor sends the header on
    /// every request once a clinic is active (client/src/services/api.js:31), so the difference is
    /// reachable whenever a caller also passes <c>?clinicId=</c>;
    /// <see cref="IClinicContext"/> resolves header-first and would pick the wrong one.</para>
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType<IReadOnlyList<ConnDiscoverSearchItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] string? clinicId,
        [FromHeader(Name = HttpClinicContext.HeaderName)] string? clinicHeaderId)
        => Payload(await Send(new ConnDiscoverSearchQuery(q, clinicId, clinicHeaderId)));

    /// <summary>
    /// <b>An endpoint connections.js does not implement.</b> The patient onboarding wizard's "find
    /// your doctor" step calls it and silently does nothing today; implemented for the same reason
    /// as <see cref="Connect"/>, its sibling — the two complete one screen.
    ///
    /// <para>A BARE JSON ARRAY of physicians. Same gate as <see cref="Search"/>: a term shorter than
    /// two characters is <c>200 []</c>. No clinic input of any kind — the wizard's caller is a
    /// patient with no clinic context — so no header is bound here.</para>
    /// </summary>
    [HttpGet("search-physicians")]
    [ProducesResponseType<IReadOnlyList<ConnDiscoverPhysicianSearchItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchPhysicians([FromQuery] string? q)
        => Payload(await Send(new ConnDiscoverSearchPhysiciansQuery(q)));

    /// <summary>
    /// A physician looks a patient up by phone number and connects to them in one call
    /// (connections.js:529-615).
    ///
    /// <para><b>NO GENERIC ON THE 200: three success branches with three INCOMPATIBLE key sets.</b>
    /// No account holds the number → <c>{ found: false, phone }</c> with no <c>patient</c> key at
    /// all; an edge already exists → an "already connected" shape and nothing is created; otherwise
    /// the created edge. The handler's result type is <c>object</c> and the value is passed through
    /// untyped — never cast it to one of the three.</para>
    ///
    /// <para>The gate is the raw <c>userType</c> claim compared as a string: anything other than the
    /// exact value "PHYSICIAN", a null claim included, is
    /// <c>403 "Only physicians can add patients"</c>. A falsy <c>phone</c> is then
    /// <c>400 "Phone number is required"</c>, and a NUMERIC <c>phone</c> passes that gate and dies
    /// inside the route's own catch as a 500 — which is why the body member is a raw
    /// <c>JsonElement</c> and why this action allows an empty body.</para>
    /// </summary>
    [HttpPost("add-by-phone")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddByPhone(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnDiscoverAddByPhoneBody? request)
        => Payload(await Send(
            new ConnDiscoverAddByPhoneCommand(UserId, currentUser.UserType, request)));

    /// <summary>
    /// A physician or receptionist creates a whole PATIENT ACCOUNT for somebody who has never used
    /// the app, and is connected to it immediately (connections.js:617-735).
    ///
    /// <para><b>200, NOT 201.</b> connections.js:721 is a plain <c>res.json(...)</c>, unlike its
    /// near-neighbour <see cref="CreateClinicPatient"/> (:802), which is a 201 for a very similar
    /// operation. The asymmetry is in the source and is reproduced, not harmonised.</para>
    ///
    /// <para>The error ORDER is exact and testable (:621-654): staff resolution runs FIRST, so a
    /// receptionist with no clinic id gets <c>400 "clinicId is required for staff"</c> even with a
    /// completely empty body; then <c>403 "Not authorized"</c> for a non-staff caller, then
    /// <c>404 "Clinic physician not found"</c>, then <c>400 "Patient name is required"</c>, then
    /// <c>400 "Phone or email is required"</c>, then the duplicate probe's 409.</para>
    ///
    /// <para>Clinic precedence is <c>body.clinicId || X-Clinic-Id</c> (:627) — <b>body first</b>,
    /// and read ONLY on the staff path; a physician caller's body <c>clinicId</c> is ignored
    /// entirely. The raw <c>userType</c> claim decides which path runs: exactly "PHYSICIAN" takes
    /// the direct path, everything else the staff fallback.</para>
    /// </summary>
    [HttpPost("create-patient")]
    [ProducesResponseType<ConnDiscoverCreatePatientResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreatePatient(
        [FromHeader(Name = HttpClinicContext.HeaderName)] string? clinicHeaderId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnDiscoverCreatePatientBody? request)
        => Payload(await Send(new ConnDiscoverCreatePatientCommand(
            UserId, currentUser.UserType, clinicHeaderId, request)));

    /// <summary>
    /// Mails a signup invitation carrying the caller's id in the link (connections.js:969-1009).
    ///
    /// <para><b>The route never checks that the caller is a physician</b>, so any authenticated user
    /// can send a "Dr. {my name} invited you" email with their own id embedded in the signup link.
    /// Reproduced, not gated. A falsy <c>email</c> is <c>400 "Email is required"</c>; the value is
    /// never validated as an address and goes straight to the mail transport, so a malformed one
    /// either bounces silently or surfaces as this route's 500.</para>
    /// </summary>
    [HttpPost("invite-email")]
    [ProducesResponseType<ConnDiscoverInviteEmailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> InviteEmail(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnDiscoverInviteEmailBody? request)
        => Payload(await Send(new ConnDiscoverInviteEmailCommand(UserId, request)));

    /// <summary>
    /// The shareable signup link for the calling physician (connections.js:1011-1040). No parameters
    /// of any kind — no query string, no body, no header — so the response is a pure function of the
    /// caller's identity and the configured client URL, and the only non-200 is this route's own
    /// <c>500 {"error":"Failed to generate link"}</c>.
    ///
    /// <para>Its origin falls back to port <b>5174</b> where <see cref="QrCode"/> falls back to
    /// <b>5173</b>. That difference is in the Node source and is not a typo.</para>
    /// </summary>
    [HttpGet("invite-link")]
    [ProducesResponseType<ConnDiscoverInviteLinkResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> InviteLink()
        => Payload(await Send(new ConnDiscoverInviteLinkQuery()));

    // ── Walk-in charts ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a physician-owned chart for a walk-in patient who has no Tebrazi account
    /// (connections.js:747-807). <b>201</b> <c>{ success, patient, message }</c>.
    ///
    /// <para>The gate ORDER differs from <see cref="CreatePatient"/>'s even though the two routes
    /// look alike: here <c>400 "Patient name is required"</c> comes FIRST (:751), before staff
    /// resolution, so an empty body from a receptionist answers the name error rather than the
    /// clinic error. The clinic-missing literal is <c>404 "Clinic not found"</c> — a DIFFERENT
    /// literal from <c>create-patient</c>'s "Clinic physician not found".</para>
    ///
    /// <para>Clinic precedence is <c>body.clinicId || X-Clinic-Id</c> (:768) — <b>body first</b>. On
    /// the PHYSICIAN path the body is the only source and the header is not consulted at all.</para>
    /// </summary>
    [HttpPost("clinic-patients")]
    [ProducesResponseType<ConnClinicPatientCreateResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateClinicPatient(
        [FromHeader(Name = HttpClinicContext.HeaderName)] string? clinicHeaderId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnClinicPatientCreateBody? request)
        => CreatedPayload(await Send(new ConnClinicPatientCreateCommand(
            UserId, currentUser.UserType, clinicHeaderId, request)));

    /// <summary>
    /// The caller's physician's ACTIVE walk-in charts (connections.js:813-849) — a BARE JSON ARRAY
    /// ordered by name ascending, each with a <c>_count.visits</c> appended. No 4xx: a caller with
    /// no resolvable physician gets an empty array, and the only failure is this route's own
    /// <c>500 {"error":"Failed to load clinic patients"}</c>.
    ///
    /// <para><b>Clinic precedence is <c>?clinicId= || X-Clinic-Id</c> (:822) — QUERY FIRST</b>, and
    /// read ONLY on the non-physician path; a physician's list is never scoped by clinic at
    /// all.</para>
    /// </summary>
    [HttpGet("clinic-patients")]
    [ProducesResponseType<IReadOnlyList<ConnClinicPatientListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClinicPatients(
        [FromQuery] string? clinicId,
        [FromHeader(Name = HttpClinicContext.HeaderName)] string? clinicHeaderId)
        => Payload(await Send(new ConnClinicPatientListQuery(clinicId, clinicHeaderId)));

    /// <summary>
    /// A <b>HARD</b> delete of one walk-in chart and of every visit and appointment attached to it
    /// (connections.js:860-893), answering <b>200</b> <c>{ success, message }</c>.
    ///
    /// <para>The caller's user id is the ONLY authorization input — there is no <c>userType</c>
    /// branch and no staff fallback, so a receptionist can never delete a chart even at their own
    /// clinic. Ownership and existence collapse into one
    /// <c>404 "Patient file not found or not authorized"</c>.</para>
    ///
    /// <para>Two segments, so it never competes with <see cref="Delete"/>. A
    /// <c>DELETE /api/connections/clinic-patients</c> with no chart id has no route of its own here,
    /// as it has none in Node, and binds to <see cref="Delete"/> with
    /// <c>id = "clinic-patients"</c> — reaching that handler's own 404 rather than a route-miss 404
    /// or a 405. Do not add an action to "tidy" it.</para>
    /// </summary>
    [HttpDelete("clinic-patients/{id}")]
    [ProducesResponseType<ConnClinicPatientDeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteClinicPatient(string id)
        => Payload(await Send(new ConnClinicPatientDeleteCommand(id, UserId)));

    // ── Pairing: PIN and QR ──────────────────────────────────────────────────

    /// <summary>
    /// Issues a short-lived four-character PIN a patient can redeem to connect
    /// (connections.js:1515-1596). 200.
    ///
    /// <para><b>The entry gate is the ABSENCE OF A PHYSICIAN PROFILE ROW, not the userType
    /// claim</b> (:1522-1523) — the only one of the file's seven staff fallbacks that works that
    /// way, which is why this command takes no <c>userType</c> parameter while five siblings do. A
    /// caller with no profile falls through to the staff branch and can reach
    /// <c>403 "Only physicians or clinic staff can generate PINs"</c>,
    /// <c>403 "Not authorized for this clinic"</c> or <c>404 "Clinic physician not found"</c>.
    /// Exhausting the PIN space is a <c>500 "Could not generate unique PIN, try again"</c>, not a
    /// 409.</para>
    ///
    /// <para><b>THE LIVE EMPTY-BODY PATH.</b> ConnectionsPage.jsx:315 posts
    /// <c>{ clinicId: undefined }</c>, which axios serializes to <c>{}</c>. Without
    /// <c>EmptyBodyBehavior.Allow</c> a <c>Content-Length: 0</c> variant of that request answers the
    /// binder's <c>400 {"error":"Validation failed"}</c> and the physician can never generate a
    /// PIN.</para>
    ///
    /// <para>Clinic precedence is <c>body.clinicId || X-Clinic-Id || null</c> (:1518) with <b>no
    /// query arm at all</b>. <see cref="IClinicContext"/> would add <c>?clinicId=</c> as a source
    /// and turn a 403 into a successful staff pin.</para>
    /// </summary>
    [HttpPost("generate-pin")]
    [ProducesResponseType<ConnPinGenerateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GeneratePin(
        [FromHeader(Name = HttpClinicContext.HeaderName)] string? clinicHeaderId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnPinGenerateBody? request)
        => Payload(await Send(new ConnPinGenerateCommand(UserId, clinicHeaderId, request)));

    /// <summary>
    /// The patient redeems a PIN (connections.js:1608-1725). <b>200 on all three success
    /// branches.</b>
    ///
    /// <para><b>NO GENERIC ON THE 200: three INCOMPATIBLE key sets</b>, and the differences are in
    /// the KEYS PRESENT rather than in null values — the "connected" body is
    /// <c>{ message, connection, physician }</c> with no <c>alreadyConnected</c> key, while the
    /// "already connected" body is <c>{ message, alreadyConnected, connection }</c> with no
    /// <c>physician</c> key. The handler's result type is <c>object</c> and the value is passed
    /// through untyped — never cast it.</para>
    ///
    /// <para>A PIN that is falsy or not exactly four characters is
    /// <c>400 "Please enter a 4-digit PIN"</c> — a JavaScript expression over an unvalidated value,
    /// so a number, an object and a boolean all reach it through <c>undefined.length</c>, which is
    /// why the body member is a raw <c>JsonElement</c>. An unknown or expired code is
    /// <c>404 "Invalid or expired PIN. Ask your doctor for a new code."</c>, and a physician
    /// redeeming their own is <c>400 "Cannot connect to yourself"</c>. The PIN is burned BEFORE the
    /// status test, so a code redeemed against an existing connection is spent.</para>
    ///
    /// <para>No clinic input: the pin row already carries the clinic it was issued for.</para>
    /// </summary>
    [HttpPost("connect-by-pin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConnectByPin(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnPinConnectBody? request)
        => Payload(await Send(new ConnPinConnectCommand(UserId, request)));

    /// <summary>
    /// The calling physician's connect QR code (connections.js:1793-1816). No parameters of any
    /// kind.
    ///
    /// <para><b>IT RETURNS JSON, NOT AN IMAGE.</b> The body is <c>{ qrCode, connectUrl }</c> where
    /// <c>qrCode</c> is a <c>data:image/png;base64,…</c> STRING inside the JSON — the same class of
    /// trap as <c>GET /api/prescriptions/{id}/pdf</c>, which returns <c>text/html</c>. Do NOT set a
    /// content type and do NOT use <c>File(...)</c>: the client assigns the string straight into an
    /// <c>&lt;img src&gt;</c> (ConnectionsPage.jsx:911) and never decodes it.</para>
    ///
    /// <para>The base64 payload is not byte-identical to Node's and cannot be — two encoders choose
    /// different mask patterns and emit different PNG chunk layouts — but the media type, the
    /// <c>data:</c> URL shape and the DECODED string are identical, which is the whole of what a
    /// client and a scanner observe. Recorded in docs/connections-surface.md §11.7.</para>
    /// </summary>
    [HttpGet("qr-code")]
    [ProducesResponseType<ConnPinQrCodeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> QrCode()
        => Payload(await Send(new ConnPinQrCodeQuery()));

    // ── One connection: the two transitions and DELETE ───────────────────────
    //
    // Declared last, after every literal. All three fetch the row FIRST with no ownership predicate,
    // so a fake id is 404 before authorization is considered — which lets any authenticated caller
    // probe connection-id existence. None of the three reads a body, so none takes a [FromBody]
    // parameter and none carries an inferred consumes constraint: the client calls accept and reject
    // as bare `api.put(url)`, which axios sends with no Content-Type at all.

    /// <summary>
    /// Accepts a pending connection request (connections.js:240-271).
    ///
    /// <para><b>FOUR gates and the ORDER is on the wire</b>; reordering any pair changes the status
    /// a real client sees: <c>404 "Connection not found"</c> (:246); then
    /// <c>400 "Cannot accept your own request"</c> when the caller is <c>initiatedBy</c> (:250) —
    /// this PRECEDES the membership test, so the requester learns "cannot accept your own" rather
    /// than "access denied"; then <c>403 "Access denied"</c> for anyone who is neither end of the
    /// edge (:255); then <c>400 "Connection is already {status.toLowerCase()}"</c>, an INTERPOLATED
    /// literal, e.g. "Connection is already accepted" (:259).</para>
    /// </summary>
    [HttpPut("{id}/accept")]
    [ProducesResponseType<ConnLinkAcceptResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Accept(string id)
        => Payload(await Send(new ConnLinkAcceptCommand(id, UserId)));

    /// <summary>
    /// Rejects a connection request (connections.js:277-296).
    ///
    /// <para><b>TWO gates, not four, and the asymmetry with <see cref="Accept"/> is the
    /// contract</b>: <c>404 "Connection not found"</c> (:282) then <c>403 "Access denied"</c>
    /// (:284-286), and nothing else. There is NO <c>initiatedBy</c> gate, so the requester can
    /// reject their own request, and there is no status precondition, so an already-ACCEPTED edge
    /// can be rejected. The body is the updated row plus a <c>message</c> key.</para>
    /// </summary>
    [HttpPut("{id}/reject")]
    [ProducesResponseType<ConnLinkRejectResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reject(string id)
        => Payload(await Send(new ConnLinkRejectCommand(id, UserId)));

    /// <summary>
    /// Disconnects two parties (connections.js:401-433). <b>It HARD deletes</b> —
    /// <c>prisma.doctorPatientConnection.delete</c> (:427), not a status write; <c>REMOVED</c>
    /// exists in the enum and no route in the file ever assigns it. Either party may do it.
    ///
    /// <para><b>The route parameter is not only a connection id.</b> The handler first treats it as
    /// a CONNECTION id and, failing that, as the OTHER PARTY'S USER id (:405-420) — both are live
    /// client usages, which is why the command's parameter is named <c>ConnectionOrUserId</c> and
    /// why <c>{id}</c> carries no route constraint. The gates are
    /// <c>404 "Connection not found"</c> then <c>403 "Access denied"</c>.</para>
    ///
    /// <para>This action also serves <c>DELETE /api/connections/clinic-patients</c> with no chart
    /// id, as <c>id = "clinic-patients"</c>, exactly as Express does — see
    /// <see cref="DeleteClinicPatient"/>.</para>
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType<ConnLinkDeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id)
        => Payload(await Send(new ConnLinkDeleteCommand(id, UserId)));
}

// ─────────────────────────────────────────────────────────────────────────────
// No controller-owned request records, on purpose.
//
// All nine bodies bind straight to the records the Connections Application layer already publishes
// and its commands already consume — ConnDiscoverConnectBody, ConnLinkRequestBody,
// ConnSubprofileAssignBody, ConnDiscoverAddByPhoneBody, ConnDiscoverCreatePatientBody,
// ConnDiscoverInviteEmailBody, ConnClinicPatientCreateBody, ConnPinGenerateBody and
// ConnPinConnectBody. Every member of all nine is a NON-NULLABLE JsonElement, and that is the
// subtlest part of the port: JsonElement distinguishes an ABSENT key (ValueKind.Undefined) from an
// explicit null, which JsonElement? cannot, and it lets a wrongly-typed value reach the handler's
// own guard — where Node produces a 500 through `undefined.length` or a Prisma type error — rather
// than the binder's 400. Re-declaring any of them here would invite exactly that drift.
//
// Every one of the nine carries [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] because
// express.json() hands each Node handler `{}` for a body-less request and each command's `Body?` is
// written for that. Eight of the nine then reach their own documented 400; the ninth, generate-pin,
// reaches a SUCCESS with no body at all, and it is the live one — ConnectionsPage.jsx:315 posts
// { clinicId: undefined }, which axios serializes to {}. A body-less request that ALSO omits
// Content-Type does not 415 on these actions either: with AllowEmptyBody set, BodyModelBinder finds
// no formatter for the missing content type, consults IHttpRequestBodyDetectionFeature.CanHaveBody,
// and binds a null model instead of throwing the UnsupportedContentTypeException that
// UnsupportedContentTypeFilter renders as a 415.
//
// X-Clinic-Id is bound RAW, with [FromHeader(Name = HttpClinicContext.HeaderName)], on SIX actions:
// RequestConnection, Search, CreatePatient, CreateClinicPatient, ClinicPatients and GeneratePin.
// IClinicContext is NOT injected for any of them. It collapses header-then-query into one trimmed
// value, and connections.js resolves a clinic seven different ways (docs/connections-surface.md
// §10.2): only GET / is header-first, three are body-first, two are query-first, and POST /request
// and POST /generate-pin read no query arm at all. GET / is therefore the one route whose handler
// does inject IClinicContext — its precedence and the kernel service's are identical — and List()
// takes no header parameter as a result.
//
// Two status codes above differ from the summary this controller was commissioned from, and Node is
// the authority on both: POST /assign-subprofile is 201 (connections.js:364,
// res.status(201).json(connection)) and POST /create-patient is 200 (connections.js:721, a plain
// res.json). ConnSubprofileAssignResponse and ConnDiscoverCreatePatientHandler document the same
// two values in their own XML docs, so the handlers and the source agree.
// ─────────────────────────────────────────────────────────────────────────────
