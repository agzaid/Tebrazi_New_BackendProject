using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Tebrazi.Common.Api.Controllers;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Visits.Application.ApiModels.Responses;
using Tebrazi.Visits.Application.UseCases;

namespace Tebrazi.Api.Controllers;

/// <summary>
/// <c>/api/visits</c> — the port of <c>server/src/routes/visits.js</c>.
///
/// <para>Route ORDER: the literal segments (<c>specialty-template</c>,
/// <c>specialty-template/{key}</c>, <c>inbox</c>, <c>follow-ups-due</c> and
/// <c>patient/{patientUserId}</c>) are declared before <c>{id}</c>. ASP.NET prefers a literal
/// over a parameter regardless of declaration order, so this is legibility rather than
/// correctness — but do NOT "fix" the collision with a route constraint such as
/// <c>{id:guid}</c>: <c>Visit.id</c> is unconstrained text, and a guid constraint would turn
/// today's <c>404 {"error":"Visit not found"}</c> into a routing 404 with a different body.</para>
///
/// <para>THREE ROUTES HERE ANSWER 404 IN NODE TODAY, for two different reasons.
/// <c>GET /follow-ups-due</c> is the only one with a handler: it is registered at visits.js:1244,
/// 972 lines AFTER <c>GET /:id</c> at visits.js:272, so <c>/:id</c> shadows it and Node answers
/// <c>404 {"error":"Visit not found"}</c> — a registration that exists but is unreachable.
/// <c>GET /inbox</c> has no handler anywhere in visits.js (the file's only two <c>inbox</c>
/// matches are a comment at L1412 and the patient-dismiss message at L1435) and reaches that same
/// <c>/:id</c> 404 by being parsed as an id. <c>GET /patient/{patientUserId}</c> is two segments,
/// so <c>/:id</c> cannot swallow it either: it matches no route and falls through to the global
/// handler's <c>404 {"error":"Not Found","message":"Route GET … not found"}</c>
/// (index.js:302-308). All three are mapped properly here rather than reproducing those 404s — the
/// decision is recorded in <c>docs/MODULES-visits-appointments-prescriptions.md</c> (finding 3) and
/// deliberately overrides <c>docs/port-contracts/audit-3.json</c>'s <c>routeOrderHazards</c>,
/// which argues for byte-faithfulness. The client already handles the correct shapes
/// (<c>PatientTimeline.jsx:26</c> does <c>r.data || []</c> over a bare array), so un-shadowing
/// them cannot break it.</para>
///
/// <para>The five READ use cases resolve the caller through their own injected
/// <see cref="ICurrentUser"/>, so their queries take no user id. Every write, AI, investigation
/// and specialty-template request takes it explicitly.</para>
/// </summary>
[Route("api/visits")]
[Authorize]
public sealed class VisitsController(IMediator mediator, ICurrentUser currentUser)
    : BaseApiController(mediator)
{
    /// <summary>
    /// multer's <c>limits.fileSize</c> on the transcribe route (visits.js:952). Exceeding it is a
    /// 500, not a 413 — see <see cref="Transcribe"/>.
    /// </summary>
    private const long MaxAudioBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Kestrel's own default <c>Limits.MaxRequestBodySize</c>. Nothing in the solution reconfigures
    /// it, so it is the real outer ceiling on a request body; the transcribe route's
    /// <see cref="RequestFormLimitsAttribute"/> matches it so that multipart READING never rejects
    /// a body Kestrel already accepted, leaving <see cref="MaxAudioBytes"/> as the only audio-size
    /// gate — which is what multer does in Node.
    /// </summary>
    private const long KestrelDefaultMaxRequestBodyBytes = 30_000_000;

    private string UserId => currentUser.UserId ?? throw new UnauthorizedException("Invalid token");

    // ── Specialty exam templates ─────────────────────────────────────────────
    //
    // Neither route has a userType gate or an ownership check, so a patient may read all 11
    // clinical templates. Declared first because both collide with {id}.

    /// <summary>
    /// The exam template matched to the caller's own <c>PhysicianProfile.specialty</c>, plus the
    /// picker's summary list. A caller with no physician profile gets the two-key degenerate body
    /// with an EMPTY template list, never a 403.
    /// </summary>
    [HttpGet("specialty-template")]
    [ProducesResponseType<SpecialtyTemplatePickerResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SpecialtyTemplate()
        => Payload(await Send(new SpecialtyTemplateForCallerQuery(UserId)));

    /// <summary>
    /// One template by its literal key. The segment is used raw — neither lowercased nor
    /// alias-resolved — so "Ophthalmology" and "eye" are both 404s.
    /// </summary>
    [HttpGet("specialty-template/{key}")]
    [ProducesResponseType<SpecialtyTemplateByKeyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SpecialtyTemplateByKey(string key)
        => Payload(await Send(new SpecialtyTemplateByKeyQuery(key)));

    // ── Literal read routes that must precede {id} ───────────────────────────

    /// <summary>
    /// The caller's own completed visits, newest first, as a BARE ARRAY —
    /// <c>ConnectionsPage.jsx:214</c> iterates the body directly, so this must not be wrapped in
    /// the <c>{ data, pagination }</c> envelope <c>GET /api/visits</c> uses.
    /// </summary>
    [HttpGet("inbox")]
    [ProducesResponseType<IReadOnlyList<VisitReadInboxItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Inbox([FromQuery] string? page, [FromQuery] string? limit)
        => Payload(await Send(new VisitReadInboxQuery(page, limit)));

    /// <summary>
    /// The physician dashboard's follow-up queue: up to twenty COMPLETED visits due now or within
    /// seven days, oldest first. A caller with no physician profile gets an empty array.
    ///
    /// <para>Two Node behaviours are deliberately NOT reproduced: the FOLLOW_UP_REMINDER
    /// notification burst a plain GET fires, and <c>cacheMiddleware(30)</c>.</para>
    /// </summary>
    [HttpGet("follow-ups-due")]
    [ProducesResponseType<IReadOnlyList<VisitReadFollowUpDueItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> FollowUpsDue()
        => Payload(await Send(new VisitReadFollowUpsDueQuery()));

    /// <summary>
    /// One patient's visit history, newest first and unpaginated, as a BARE ARRAY. A physician
    /// gets their OWN visits with that patient; the patient themselves gets their completed
    /// record and anyone else gets 403.
    /// </summary>
    [HttpGet("patient/{patientUserId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PatientHistory(
        string patientUserId, [FromQuery] string? subprofileId)
        => Payload(await Send(new VisitReadPatientHistoryQuery(patientUserId, subprofileId)));

    // ── The visit list and creation ──────────────────────────────────────────

    /// <summary>
    /// The dual-persona visit list, paged. Which branch runs is decided by the <c>userType</c>
    /// CLAIM: a physician gets their clinic's visits with the four filters applied, and everyone
    /// else — patients, receptionists and staff alike — gets the visits filed against their OWN
    /// user id.
    ///
    /// <para><c>page</c> and <c>limit</c> bind as STRINGS, never <c>int?</c>. The port reproduces
    /// JavaScript's <c>||</c> coercion — <c>limit=0</c> yields 20 and <c>limit=50abc</c> yields 50
    /// — and int binding would answer 400 for inputs Node accepts.</para>
    ///
    /// <para><c>status</c> is applied as equality when present. An unrecognised value is a 500
    /// with <c>{"error":"Failed to list visits"}</c>, because Node hands it straight to the Prisma
    /// enum filter. <c>clinicId</c>, <c>patientUserId</c> and <c>subprofileId</c> are the other
    /// three physician-branch filters, each ignored on the non-physician branch.</para>
    ///
    /// <para>Documented in the summary rather than with <c>&lt;param&gt;</c> tags: this file and
    /// its sibling controllers document parameters in prose, and a partial set of tags would emit
    /// CS1573 for every parameter left undocumented.</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType<VisitReadListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? clinicId,
        [FromQuery] string? status,
        [FromQuery] string? patientUserId,
        [FromQuery] string? subprofileId,
        [FromQuery] string? page,
        [FromQuery] string? limit)
        => Payload(await Send(new VisitReadListQuery(
            clinicId, status, patientUserId, subprofileId, page, limit)));

    /// <summary>
    /// Creates an IN_PROGRESS visit for either an app-user patient or a physician-owned local
    /// chart. A missing physician profile is a 400 here — the only gate in the file that answers
    /// that with a bad-request status rather than a 403.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<VisitWriteCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] VisitWriteCreateVisitBody? request)
        => CreatedPayload(await Send(new VisitWriteCreateVisitCommand(UserId, request)));

    // ── One visit ────────────────────────────────────────────────────────────

    /// <summary>
    /// One visit in full. Readable by the owning physician and by the patient, and by nobody else
    /// — clinic staff get 403 even for a clinic they work at. ARCHIVED and soft-deleted visits are
    /// still readable by id, which is how a patient keeps access to a record the physician
    /// archived.
    ///
    /// <para>The response has two incompatible key sets (the physician view carries the raw
    /// audio/transcript/notes columns, the patient view hides the unshared SOAP sections), so the
    /// handler's result type is <c>object</c> and no generic is claimed here.</para>
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id)
        => Payload(await Send(new VisitReadDetailQuery(id)));

    /// <summary>
    /// Updates a visit, owner only. A COMPLETED visit accepts follow-up edits ONLY — anything else
    /// in the body is a 400 — while ARCHIVED and CANCELLED visits stay fully editable.
    ///
    /// <para>The body binds to the Application's own record rather than a controller-local copy,
    /// because every member is a <see cref="JsonElement"/>: an absent key and a present
    /// <c>null</c> mean different things (<c>{"followUpDate": null}</c> CLEARS the column, an
    /// empty body does not), and re-declaring twelve such members here would only invite drift.
    /// </para>
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType<VisitWriteVisitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] VisitWriteUpdateVisitBody? request)
        => Payload(await Send(new VisitWriteUpdateVisitCommand(id, UserId, request)));

    /// <summary>
    /// Signs the visit off, stores the section whitelist the patient may read, and notifies the
    /// patient plus every active staff member at the clinic. There is no idempotency guard:
    /// re-completing succeeds, overwrites the whitelist and re-fires every notification.
    ///
    /// <para><c>sharedSections</c> stays a <see cref="JsonElement"/> because visits.js gates on
    /// <c>Array.isArray</c> alone — a string, an object or a number is not rejected, it silently
    /// means "share everything", and so does an empty array.</para>
    /// </summary>
    [HttpPut("{id}/complete")]
    [ProducesResponseType<VisitWriteCompletedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] VisitWriteCompleteVisitBody? request)
        => Payload(await Send(new VisitWriteCompleteVisitCommand(id, UserId, request)));

    /// <summary>
    /// The patient attaches a question to a COMPLETED visit and the physician is notified. Four
    /// gates, and their order is observable: empty text is a 400 BEFORE the visit is fetched, so
    /// an empty body against a nonexistent id answers 400 rather than 404.
    /// </summary>
    [HttpPut("{id}/patient-feedback")]
    [ProducesResponseType<VisitWritePatientFeedbackResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatientFeedback(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] VisitWritePatientFeedbackBody? request)
        => Payload(await Send(new VisitWriteSavePatientFeedbackCommand(id, UserId, request)));

    /// <summary>
    /// The patient's own note on a visit — the same two columns as
    /// <c>PUT /{id}/patient-feedback</c>, but stored untrimmed, with NO status gate and no
    /// notification. A falsy note clears both columns without confirmation.
    /// </summary>
    [HttpPatch("{id}/patient-note")]
    [ProducesResponseType<VisitWritePatientNoteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatientNote(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] VisitWriteSavePatientNoteBody? request)
        => Payload(await Send(new VisitWriteSavePatientNoteCommand(id, UserId, request)));

    /// <summary>
    /// Despite the verb, nothing is deleted: the status moves to ARCHIVED, <c>deleted_at</c> is
    /// untouched and the patient keeps access.
    ///
    /// <para>The guards run 403-BEFORE-404 here, inverted relative to every other route in the
    /// file: the physician profile is resolved first, so a patient calling this on an id that does
    /// not exist gets 403 where the rest of the file gives 404.</para>
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Archive(string id)
        => Payload(await Send(new VisitWriteArchiveVisitCommand(id, UserId)));

    /// <summary>
    /// The patient hides a visit from their own inbox. The physician's record is untouched,
    /// nothing is deleted, and no endpoint anywhere undoes it.
    /// </summary>
    [HttpDelete("{id}/patient-dismiss")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatientDismiss(string id)
        => Payload(await Send(new VisitWriteDismissVisitCommand(id, UserId)));

    // ── AI ───────────────────────────────────────────────────────────────────
    //
    // All three sit behind aiUsageCheck in Node, which is NOT ported: its 401
    // "Authentication required" / "User not found", its 403 "Feature not available on your plan"
    // and its 429 quota rejection do not exist here yet (docs/PORT-STATUS.md).

    /// <summary>
    /// Turns the visit's raw notes into a SOAP note, owner only. There is no status guard, so a
    /// COMPLETED visit already shared with the patient can be regenerated over.
    ///
    /// <para>An AI failure is NOT an error: it degrades to a 200 carrying a template-built note
    /// with a narrower key set. The two 200 bodies are separate records under one abstract result
    /// type, which is why the declared response type is the base.</para>
    /// </summary>
    [HttpPost("{id}/generate-soap")]
    [ProducesResponseType<VisitAiSoapResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateSoap(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] GenerateSoapRequest? request)
        => Payload(await Send(new VisitAiGenerateSoapCommand(
            id, UserId, request?.Enhance, request?.SpecialtyKey)));

    /// <summary>
    /// Suggests ICD-10-CM codes, owner only. Reads no body at all, and writes nothing: the codes
    /// the physician accepts are persisted later by <c>PUT /api/visits/{id}</c>.
    ///
    /// <para>Unlike generate-soap there is no graceful fallback — a gateway failure is a 500 —
    /// but a successful call whose text will not parse is a 200 with an empty list.</para>
    /// </summary>
    [HttpPost("{id}/suggest-codes")]
    [ProducesResponseType<VisitAiSuggestCodesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuggestCodes(string id)
        => Payload(await Send(new VisitAiSuggestCodesCommand(id, UserId)));

    /// <summary>
    /// Transcribes an audio recording and APPENDS it to the visit's running transcript.
    ///
    /// <para>THE ONLY multipart endpoint in the solution so far, so it sets the convention: bind
    /// the file with <c>[FromForm(Name = "...")] IFormFile?</c> under the EXACT field name the
    /// client sends, keep the part optional so the handler owns the missing-file response, and
    /// copy the bytes through a <see cref="MemoryStream"/> sized from
    /// <see cref="IFormFile.Length"/>. <c>VoiceRecorder.jsx:107-109</c> posts two parts,
    /// <c>audio</c> (the blob) and <c>language</c>.</para>
    ///
    /// <para>MISSING PART: Node's <c>if (!req.file)</c> answers
    /// <c>400 {"error":"No audio file provided"}</c> (visits.js:968), and
    /// <c>VisitAiTranscribeHandler</c> reproduces that from its own
    /// <c>Audio is null or { Length: 0 }</c> guard. So an absent part is forwarded as an EMPTY
    /// array on purpose — the handler, not this action, renders the 400 — and the controller must
    /// not short-circuit it.</para>
    ///
    /// <para>The three multer rejections below are reproduced here because multer runs before the
    /// Node handler and its errors reach the global error handler, which uses
    /// <c>err.status || 500</c>. MulterError carries no status, so all three are 500s with
    /// multer's own text — NOT 413, NOT 415, NOT 400.</para>
    ///
    /// <para>THE FORM LIMIT IS SET PER-ACTION and must stay that way. <c>Program.cs</c> configures
    /// a global <c>FormOptions.MultipartBodyLengthLimit</c> from <c>FileStorage:MaxFileSizeBytes</c>
    /// — 10 MB, chosen for the Documents module's own uploads. Inherited here it would refuse
    /// every 10-to-25 MB recording inside <c>ReadFormAsync</c>, before this action's body runs, so
    /// uploads Node transcribes with a 200 would answer 400 <c>{"error":"Validation failed"}</c>
    /// and the <see cref="MaxAudioBytes"/> gate below would be dead code. The override is pinned
    /// to Kestrel's own default <c>MaxRequestBodySize</c> (30,000,000 bytes) so form reading is
    /// never the binding constraint: everything Kestrel admits reaches the action, and the 25 MB
    /// ceiling is enforced where Node enforces it — as multer's
    /// <c>500 {"error":"File too large"}</c>. Two divergences remain and are accepted: a body above
    /// 30,000,000 bytes is refused by Kestrel with a 413 before this action runs (Node would 500),
    /// and a non-multipart content type is refused as a 415 by <c>[Consumes]</c> where Node would
    /// reach the handler's 400.</para>
    /// </summary>
    [HttpPost("{id}/transcribe")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = KestrelDefaultMaxRequestBodyBytes)]
    [ProducesResponseType<VisitAiTranscribeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Transcribe(
        string id,
        [FromForm(Name = "audio")] IFormFile? audio,
        [FromForm(Name = "language")] string? language)
    {
        // multer's .single('audio') accepts exactly ONE file part, named `audio`. Any other file
        // part — a different name, or a second one — raises LIMIT_UNEXPECTED_FILE and surfaces as
        // 500 {"error":"Unexpected field"}. A request carrying no file part at all is a different
        // case and belongs to the handler's 400, so it falls through.
        var fileParts = Request.HasFormContentType ? Request.Form.Files.Count : 0;

        if (fileParts > 1 || (fileParts == 1 && audio is null))
            throw new BusinessException("Unexpected field", "Unexpected field", 500);

        var bytes = Array.Empty<byte>();

        if (audio is not null)
        {
            // fileFilter runs on the part header, before the size limit can trip, so the mimetype
            // is checked first. `startsWith('audio/')` in JS is case-sensitive and subsumes
            // multer's explicit allow-list (every entry on it starts with that prefix), so
            // StringComparison.Ordinal is the faithful comparison.
            if (audio.ContentType is null
                || !audio.ContentType.StartsWith("audio/", StringComparison.Ordinal))
            {
                throw new BusinessException(
                    "Only audio files are allowed", "Only audio files are allowed", 500);
            }

            if (audio.Length > MaxAudioBytes)
                throw new BusinessException("File too large", "File too large", 500);

            using var buffer = new MemoryStream((int)audio.Length);
            await audio.CopyToAsync(buffer, HttpContext.RequestAborted);
            bytes = buffer.ToArray();
        }

        return Payload(await Send(new VisitAiTranscribeCommand(
            id, UserId, bytes, audio?.FileName, language)));
    }

    // ── Investigations, scoped to a visit ────────────────────────────────────
    //
    // None of the three has a userType gate, a visit.status gate, an audit row, a notification or
    // a transaction: investigations stay writable on a COMPLETED or ARCHIVED visit. The parameter
    // is `invId`, matching the Node route — the third segment is validated against the visit.

    /// <summary>
    /// Creates an investigation attached to a visit. <b>201</b>, unlike its 200-answering
    /// siblings. A missing <c>type</c> or <c>name</c> is a 400 raised BEFORE the visit lookup, so
    /// an empty body against a nonexistent visit answers 400 rather than 404; an omitted
    /// <c>subprofileId</c> inherits the parent visit's.
    /// </summary>
    [HttpPost("{id}/investigations")]
    [ProducesResponseType<InvestigationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateInvestigation(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateInvestigationRequest? request)
        => CreatedPayload(await Send(new InvestigationCreateCommand(
            id, UserId, request?.Type, request?.Name, request?.Instructions, request?.SubprofileId)));

    /// <summary>
    /// Updates an investigation's status and/or result notes — the only two writable fields; type,
    /// name, instructions and resultUrl in the body are ignored. A bad status is a 500, not a 400,
    /// and an <c>invId</c> belonging to another visit is a 404 with its own message.
    /// </summary>
    [HttpPut("{id}/investigations/{invId}")]
    [ProducesResponseType<InvestigationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateInvestigation(
        string id,
        string invId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpdateInvestigationRequest? request)
        => Payload(await Send(new InvestigationUpdateCommand(
            id, invId, UserId, request?.Status, request?.ResultNotes)));

    /// <summary>
    /// Hard-deletes an investigation scoped to its visit. Answers <c>{ success: true }</c> whether
    /// or not anything was removed — an unknown id, an already-deleted id and an id belonging to
    /// another visit all get the same 200, which is where DELETE parts company with its sibling
    /// PUT. The client retries deletes idempotently, so do not turn this into a 404.
    /// </summary>
    [HttpDelete("{id}/investigations/{invId}")]
    [ProducesResponseType<SuccessResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteInvestigation(string id, string invId)
        => Payload(await Send(new InvestigationDeleteCommand(id, invId, UserId)));
}

// ─────────────────────────────────────────────────────────────────────────────
// Request bodies.
//
// Only the routes whose command takes FLAT parameters get a record here. Create, update,
// complete, patient-feedback and patient-note bind straight to the body records the Visits
// Application layer already publishes and its commands already consume — copying those across
// would duplicate twelve JsonElement members with no gain and a real risk of drift.
//
// Every body parameter carries [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] so an
// absent or empty body binds as null instead of a Validation-failed 400: express.json() hands the
// Node handlers `{}` for a body-less request, and each command's `Body?` is written for exactly
// that. The nullable ANNOTATION alone does not do this on the MVC path — unlike minimal APIs,
// BodyModelBinderProvider consults EmptyBodyBehavior and MvcOptions.AllowEmptyInputInBodyModelBinding
// (neither of which Program.cs sets), never NRT — so without the explicit behaviour a
// Content-Length: 0 request answered 400 {"error":"Validation failed"} where Node reaches the
// handler's own message. One divergence is left standing: a body-less request that also omits
// Content-Type is a 415 from UnsupportedContentTypeFilter, because [ApiController] infers a
// consumes constraint from the body parameter.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The two optional fields <c>POST /{id}/generate-soap</c> reads.</summary>
/// <param name="Enhance">
/// Raw <see cref="JsonElement"/>, deliberately not a <c>bool</c>. Node applies <c>!!enhance</c>,
/// so <c>"yes"</c> and <c>1</c> both mean true; binding it as a bool would answer 400 for bodies
/// the Node route accepts, and flattening it would lose the absent/null distinction the handler
/// reads.
/// </param>
/// <param name="SpecialtyKey">
/// The client's template choice — <c>VisitDetailPage.jsx:216</c> sends it, possibly null.
/// Honoured only when it names a real specialty template.
/// </param>
public sealed record GenerateSoapRequest(JsonElement? Enhance, string? SpecialtyKey);

/// <summary>The investigation-creation body. <c>instructions</c> collapses "" to null.</summary>
/// <param name="Type">Required, free text. NOT an enum: the column has no whitelist.</param>
/// <param name="Name">Required, free text, stored untrimmed.</param>
/// <param name="Instructions">Optional; an empty string is stored as null.</param>
/// <param name="SubprofileId">Optional; when absent the row inherits the parent visit's.</param>
public sealed record CreateInvestigationRequest(
    string? Type, string? Name, string? Instructions, string? SubprofileId);

/// <summary>
/// The investigation-update body. These are the only two writable fields — <c>type</c>,
/// <c>name</c>, <c>instructions</c> and <c>resultUrl</c> are ignored if sent.
/// </summary>
/// <param name="Status">
/// Optional, read with a truthy check: null and "" are silently ignored and the row keeps its
/// status. An unrecognised non-empty value is a 500.
/// </param>
/// <param name="ResultNotes">
/// Optional. A present <c>null</c> currently reads as "not supplied" where Node clears the
/// column — the one gap left in this route, and it needs the command to distinguish an absent key
/// from a null one (see the note in <c>InvestigationUpdateHandler</c>), not a change here.
/// </param>
public sealed record UpdateInvestigationRequest(string? Status, string? ResultNotes);
