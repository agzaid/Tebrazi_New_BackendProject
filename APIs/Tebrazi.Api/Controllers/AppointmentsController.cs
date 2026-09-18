using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Tebrazi.Appointments.Application.ApiModels.Responses;
using Tebrazi.Appointments.Application.UseCases;
using Tebrazi.Common.Api.Controllers;
using Tebrazi.Common.Api.Security;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Api.Controllers;

/// <summary>
/// <c>/api/appointments</c> — the port of <c>server/src/routes/appointments.js</c>, all twenty
/// router registrations.
///
/// <para>Route ORDER: the eight literal templates (<c>slots</c>, <c>slots/bulk</c>,
/// <c>slots/sync-from-hours</c>, <c>slots/{id}</c>, <c>available</c>, <c>today</c>, <c>queue</c>
/// and <c>ai-optimize</c>), plus the single-segment literal POSTs <c>recurring</c> and
/// <c>walk-in</c>, are all declared before the <c>{id}</c> family. ASP.NET prefers a literal over
/// a parameter regardless of declaration order, so this is legibility rather than correctness —
/// but do NOT "fix" the collision with a route constraint such as <c>{id:guid}</c>:
/// <c>Appointment.id</c> is unconstrained text and a guid constraint would turn today's
/// <c>404 {"error":"Appointment not found"}</c> into a routing 404 with a different body.</para>
///
/// <para>NO <c>{id}/{action}</c> CATCH-ALL, EVER. <c>docs/port-contracts/audit-2.json</c>'s
/// <c>routeOrderHazards</c> records why: <c>POST /slots/bulk</c> and
/// <c>POST /slots/sync-from-hours</c> share a segment count with
/// <c>POST /{id}/intake-note</c>, and only the differing second literal keeps them apart. The same
/// goes for the five <c>PUT /{id}/&lt;literal&gt;</c> transitions — and the kebab-case spellings
/// <c>no-show</c> and <c>check-in</c> are exactly what the client sends.</para>
///
/// <para>ONE ROUTE-TABLE BEHAVIOUR IS INHERITED RATHER THAN WRITTEN. <c>DELETE /slots</c> with no
/// id has no route of its own in Node and binds to <c>DELETE /{id}</c> with <c>id = "slots"</c>,
/// answering <c>404 {"error":"Appointment not found"}</c> — not a route-miss 404 and not a 405.
/// This table reproduces that: the path matches both the <c>slots</c> literal (GET and POST only)
/// and <c>{id}</c>, and because <c>{id}</c> does serve DELETE the method policy never synthesises
/// a 405, so the request reaches <see cref="Delete"/> with <c>id = "slots"</c> and the handler's
/// own not-found. Do not add a <c>DELETE slots</c> action to "tidy" it.</para>
///
/// <para>Every read query resolves the caller through its own injected
/// <see cref="ICurrentUser"/>, so none of the five takes a user id. Every write takes it
/// explicitly, which is why <see cref="UserId"/> appears only on the writes.</para>
///
/// <para>Two Node behaviours the module deliberately does NOT reproduce, both recorded in
/// <c>docs/PORT-STATUS.md</c>: <c>cacheMiddleware(15)</c> on <c>GET /</c> and <c>GET /today</c>
/// (whose cache key excludes the <c>X-Clinic-Id</c> header <c>GET /</c> branches on, serving a
/// staff user the previous clinic's array for up to 15 seconds), and the global 100 req/min/IP
/// rate limiter's 429.</para>
/// </summary>
[Route("api/appointments")]
[Authorize]
public sealed class AppointmentsController(IMediator mediator, ICurrentUser currentUser)
    : BaseApiController(mediator)
{
    private string UserId => currentUser.UserId ?? throw new UnauthorizedException("Invalid token");

    // ── Slots ────────────────────────────────────────────────────────────────
    //
    // Declared first because `slots` collides with {id} for DELETE. The three writing routes give
    // three different answers to "no physician profile" and two different answers to "not your
    // clinic"; the handlers own all of that, the controller only routes.

    /// <summary>
    /// The caller's own time slots, newest weekday first. Pinned to the caller's PhysicianProfile,
    /// so a patient, a staff member, an unknown <c>?clinicId</c> and another physician's clinic id
    /// all answer <c>200 []</c> — never 403, never 404.
    ///
    /// <para><c>clinicId</c> is gated on JavaScript truthiness, so <c>?clinicId=</c> is an ABSENT
    /// filter rather than a filter on the empty string. It binds as <c>string?</c> for that
    /// reason.</para>
    /// </summary>
    [HttpGet("slots")]
    [ProducesResponseType<IReadOnlyList<SlotListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Slots([FromQuery] string? clinicId)
        => Payload(await Send(new SlotListQuery(clinicId)));

    /// <summary>
    /// Creates slots from an explicit array of windows. <b>201</b>, with the same
    /// <c>{ count, message }</c> envelope as <c>POST /slots/bulk</c>.
    ///
    /// <para>LIVE BUT UNUSED: <c>audit-2.json</c>'s <c>missedRoutes</c> entry is the only record of
    /// it, and no caller exists anywhere in <c>client/src</c>. Mapped for parity — it is a real
    /// registration at appointments.js:57-88, not dead code.</para>
    ///
    /// <para>The ONLY slot-creating route with no <c>deleteMany</c>: it APPENDS, so posting the
    /// same array twice leaves two identical rows and there is no unique constraint to stop it.
    /// A missing physician profile is a 400 here, not a 403; a clinic the physician does not own
    /// is a 403 that also covers a clinic that does not exist.</para>
    /// </summary>
    [HttpPost("slots")]
    [ProducesResponseType<SlotCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateSlots(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SlotCreateBody? request)
        => CreatedPayload(await Send(new SlotCreateCommand(UserId, request)));

    /// <summary>
    /// Wipes ONE weekday's slots at one clinic and regenerates them by walking
    /// <c>[startTime, endTime]</c> in <c>slotDuration</c> steps. <b>201</b> with a count, never
    /// the rows.
    ///
    /// <para>DESTRUCTIVE-THEN-FAIL is the contract: a time range too short to yield a single slot
    /// answers <c>400 {"error":"Time range too short for slot duration"}</c> AFTER the delete has
    /// already committed, so the weekday is left empty. Do not reorder the guard.</para>
    ///
    /// <para><c>AppointmentsPage</c> fires one request per selected weekday through
    /// <c>Promise.all</c>, so selecting seven days is seven requests.</para>
    /// </summary>
    [HttpPost("slots/bulk")]
    [ProducesResponseType<SlotBulkCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateBulkSlots(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SlotBulkCreateBody? request)
        => CreatedPayload(await Send(new SlotBulkCreateCommand(UserId, request)));

    /// <summary>
    /// Replaces a clinic's ENTIRE weekly grid from a structured schedule and writes that schedule
    /// back onto <c>Clinic.workingHours</c>. <b>200</b> — not the 201 its sibling uses — with a
    /// count that is legitimately 0.
    ///
    /// <para>The wipe is NOT day-scoped, so a schedule containing Monday alone erases Tue–Sun,
    /// deactivated rows included. An EMPTY array is truthy in JavaScript and is therefore a legal
    /// "delete everything" request that passes the 400 guard — the opposite of
    /// <c>POST /slots</c>, which rejects an empty array. Per-entry <c>slotDuration</c> defaults to
    /// 15 here against 30 in its two siblings.</para>
    /// </summary>
    [HttpPost("slots/sync-from-hours")]
    [ProducesResponseType<SlotSyncFromHoursResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SyncSlotsFromHours(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SlotSyncFromHoursBody? request)
        => Payload(await Send(new SlotSyncFromHoursCommand(UserId, request)));

    /// <summary>
    /// Despite the verb, nothing is deleted: the row survives with <c>isActive = false</c> and only
    /// the two generation routes ever physically remove it.
    ///
    /// <para>GATE ORDER IS OBSERVABLE AND MUST STAY: the slot is fetched FIRST, so a fake id
    /// answers <c>404 "Slot not found"</c> while a real one the caller does not own answers
    /// <c>403 "Not your slot"</c> — which lets any authenticated caller probe slot-id existence.
    /// Both strings are route-specific and differ from the appointment routes' bare
    /// "Not authorized".</para>
    ///
    /// <para><c>AppointmentsPage</c>'s "Clear all slots" fires one request PER SLOT through
    /// <c>Promise.all</c>, so a week of 15-minute slots is hundreds of parallel deletes.</para>
    /// </summary>
    [HttpDelete("slots/{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSlot(string id)
        => Payload(await Send(new SlotDeleteCommand(id, UserId)));

    // ── Literal read routes ──────────────────────────────────────────────────

    /// <summary>
    /// The bookable slots for one physician on one date, deduplicated by <c>startTime</c> and each
    /// flagged <c>isBooked</c>.
    ///
    /// <para><c>physicianUserId</c> is the USER id, not the PhysicianProfile id, and it and
    /// <c>date</c> are the only required parameters — a falsy either is
    /// <c>400 {"error":"physicianUserId and date are required"}</c>. Everything else that could go
    /// wrong answers <c>200 []</c>: an unknown physician, and a clinic with
    /// <c>allowPatientBooking = false</c> called by neither the physician nor its staff. A
    /// malformed <c>date</c> is a 500, not a 400, which is why all three bind as <c>string?</c> —
    /// a <c>DateTime?</c> binding would answer 400 for input Node hands to the handler.</para>
    ///
    /// <para>Omitting <c>clinicId</c> merges slots from ALL of the physician's clinics AND skips
    /// the <c>allowPatientBooking</c> gate entirely, so a booking-disabled clinic's slots go to any
    /// authenticated caller. That hole is the contract.</para>
    /// </summary>
    [HttpGet("available")]
    [ProducesResponseType<IReadOnlyList<ApptReadAvailableSlot>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Available(
        [FromQuery] string? physicianUserId,
        [FromQuery] string? clinicId,
        [FromQuery] string? date)
        => Payload(await Send(new ApptReadAvailableQuery(physicianUserId, clinicId, date)));

    /// <summary>
    /// The calling physician's own appointments for today, ordered by start time. Reads no
    /// parameters at all.
    ///
    /// <para>There is no userType check and no 403: a patient or a receptionist gets
    /// <c>200 []</c>, because the query is pinned to the caller's PhysicianProfile and a caller
    /// without one short-circuits to the empty list.</para>
    /// </summary>
    [HttpGet("today")]
    [ProducesResponseType<IReadOnlyList<ApptReadTodayItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Today()
        => Payload(await Send(new ApptReadTodayQuery()));

    /// <summary>
    /// The reception queue for one clinic: today's appointments with their check-in state and
    /// today's visit overlay, plus a <c>summary</c> envelope.
    ///
    /// <para><c>clinicId</c> is REQUIRED — a falsy value is
    /// <c>400 {"error":"clinicId required"}</c>, the only 400 on any GET in this file. The 403
    /// gate is clinic OWNERSHIP or an active staff row, so a physician who merely works at the
    /// clinic gets <c>403 {"error":"Not staff at this clinic"}</c>.</para>
    ///
    /// <para>The <c>summary</c> buckets are NOT disjoint and will not reconcile: an un-checked-in
    /// NO_SHOW is counted in both <c>pending</c> and <c>noShow</c>, so
    /// <c>total != arrived + pending + completed</c>. The client renders those numbers; a port that
    /// "fixes" the arithmetic changes them.</para>
    /// </summary>
    [HttpGet("queue")]
    [ProducesResponseType<ApptReadQueueResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Queue([FromQuery] string? clinicId)
        => Payload(await Send(new ApptReadQueueQuery(clinicId)));

    /// <summary>
    /// Scheduling statistics over the caller's last 90 days, plus AI insights. Reads no parameters.
    ///
    /// <para>The ONLY GET in this file that 403s: a caller with no PhysicianProfile gets
    /// <c>403 {"error":"Only physicians can access scheduling insights"}</c> where
    /// <c>GET /slots</c>, <c>GET /today</c> and <c>GET /</c> all answer <c>200 []</c> for the same
    /// caller. Harmonising them breaks one client or the other.</para>
    ///
    /// <para>No generic is claimed on the 200 because the handler's result type is
    /// <c>object</c>: the two success bodies differ in the KEYS PRESENT, not in null values —
    /// under five appointments in the window it is <c>{ stats: null, insights: [], message }</c>
    /// with no <c>aiProvider</c> key, otherwise <c>{ stats, insights, aiProvider }</c> with no
    /// <c>message</c>. Only the statistics stage can 500; every AI failure degrades to
    /// <c>insights: []</c> with a 200.</para>
    /// </summary>
    [HttpGet("ai-optimize")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AiOptimize()
        => Payload(await Send(new ApptReadOptimizeQuery()));

    // ── The appointment list and the three creates ───────────────────────────

    /// <summary>
    /// The tri-persona appointment list, a BARE ARRAY hard-capped at 200 rows with no pagination
    /// metadata. Which branch runs is decided by the <c>userType</c> CLAIM: a physician gets their
    /// own appointments, a non-physician with a resolvable staff clinic gets that clinic's, and
    /// everyone else gets the bookings filed against their own user id.
    ///
    /// <para>THE <c>X-Clinic-Id</c> HEADER IS BOUND HERE, SEPARATELY FROM <c>?clinicId</c>, AND
    /// THAT IS DELIBERATE. <see cref="IClinicContext"/> cannot serve this endpoint: it collapses
    /// header-then-query into one value (and trims it), while this handler needs the two
    /// SEPARATELY — the header wins for the staff check, and the query value then OVERWRITES the
    /// resulting filter. Reproducing that needs both, raw, so the header is bound with
    /// <see cref="FromHeaderAttribute"/> under <see cref="HttpClinicContext.HeaderName"/> — the
    /// same constant the collapsing implementation reads, so the two cannot drift apart.</para>
    ///
    /// <para>AUTHORIZATION DEFECT, REPRODUCED, NOT FIXED. A user with an active staff row at
    /// clinic A who sends <c>X-Clinic-Id: A</c> and <c>?clinicId=B</c> passes the staff check on A
    /// and then receives clinic B's appointments, patient-enriched and never re-validated. Flagged
    /// in <c>docs/PORT-STATUS.md</c>.</para>
    ///
    /// <para>Neither <c>status</c> nor <c>date</c> is validated: an unrecognised status and an
    /// unparseable date are both <c>500 {"error":"Failed to list appointments"}</c>. There is NO
    /// 400 on this route, which is why every parameter binds as <c>string?</c>.</para>
    ///
    /// <para>No generic is claimed on the 200: the row shape forks on the branch — the enriched
    /// branches append a <c>patientUser</c> key the patient branch omits entirely — so the
    /// handler's result type is <c>IReadOnlyList&lt;object&gt;</c>.</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? date,
        [FromQuery] string? clinicId,
        [FromHeader(Name = HttpClinicContext.HeaderName)] string? clinicHeaderId)
        => Payload(await Send(new ApptReadListQuery(status, date, clinicId, clinicHeaderId)));

    /// <summary>
    /// The patient self-books a PENDING appointment for THEMSELVES against the physician named in
    /// the body. <b>201</b>.
    ///
    /// <para>There is NO authorization gate of any kind — no userType check, no clinic ownership
    /// check, and <c>allowPatientBooking</c> is never consulted — so any authenticated caller may
    /// book, and the row's <c>patientUserId</c> is always the caller. An unknown
    /// <c>physicianUserId</c> is <c>400 {"error":"Physician not found"}</c>, NOT a 404.</para>
    ///
    /// <para>The 409 collision guard matches on physician + local day + EXACT <c>startTime</c>
    /// string, is not scoped to <c>clinicId</c> and ignores <c>endTime</c>: a booking at the
    /// physician's other clinic blocks this one, and 09:00-09:30 against 09:15-09:45 is never
    /// detected. It is also a check-then-create with no transaction, so two concurrent requests
    /// both 201.</para>
    /// </summary>
    [HttpPost]
    [ProducesResponseType<BookingCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BookingCreateAppointmentBody? request)
        => CreatedPayload(await Send(new BookingCreateAppointmentCommand(UserId, request)));

    /// <summary>
    /// Creates up to twelve PENDING appointments spaced by a fixed day interval, all sharing one
    /// generated <c>recurringGroupId</c>. <b>201</b>.
    ///
    /// <para>Unlike <c>POST /</c> there is NO collision check and NO notification, so a series can
    /// land straight on top of existing PENDING/CONFIRMED rows and never returns 409. The creates
    /// are sequential with no transaction: a failure on occurrence <c>k</c> is a 500 with
    /// <c>k-1</c> orphan rows already persisted under a group id the client never learns.</para>
    ///
    /// <para><c>count</c> is the one body member that is not a string — it stays a raw JSON
    /// element because <c>Math.min(count || 4, 12)</c> is observable: <c>0</c> is falsy and becomes
    /// 4, a negative value creates nothing, and a non-numeric value yields <c>NaN</c> and also
    /// creates nothing, each answering 201 with a different <c>count</c>.</para>
    /// </summary>
    [HttpPost("recurring")]
    [ProducesResponseType<BookingRecurringResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateRecurring(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BookingCreateRecurringBody? request)
        => CreatedPayload(await Send(new BookingCreateRecurringCommand(UserId, request)));

    /// <summary>
    /// Reception (or the physician) creates an immediately CONFIRMED <c>WALK_IN</c> appointment for
    /// a NAMED patient. <b>201</b>.
    ///
    /// <para>The only route in the file with all four of 400, 403, 404 and 500 on the same bad
    /// input, decided by WHO calls: a caller who is neither the physician nor active staff at that
    /// clinic gets <c>403 {"error":"Not authorized"}</c>;
    /// <c>404 {"error":"Clinic not found"}</c> is reachable ONLY on the staff path, because a
    /// physician caller's bad <c>clinicId</c> becomes a foreign-key 500 instead.</para>
    ///
    /// <para><c>reason</c> defaults to the literal "Walk-in" and never null, <c>notes</c> is the
    /// literal "WALK_IN", <c>confirmedAt</c> is always stamped, and an omitted <c>endTime</c> falls
    /// back to the resolved start — producing a zero-length appointment on purpose.</para>
    /// </summary>
    [HttpPost("walk-in")]
    [ProducesResponseType<BookingWalkInResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateWalkIn(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BookingCreateWalkInBody? request)
        => CreatedPayload(await Send(new BookingCreateWalkInCommand(UserId, request)));

    // ── One appointment: the four transitions, the two assistant routes and DELETE ──
    //
    // All seven fetch the appointment FIRST, so a fake id is 404 before authorization is
    // considered. Reading no body means no [FromBody] parameter and therefore no inferred
    // consumes constraint, which is why confirm, complete, no-show and DELETE take nothing at all
    // — the client calls each of them as a bare `api.put(url)`.

    /// <summary>
    /// Flips the status to CONFIRMED, stamps <c>confirmedAt</c> unconditionally — overwriting an
    /// existing timestamp, unlike check-in, which preserves it — and notifies the patient.
    ///
    /// <para>The gate is the owning physician OR any active staff member of the appointment's
    /// clinic. There is no userType check, so a PATIENT-typed user who holds an active staff row
    /// CAN confirm; the booking patient themselves cannot.</para>
    /// </summary>
    [HttpPut("{id}/confirm")]
    [ProducesResponseType<StatusConfirmedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Confirm(string id)
        => Payload(await Send(new StatusConfirmAppointmentCommand(id, UserId)));

    /// <summary>
    /// Moves the status to CANCELLED, stamps <c>cancelledAt</c>, stores <c>cancelReason</c> and
    /// notifies the other party.
    ///
    /// <para>The WIDEST authorization of the four transitions: it includes the booking patient, so
    /// a patient can cancel but cannot confirm, complete or mark a no-show.</para>
    ///
    /// <para>The body is optional in full — a body-less cancel is a perfectly ordinary success —
    /// and <c>reason</c> stays a raw JSON element because the coercions are observable:
    /// <c>0</c>, <c>false</c>, <c>""</c> and <c>null</c> all store null (so the response's
    /// <c>cancelReason</c> is null while the client believes it sent a reason), the STRING
    /// <c>"0"</c> is kept, and a truthy non-string reaches the database as the wrong type and
    /// surfaces as <c>500 {"error":"Failed to cancel"}</c>. A <c>string?</c> member would let the
    /// model binder answer 400 for those instead.</para>
    /// </summary>
    [HttpPut("{id}/cancel")]
    [ProducesResponseType<StatusCancelledResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StatusCancelAppointmentBody? request)
        => Payload(await Send(new StatusCancelAppointmentCommand(id, UserId, request)));

    /// <summary>
    /// Moves the status to COMPLETED and stamps <c>completedAt</c>. Reads no body, and sends no
    /// notification — as does <c>PUT /{id}/no-show</c>: two of the four transitions are silent, so
    /// do not infer that no-show notifies.
    /// </summary>
    [HttpPut("{id}/complete")]
    [ProducesResponseType<StatusCompletedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete(string id)
        => Payload(await Send(new StatusCompleteAppointmentCommand(id, UserId)));

    /// <summary>
    /// Writes the status column and NOTHING else — no timestamp, no notification, no payment void,
    /// no queue update. Reads no body.
    /// </summary>
    [HttpPut("{id}/no-show")]
    [ProducesResponseType<StatusNoShowResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> NoShow(string id)
        => Payload(await Send(new StatusNoShowAppointmentCommand(id, UserId)));

    /// <summary>
    /// Reception marks the patient arrived: the status moves to CONFIRMED, a <c>CHECKIN:{…}</c>
    /// blob is APPENDED to <c>notes</c>, and a consultation fee may be auto-billed. The response is
    /// the updated row plus <c>message</c>, <c>checkedInBy</c> and <c>payment</c>, in that order.
    ///
    /// <para>THIS ROUTE MUST ACCEPT NO BODY AT ALL. <c>ConnectionsPage.jsx:149</c> calls it as a
    /// bare <c>api.put(url)</c> while <c>AssistantDashboardPage.jsx:152</c> sends
    /// <c>{ room }</c>, and both are legal in Node. Hence
    /// <c>EmptyBodyBehavior.Allow</c>: the nullable annotation alone does NOT make an MVC body
    /// optional — <c>BodyModelBinderProvider</c> consults <c>EmptyBodyBehavior</c> and
    /// <c>MvcOptions.AllowEmptyInputInBodyModelBinding</c>, never NRT, and neither is set in
    /// <c>Program.cs</c> — so without it a <c>Content-Length: 0</c> request would answer
    /// <c>400 {"error":"Validation failed","message":"A non-empty request body is required."}</c>
    /// where Node checks in the patient.</para>
    ///
    /// <para>AND THAT LIVE CALL CARRIES NO <c>Content-Type</c> EITHER, so do not reason about it as
    /// a JSON request with an empty payload. The axios instance does declare a default
    /// <c>Content-Type: application/json</c> (<c>client/src/services/api.js:15-17</c>), but axios
    /// 1.x DELETES it again in the xhr adapter whenever <c>data === undefined</c>
    /// (<c>client/node_modules/axios/dist/axios.js:3123-3124</c>,
    /// <c>requestData === undefined &amp;&amp; requestHeaders.setContentType(null)</c>); the null
    /// value is then skipped by <c>AxiosHeaders.toJSON</c> (:2448-2453) and never reaches
    /// <c>setRequestHeader</c> (:3126-3130). A data-less <c>api.put(url)</c> transforms to
    /// <c>JSON.stringify(undefined)</c>, i.e. <c>undefined</c>, so that branch always fires.
    /// <c>EmptyBodyBehavior.Allow</c> is what saves it: with <c>AllowEmptyBody</c> set,
    /// <c>BodyModelBinder</c> finds no formatter for the absent content type, checks
    /// <c>IHttpRequestBodyDetectionFeature.CanHaveBody</c>, sees no body, and short-circuits to a
    /// NULL model instead of throwing <c>UnsupportedContentTypeException</c>. The handler then
    /// reads <c>request.Body?.Room ?? default</c> and gets <c>ValueKind.Undefined</c>, which is
    /// Node's body-less behaviour. Remove <c>Allow</c> and this route 415s, not 400s.</para>
    ///
    /// <para><c>room</c> stays a NON-NULLABLE raw JSON element: a <c>JsonElement?</c> would
    /// collapse an omitted <c>room</c> and an explicit <c>{"room": null}</c> into the same C#
    /// null, and the notes blob writes the key in the second case and drops it in the first.</para>
    ///
    /// <para><c>payment</c> is <c>null</c> on a SECOND check-in even though a PENDING payment
    /// exists, and <c>confirmedAt</c> PRESERVES an existing timestamp — the opposite of
    /// <c>PUT /{id}/confirm</c>. The 403 message is the route-specific
    /// "Not authorized to check in this patient".</para>
    /// </summary>
    [HttpPut("{id}/check-in")]
    [ProducesResponseType<AssistantCheckInResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckIn(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AssistantCheckInBody? request)
        => Payload(await Send(new AssistantCheckInCommand(id, UserId, request)));

    /// <summary>
    /// Reception writes a pre-visit note for the physician to review. <b>200, never 201</b>,
    /// despite the POST — the body is the stored <c>PatientNote</c> row plus a <c>message</c> key.
    ///
    /// <para>The appointment id resolves the patient and the physician and is then discarded: the
    /// stored note carries no reference back to it, so two appointments for the same patient write
    /// to the same note row.</para>
    ///
    /// <para><c>note</c> is required and must be non-empty after trimming
    /// (<c>400 {"error":"Note content required"}</c>, raised BEFORE the appointment lookup, so an
    /// empty body against a nonexistent id answers 400 rather than 404), and the 403 message is
    /// the longer "Not authorized to write intake notes". It stays a raw JSON element because a
    /// non-string <c>note</c> is a <c>500 {"error":"Failed to save intake note"}</c> in Node —
    /// <c>note?.trim</c> is undefined and calling it throws — where a <c>string?</c> binding would
    /// let the model binder answer 400 with a different body.</para>
    ///
    /// <para>PORT TRAP, OWNED ELSEWHERE: <c>PatientNote</c> is unique on
    /// <c>(physicianUserId, patientUserId, subprofileId)</c> and this route always writes a NULL
    /// subprofile. PostgreSQL treats those NULLs as distinct and silently duplicates the row; SQL
    /// Server treats them as equal and would raise a duplicate key on the second call, turning
    /// Node's 200 into a 500. The fix belongs in the index definition, not here — see
    /// <c>audit-2.json</c>.</para>
    /// </summary>
    [HttpPost("{id}/intake-note")]
    [ProducesResponseType<AssistantIntakeNoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> IntakeNote(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AssistantIntakeNoteBody? request)
        => Payload(await Send(new AssistantIntakeNoteCommand(id, UserId, request)));

    /// <summary>
    /// A HARD delete of the row, answering the bare <c>{"message":"Appointment deleted"}</c> — no
    /// id, no count, no echo. Unlike <c>DELETE /api/visits/{id}</c>, which only archives, and
    /// unlike <c>DELETE /slots/{id}</c>, which only deactivates.
    ///
    /// <para>THE PATIENT IS IN THE ALLOW-LIST, so a patient can permanently erase a clinic's
    /// record of their own appointment. A concurrent delete between the lookup and the delete is a
    /// 500, not a 404.</para>
    ///
    /// <para>This action also serves <c>DELETE /api/appointments/slots</c> with no slot id, as
    /// <c>id = "slots"</c> — see the class remarks.</para>
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id)
        => Payload(await Send(new StatusDeleteAppointmentCommand(id, UserId)));
}

// ─────────────────────────────────────────────────────────────────────────────
// No controller-owned request records, on purpose.
//
// All nine bodies bind straight to the records the Appointments Application layer already
// publishes and its commands already consume — SlotCreateBody, SlotBulkCreateBody,
// SlotSyncFromHoursBody, BookingCreateAppointmentBody, BookingCreateRecurringBody,
// BookingCreateWalkInBody, StatusCancelAppointmentBody, AssistantCheckInBody and
// AssistantIntakeNoteBody. Six of them carry JsonElement members whose Undefined-versus-Null
// distinction IS the contract (dayOfWeek 0 versus null in slots/bulk, room in check-in, reason in
// cancel, note in intake-note, count in recurring, schedule in sync-from-hours); re-declaring
// those here would duplicate the subtlest part of the port and invite drift.
//
// Every body parameter carries [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] because
// express.json() hands each Node handler `{}` for a body-less request and each command's `Body?`
// is written for exactly that. Two of the nine reach a SUCCESS with no body at all — cancel
// (reason is optional) and check-in (room is optional, and ConnectionsPage.jsx:149 sends nothing)
// — while the other seven reach their own documented 400 rather than the binder's
// "A non-empty request body is required."
//
// A body-less request that ALSO omits Content-Type does NOT 415 on these nine actions, and one live
// call path takes exactly that shape: ConnectionsPage.jsx:149 checks a patient in with a data-less
// api.put(url), and axios 1.x strips the instance's default Content-Type: application/json whenever
// data === undefined (client/node_modules/axios/dist/axios.js:3123-3124, with the null header value
// dropped by AxiosHeaders.toJSON at :2448-2453). EmptyBodyBehavior.Allow covers it: BodyModelBinder
// finds no formatter for the missing content type, consults
// IHttpRequestBodyDetectionFeature.CanHaveBody, and binds a null model rather than throwing the
// UnsupportedContentTypeException that UnsupportedContentTypeFilter turns into a 415. The 415 is
// only reachable by a request that omits Content-Type while actually carrying a body — no client
// call does that. So Allow is load-bearing here, not defensive: dropping it breaks check-in.
// ─────────────────────────────────────────────────────────────────────────────
