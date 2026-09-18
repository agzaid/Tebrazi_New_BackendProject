using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Tebrazi.Common.Api.Controllers;
using Tebrazi.Prescriptions.Application.ApiModels.Responses;
using Tebrazi.Prescriptions.Application.UseCases;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Api.Controllers;

/// <summary>
/// <c>/api/prescriptions</c> — the port of <c>server/src/routes/prescriptions.js</c>, all
/// seventeen router registrations.
///
/// <para>Route ORDER: the five single-segment literals (<c>summary</c>,
/// <c>interaction-history</c>, <c>check-interactions</c>, <c>extract-from-plan</c> and
/// <c>transcribe-rx</c>) are declared before the <c>{id}</c> family. ASP.NET prefers a literal
/// over a parameter regardless of declaration order, so the ordering here is legibility — but the
/// CONSTRAINTS behind it are real. <c>docs/port-contracts/audit-1.json</c>'s
/// <c>routeOrderHazards</c> records that <c>GET /summary</c> and <c>GET /interaction-history</c>
/// are genuine Express-order dependencies in the source (which carries a literal
/// "(must be before /:id)" comment at prescriptions.js:162): bound as <c>{id}</c> they would
/// answer <c>404 {"error":"Prescription not found"}</c>.</para>
///
/// <para>NO <c>POST /{id}</c>, EVER. The file has no parameterised POST at all, and adding one —
/// or an <c>[HttpPost("{id}")]</c> catch-all — would swallow <c>check-interactions</c>,
/// <c>extract-from-plan</c> and <c>transcribe-rx</c>, none of which has a parameterised sibling to
/// lose to. For the same reason there is NO <c>{id}/{action}</c> catch-all: eight second-segment
/// literals live under <c>{id}</c> (<c>pdf</c>, <c>sign</c>, <c>send</c>, <c>dispense</c>,
/// <c>refill-request</c>, <c>refill-respond</c>, <c>stop-medication</c>,
/// <c>resume-medication</c>), and only their differing literals keep them apart. Do NOT constrain
/// <c>{id}</c> with <c>:guid</c> either: <c>Prescription.id</c> is unconstrained text and a
/// constraint would turn the handlers' <c>404 {"error":"Prescription not found"}</c> into a
/// routing 404 with a different body.</para>
///
/// <para>Three registrations are NOT called by the React client — <c>GET /interaction-history</c>,
/// <c>GET /{id}</c> and <c>PUT /{id}</c>. They were ported deliberately (audit-1,
/// <c>missedRoutes</c>) for the quirks they carry: <c>GET /{id}</c>'s <c>isPhysician</c>
/// serializes as <c>null</c> rather than <c>false</c>, and <c>PUT /{id}</c> holds the router's
/// only status precondition. They are mapped here rather than left unreachable.</para>
///
/// <para>The five READ use cases resolve the caller through their own injected
/// <see cref="ICurrentUser"/>, so their queries take no user id. Every write and AI request takes
/// it explicitly, and <c>check-interactions</c> additionally takes the RAW <c>userType</c> claim
/// because physician-vs-not changes its <c>drugsChecked</c> for an identical body.</para>
///
/// <para>NO route in this file reads <c>X-Clinic-Id</c>, so no clinic context is injected. Node's
/// <c>cacheMiddleware(15)</c> on <c>GET /</c> and <c>cacheMiddleware(30)</c> on <c>GET /summary</c>
/// are not reproduced — see the handlers.</para>
/// </summary>
[Route("api/prescriptions")]
[Authorize]
public sealed class PrescriptionsController(IMediator mediator, ICurrentUser currentUser)
    : BaseApiController(mediator)
{
    /// <summary>
    /// multer's <c>limits.fileSize</c> on <c>transcribe-rx</c> (prescriptions.js:964). Exceeding
    /// it is a 500, not a 413 — see <see cref="TranscribeRx"/>.
    /// </summary>
    private const long MaxAudioBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Kestrel's own default <c>Limits.MaxRequestBodySize</c>, mirrored from
    /// <see cref="VisitsController"/>. The per-action form limit is pinned to it so multipart
    /// READING never rejects a body Kestrel already accepted, leaving <see cref="MaxAudioBytes"/>
    /// as the only audio-size gate — which is what multer does in Node.
    /// </summary>
    private const long KestrelDefaultMaxRequestBodyBytes = 30_000_000;

    private string UserId => currentUser.UserId ?? throw new UnauthorizedException("Invalid token");

    // ── Single-segment literals, declared before the {id} family ─────────────

    /// <summary>
    /// The dashboard badge: <c>{ signed, sent, total }</c>, three integers and nothing else.
    ///
    /// <para>MUST precede <c>GET /{id}</c> — bound as an id this answers
    /// <c>404 {"error":"Prescription not found"}</c>. There is no <c>userType</c> gate and no 403:
    /// a caller with no physician profile gets literal zeros, which every patient page depends on.
    /// The <c>signed</c> bucket spans CONFIRMED <b>and</b> SIGNED, so <c>signed + sent</c> does not
    /// equal <c>total</c>.</para>
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType<RxReadSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary()
        => Payload(await Send(new RxReadSummaryQuery()));

    /// <summary>
    /// The physician's own <c>InteractionAlert</c> rows, newest <c>checkedAt</c> first, as a BARE
    /// ARRAY. Not called by the client; ported for parity.
    ///
    /// <para>MUST precede <c>GET /{id}</c> — the Node source carries the comment
    /// "(must be before /:id)". A caller with no physician profile gets <c>200 []</c>, never a 403.
    /// </para>
    ///
    /// <para><c>limit</c> binds as a STRING, never <c>int?</c>. Node applies
    /// <c>parseInt(req.query.limit) || 20</c> with no clamp and no cap, so <c>?limit=0</c> and
    /// <c>?limit=abc</c> both mean 20, <c>?limit=1e6</c> means 1, and <c>?limit=-5</c> is a
    /// meaningful reverse-take. An int binding would answer 400 for inputs Node accepts and would
    /// lose the rest.</para>
    /// </summary>
    [HttpGet("interaction-history")]
    [ProducesResponseType<IReadOnlyList<RxReadInteractionHistoryItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> InteractionHistory([FromQuery] string? limit)
        => Payload(await Send(new RxReadInteractionHistoryQuery(limit)));

    /// <summary>
    /// Screens a drug list — the caller's, plus everything the backend can discover for the
    /// patient — for interactions and allergy conflicts.
    ///
    /// <para><b>THIS ROUTE HAS NO VALIDATION AND NO AUTHORIZATION: not one 400 and not one 403.</b>
    /// Do not add any. <c>PrescriptionsPage.jsx:266</c> posts an entirely empty body and other call
    /// sites omit keys, so the body is optional and each member must survive being absent.</para>
    ///
    /// <para>The three body members are non-nullable <see cref="JsonElement"/> so an ABSENT key
    /// arrives as <see cref="JsonValueKind.Undefined"/> (falsy) and an explicit <c>null</c> as
    /// <see cref="JsonValueKind.Null"/> — the handler distinguishes them, and it reads the raw
    /// values besides: <c>medications</c> may legally carry objects rather than strings, which is
    /// what makes <c>drugsChecked</c> a <c>(string|object)[]</c>. A <c>List&lt;string&gt;</c> or
    /// <c>string?</c> binding would break that matrix and answer 400 where Node answers 200 or 500.
    /// </para>
    ///
    /// <para><see cref="ICurrentUser.UserType"/> is forwarded RAW and UNPARSED. It is load-bearing:
    /// with no <c>visitId</c>, auto-discovery runs only for a non-physician, so an identical body
    /// yields a different <c>drugsChecked</c> for a physician than for a patient. The claim can
    /// also hold a value no enum has, which is why it is never parsed.</para>
    /// </summary>
    [HttpPost("check-interactions")]
    [ProducesResponseType<RxAiCheckInteractionsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckInteractions(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxCheckInteractionsRequest? request)
        => Payload(await Send(new RxAiCheckInteractionsCommand(
            UserId,
            currentUser.UserType,
            request?.Medications ?? default,
            request?.SubprofileId ?? default,
            request?.VisitId ?? default)));

    /// <summary>
    /// Pulls structured medications out of a SOAP note's free-text PLAN section. Physician-only;
    /// otherwise always a 200, because every AI failure short of a throw degrades to
    /// <c>{"medications":[]}</c> with no error key.
    ///
    /// <para><c>planText</c> binds as a raw <see cref="JsonElement"/>, never a <c>string</c>. Node's
    /// gate is <c>if (!planText || planText.trim().length &lt; 5)</c>, so a falsy value and a
    /// short string share <c>400 {"error":"Plan text is required"}</c> while a truthy NON-string
    /// reaches <c>.trim()</c> and throws — a <b>500</b>. A <c>string</c> binding would collapse
    /// those two and would let the framework's own
    /// <c>400 {"error":"Validation failed", …}</c> replace the route's message.</para>
    /// </summary>
    [HttpPost("extract-from-plan")]
    [ProducesResponseType<RxAiExtractFromPlanResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ExtractFromPlan(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxExtractFromPlanRequest? request)
        => Payload(await Send(new RxAiExtractFromPlanCommand(
            UserId, request?.PlanText ?? default)));

    /// <summary>
    /// Transcribes a dictated prescription and parses medications out of the transcript. Nothing is
    /// persisted — the client fills the Rx form and posts <c>POST /api/prescriptions</c> separately.
    ///
    /// <para>Multipart, bound exactly as <see cref="VisitsController.Transcribe"/> is:
    /// <c>[FromForm(Name = "audio")] IFormFile?</c> and <c>[FromForm(Name = "language")] string?</c>
    /// are the two parts <c>PrescriptionPanel.jsx</c> sends (the blob is named
    /// <c>rx_dictation.webm</c>, type <c>audio/webm</c>). <c>language</c> is forwarded RAW and NOT
    /// defaulted here — the handler applies Node's <c>|| 'en'</c>.</para>
    ///
    /// <para>MISSING PART: an absent file is forwarded as an EMPTY array on purpose. Node's
    /// <c>if (!req.file)</c> answers <c>400 {"error":"No audio file provided"}</c> and
    /// <c>RxAiTranscribeHandler</c> owns that response, so this action must not short-circuit it.
    /// </para>
    ///
    /// <para>The three multer rejections below are 500s, NOT 4xx. multer runs before the Node
    /// handler and its errors reach the global error handler's <c>err.status || 500</c>; neither a
    /// plain <c>Error</c> nor a <c>MulterError</c> carries a status. <c>rxUpload</c>'s limits equal
    /// the visits uploader's — memoryStorage, 25 MB, and a <c>fileFilter</c> whose ENTIRE test is
    /// <c>file.mimetype.startsWith('audio/')</c>, which is case-sensitive in JS and therefore an
    /// Ordinal comparison here.</para>
    ///
    /// <para>THE FORM LIMIT IS SET PER-ACTION and must stay that way. <c>Program.cs</c> configures a
    /// global <c>FormOptions.MultipartBodyLengthLimit</c> of 10 MB from
    /// <c>FileStorage:MaxFileSizeBytes</c>, chosen for the Documents module. Inherited here it would
    /// refuse every 10-to-25 MB recording inside <c>ReadFormAsync</c>, before this action's body
    /// runs, so uploads Node transcribes with a 200 would answer
    /// <c>400 {"error":"Validation failed"}</c> and the <see cref="MaxAudioBytes"/> gate below would
    /// be dead code.</para>
    ///
    /// <para>The declared 200 type is the polymorphic BASE: the endpoint has two 200 bodies whose
    /// third key differs (<c>transcribeDuration</c> on success, <c>parseError</c> on a parse
    /// failure) and a port must never emit both, because the client branches on
    /// <c>res.data.parseError</c>.</para>
    /// </summary>
    [HttpPost("transcribe-rx")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = KestrelDefaultMaxRequestBodyBytes)]
    [ProducesResponseType<RxAiTranscribeResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> TranscribeRx(
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
            // is checked first. `startsWith('audio/')` in JS is case-sensitive, so Ordinal.
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

        return Payload(await Send(new RxAiTranscribeCommand(
            UserId, bytes, audio?.FileName, language)));
    }

    // ── The prescription list and creation ───────────────────────────────────

    /// <summary>
    /// The dual-persona prescription list as a BARE ARRAY, never an object and never null, ordered
    /// <c>createdAt</c> DESC.
    ///
    /// <para>The branch key is the <c>userType</c> CLAIM, not the presence of a physician profile:
    /// a PHYSICIAN sees every prescription they authored in every status, and everyone else —
    /// PATIENT, RECEPTIONIST and STAFF alike — is scoped to their OWN user id and narrowed to
    /// CONFIRMED/SENT/DISPENSED. The two branches have incompatible key sets (<c>patientName</c>
    /// exists only on the physician one), so the handler's element type is <c>object</c> and no
    /// generic is claimed here.</para>
    ///
    /// <para><c>visitId</c> binds as a raw <c>string?</c> because Node's gate is <c>if (visitId)</c>
    /// — <c>?visitId=</c> is an EMPTY string, which is falsy and therefore not a filter at all.
    /// </para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? visitId)
        => Payload(await Send(new RxReadListQuery(visitId)));

    /// <summary>
    /// Creates a prescription — <b>201</b>, and it AUTO-SIGNS: the row is inserted with status
    /// SIGNED and <c>signedAt</c> already stamped.
    ///
    /// <para>A missing physician profile is <c>400 {"error":"Physician profile required"}</c>, NOT
    /// a 403; a visit that is not the caller's — including one that does not exist — is
    /// <c>403 {"error":"Visit not found or not yours"}</c>, never a 404.</para>
    ///
    /// <para>The body binds to the Application's own record rather than a controller-local copy:
    /// all three members are <see cref="JsonElement"/> so the raw values reach the handler's own
    /// truthiness gate, and re-declaring them here would only invite drift.</para>
    /// </summary>
    [HttpPost]
    [ProducesResponseType<RxLifecycleCreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxLifecycleCreateBody? request)
        => CreatedPayload(await Send(new RxLifecycleCreateCommand(UserId, request)));

    // ── One prescription ─────────────────────────────────────────────────────

    /// <summary>
    /// One prescription in full. Not called by the React client; ported for parity because of the
    /// <c>isPhysician</c> quirk — it is the truthy-AND RESULT in Node, so it serializes as
    /// <c>null</c> (not <c>false</c>) for a caller with no physician profile.
    ///
    /// <para>404 wins over the access check, because the row is read first. Access is the AUTHORING
    /// physician or the visit's patient and nobody else — a colleague at the same clinic gets 403.
    /// There is no <c>deletedAt</c> filter, so a soft-deleted prescription is still returned.</para>
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType<RxReadDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id)
        => Payload(await Send(new RxReadDetailQuery(id)));

    /// <summary>
    /// Updates a prescription's <c>medications</c> and/or <c>notes</c>, owner only. Not called by
    /// the React client; ported for parity because it holds the router's ONLY status precondition:
    /// a SENT prescription is <c>400 {"error":"Cannot edit a prescription that has already been
    /// sent"}</c>, while DISPENSED and CONFIRMED stay fully editable.
    ///
    /// <para>The body binds to the Application's own record. Both members are non-nullable
    /// <see cref="JsonElement"/> and the distinction is the contract: an ABSENT key PRESERVES the
    /// column, an explicit <c>null</c> CLEARS it (notes) or is a 500 (medications). A
    /// <c>JsonElement?</c> would bind both to C# null and lose that. An empty body <c>{}</c> is
    /// still a write — Node's update is unconditional and <c>updatedAt</c> always moves.</para>
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType<RxLifecycleUpdatedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxLifecycleUpdateBody? request)
        => Payload(await Send(new RxLifecycleUpdateCommand(id, UserId, request)));

    /// <summary>
    /// The printable Rx document. <b>THIS RETURNS HTML, NOT A PDF AND NOT JSON</b> — Node sets
    /// <c>res.setHeader('Content-Type', 'text/html')</c> and <c>res.send(html)</c>, and Express
    /// appends the charset, so the header on the wire is <c>text/html; charset=utf-8</c>. There are
    /// no PDF bytes and no <c>Content-Disposition</c>; the client fetches this with
    /// <c>responseType: 'text'</c> and <c>document.write()</c>s it into a new window.
    ///
    /// <para>The content type comes from <see cref="RxReadPdfResponse.ContentType"/> verbatim so the
    /// charset is present — the document carries the <c>🖨</c> and <c>℞</c> glyphs and a U+2014
    /// em-dash fallback, and must be served as UTF-8 unchanged. A bare <c>text/html</c> would not
    /// match Node.</para>
    ///
    /// <para>ERRORS on this route stay JSON. Node's <c>setHeader</c> call only runs on the happy
    /// path, so the 404, the 403 and the 500 come out of the exception middleware as
    /// <c>{"error": "..."}</c> with <c>application/json</c> — which is exactly what returning
    /// <see cref="ControllerBase.Content(string, string)"/> only on success gives.</para>
    ///
    /// <para>Access is the authoring physician or the visit's patient, checked AFTER the row is
    /// fetched, so 404 wins. No <c>deletedAt</c> filter, and nothing is written — <c>pdfUrl</c> is
    /// never stored.</para>
    /// </summary>
    [HttpGet("{id}/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Pdf(string id)
    {
        var response = await Send(new RxReadPdfQuery(id));

        return Content(response.Html, RxReadPdfResponse.ContentType);
    }

    // ── Lifecycle transitions ────────────────────────────────────────────────

    /// <summary>
    /// Signs a prescription, owner only. <b>It writes status CONFIRMED, not SIGNED</b> — creation
    /// already wrote SIGNED — and overwrites <c>signedAt</c>. There is no idempotency guard and no
    /// status precondition, so re-signing succeeds and re-stamps the timestamp.
    ///
    /// <para>Reads no body. A caller with no physician profile and a caller who authored a
    /// different prescription collapse into the same
    /// <c>403 {"error":"Not your prescription"}</c>.</para>
    /// </summary>
    [HttpPut("{id}/sign")]
    [ProducesResponseType<RxLifecycleSignedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Sign(string id)
        => Payload(await Send(new RxLifecycleSignCommand(id, UserId)));

    /// <summary>
    /// Marks a prescription SENT, stamps <c>sentAt</c> and fires the patient notification plus the
    /// medication-schedule reminders. Owner only, reads no body.
    ///
    /// <para>The 500 is reachable ONLY from the row lookup, the profile lookup or the status write:
    /// every notification and reminder failure is caught and logged, never surfaced.</para>
    /// </summary>
    [HttpPut("{id}/send")]
    [ProducesResponseType<RxLifecycleSentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendPrescription(string id)
        => Payload(await Send(new RxLifecycleSendCommand(id, UserId)));

    /// <summary>
    /// Marks a prescription DISPENSED and optionally decrements clinic inventory. Owner only.
    ///
    /// <para>The React client sends NO BODY AT ALL, hence
    /// <see cref="EmptyBodyBehavior.Allow"/>. <c>inventoryItems</c> is a non-nullable
    /// <see cref="JsonElement"/> because the value is never validated: any non-array — an object, a
    /// string, a number, an explicit null, an absent key — is SILENTLY IGNORED and the response
    /// comes back with <c>inventoryDeducted: []</c>, and the elements' own <c>itemId</c> /
    /// <c>quantity</c> coercions are observable (<c>"5 boxes"</c> deducts 5, <c>1.9</c> deducts 1,
    /// a 0/""/null quantity is skipped, an inactive item is skipped). A typed binding would answer
    /// 400 for bodies that must reach the handler.</para>
    ///
    /// <para>The status write happens BEFORE the inventory loop with no wrapping transaction, so a
    /// truthy-but-unparseable quantity yields a 500 on a mutation that has already partially
    /// succeeded. That is the contract, not a defect to repair here.</para>
    /// </summary>
    [HttpPut("{id}/dispense")]
    [ProducesResponseType<RxLifecycleDispensedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Dispense(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxLifecycleDispenseBody? request)
        => Payload(await Send(new RxLifecycleDispenseCommand(id, UserId, request)));

    // ── Refills and per-medication stops ─────────────────────────────────────

    /// <summary>
    /// The PATIENT asks for a refill — and this one is a <b>POST</b>, unlike its three PUT
    /// siblings. Flips <c>refillStatus</c> to PENDING, stamps <c>refillRequestedAt</c>, overwrites
    /// <c>refillNotes</c> and notifies the prescribing physician. The response carries exactly two
    /// keys, not the prescription.
    ///
    /// <para>The prescribing physician gets 403 here: the gate is
    /// <c>visit.patientUserId === req.user.id</c>. Gate order is 404, then 403, then the two 400s
    /// (a status outside SENT/DISPENSED/CONFIRMED, then an already-pending request).</para>
    ///
    /// <para>Both client call sites send <c>{}</c>. <c>notes</c> stays a
    /// <see cref="JsonElement"/>? because <c>notes || null</c> is observable: <c>0</c>,
    /// <c>false</c>, <c>""</c> and <c>null</c> all store null while the STRING <c>"0"</c> is kept,
    /// and a truthy non-string is a 500. Nullable is correct here — <c>x || null</c> cannot tell an
    /// absent key from an explicit one.</para>
    /// </summary>
    [HttpPost("{id}/refill-request")]
    [ProducesResponseType<RxRefillRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RefillRequest(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxRefillRequestBody? request)
        => Payload(await Send(new RxRefillRequestCommand(id, UserId, request)));

    /// <summary>
    /// The prescribing physician approves or denies a PENDING refill, and the patient is notified
    /// WITH an email.
    ///
    /// <para>Gate order differs from every other route in this group: the action check runs FIRST,
    /// before the prescription is even loaded, so a bad <c>action</c> against a nonexistent id
    /// answers <c>400 {"error":"Action must be APPROVED or DENIED"}</c> rather than 404 — and a
    /// body-less request takes that same 400. The comparison is strict equality, so it is
    /// case-sensitive and no non-string value can pass, which is why <c>action</c> is a
    /// <see cref="JsonElement"/>? and not a <c>string</c>.</para>
    /// </summary>
    [HttpPut("{id}/refill-respond")]
    [ProducesResponseType<RxRefillRespondResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RefillRespond(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxRefillRespondBody? request)
        => Payload(await Send(new RxRefillRespondCommand(id, UserId, request)));

    /// <summary>
    /// The PATIENT stops one medication inside the prescription's JSON array: <c>stoppedAt</c> and
    /// <c>stoppedByPatient</c> are added to that element and the rebuilt array comes back.
    ///
    /// <para><c>medicationIndex</c> is a non-nullable <see cref="JsonElement"/> and must stay one.
    /// The presence check is <c>=== undefined || === null</c>, so <c>0</c>, <c>""</c>,
    /// <c>false</c> and <c>NaN</c> all pass it, and the bounds check compares with NaN so a
    /// non-numeric string slips through into a 200 whose medications array is UNCHANGED. An
    /// <c>int</c> binding would answer 400 for every one of those.</para>
    /// </summary>
    [HttpPut("{id}/stop-medication")]
    [ProducesResponseType<RxRefillStopMedicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StopMedication(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxRefillStopMedicationBody? request)
        => Payload(await Send(new RxRefillStopMedicationCommand(id, UserId, request)));

    /// <summary>
    /// The PATIENT un-stops a medication: <c>stoppedAt</c> and <c>stoppedByPatient</c> are REMOVED
    /// from the element — absent keys, never nulls — and the rebuilt array comes back.
    ///
    /// <para>The mirror of stop-medication's presence check simply is not there, and the asymmetry
    /// is DIRECTLY OBSERVABLE: against an empty medications array an ABSENT <c>medicationIndex</c>
    /// slips past both NaN comparisons into a <b>500</b> while an explicit <c>null</c> coerces to 0
    /// and takes the <b>400</b> "Invalid medication index". That is why the body member is a
    /// non-nullable <see cref="JsonElement"/> — a <c>JsonElement?</c> collapses both onto C# null
    /// and turns the 500 into a 400.</para>
    /// </summary>
    [HttpPut("{id}/resume-medication")]
    [ProducesResponseType<RxRefillResumeMedicationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResumeMedication(
        string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RxRefillResumeMedicationBody? request)
        => Payload(await Send(new RxRefillResumeMedicationCommand(id, UserId, request)));
}

// ─────────────────────────────────────────────────────────────────────────────
// Request bodies.
//
// Only the two AI routes whose command takes FLAT parameters get a record here. Create, update,
// dispense, refill-request, refill-respond, stop-medication and resume-medication bind straight to
// the body records the Prescriptions Application layer already publishes and its commands already
// consume — copying those across would duplicate JsonElement members whose Undefined-vs-Null
// distinction IS the contract, with no gain and a real risk of drift.
//
// Every body parameter carries [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] because
// express.json() hands the Node handlers `{}` for a body-less request and each command's `Body?`
// is written for exactly that. The nullable ANNOTATION alone does not do this on the MVC path —
// BodyModelBinderProvider consults EmptyBodyBehavior and MvcOptions.AllowEmptyInputInBodyModelBinding
// (neither of which Program.cs sets), never NRT — so without the explicit behaviour a
// Content-Length: 0 request answers 400 {"error":"Validation failed","message":"A non-empty request
// body is required."} where Node reaches the handler's own response. One divergence is left
// standing, matching VisitsController: a body-less request that also omits Content-Type is a 415
// from UnsupportedContentTypeFilter, because [ApiController] infers a consumes constraint from the
// body parameter.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The three body fields <c>POST /api/prescriptions/check-interactions</c> reads. Every member is a
/// NON-NULLABLE <see cref="JsonElement"/> so an absent key binds as
/// <see cref="JsonValueKind.Undefined"/> — JavaScript-falsy, and distinct from an explicit
/// <c>null</c>, which the handler treats differently.
/// </summary>
/// <param name="Medications">
/// The drug list, RAW. The client sends bare drug-name strings but nothing validates that: an
/// element may be an object, which is echoed verbatim in <c>drugsChecked</c> and can reach
/// <c>drug.toLowerCase()</c> for a patient with allergies and produce a 500. Never bind this as
/// <c>List&lt;string&gt;</c> or <c>string?</c>.
/// </param>
/// <param name="SubprofileId">
/// Used UNVALIDATED and UNAUTHORIZED — only its truthiness and its being a string matter.
/// </param>
/// <param name="VisitId">
/// Same: unvalidated and unauthorized, and it suppresses the non-physician auto-discovery branch.
/// </param>
public sealed record RxCheckInteractionsRequest(
    JsonElement Medications,
    JsonElement SubprofileId,
    JsonElement VisitId);

/// <summary>
/// The one body field <c>POST /api/prescriptions/extract-from-plan</c> reads.
/// </summary>
/// <param name="PlanText">
/// The PLAN section's free text, RAW and untyped. A falsy value or a string whose TRIMMED length is
/// under 5 is <c>400 {"error":"Plan text is required"}</c>, while a truthy NON-string reaches
/// <c>.trim()</c> in Node and throws — a 500. A <c>string</c> binding would collapse those two
/// outcomes and would surrender the 400's message to the framework's validation response.
/// </param>
public sealed record RxExtractFromPlanRequest(JsonElement PlanText);
