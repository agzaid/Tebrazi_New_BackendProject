using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tebrazi.Prescriptions.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Bodies for the three AI endpoints on the prescriptions router:
//
//      POST /api/prescriptions/check-interactions  (prescriptions.js:735-868)
//      POST /api/prescriptions/extract-from-plan   (prescriptions.js:1078-1143)
//      POST /api/prescriptions/transcribe-rx       (prescriptions.js:972-1069)
//
//  All three are 200-only on success and every error body is a bare `{ "error": "..." }`
//  raised by the handlers through RxErrors — none of these records ever carries an error key.
//
//  The five-key medication element is NOT declared here: `RxMedicationLine`
//  (ApiModels/Responses/CommonPrescriptionResponses.cs) already exists and is shared by
//  extract-from-plan and transcribe-rx precisely because their two arrays are byte-identical.
// ═════════════════════════════════════════════════════════════════════════════

// ── POST /api/prescriptions/check-interactions ────────────────────────────────

/// <summary>
/// <c>POST /api/prescriptions/check-interactions</c>. Exactly three keys, in this order, on BOTH
/// of the endpoint's 200 paths — the fewer-than-two-drugs early return (prescriptions.js:804-806)
/// and the normal checker path (prescriptions.js:854-858).
///
/// <para>One record covers both, deliberately: the two paths differ only in the VALUES of
/// <paramref name="Interactions"/> and <paramref name="Message"/>, never in the key set, so the
/// polymorphic pair that <c>transcribe-rx</c> needs would buy nothing here.</para>
/// </summary>
/// <param name="Interactions">
/// <b>Loose JSON, never a typed record.</b> On the happy path this is the array
/// <c>aiGateway.checkDrugs</c> produced — deterministic allergy warnings PREPENDED to the RAW
/// <c>JSON.parse</c> of the model's text, with no per-element normalization at all
/// (aiGateway.js:685-687, :734). Elements may omit keys, carry extra keys, or hold non-string
/// values; the client reads <c>drug1 / drug2 / severity / description / recommendation</c>
/// (DrugScreeningsPage.jsx:91-101). Always an array, never null: the early return sends an empty
/// one.
/// </param>
/// <param name="DrugsChecked">
/// The handler's OWN deduped <c>allDrugs</c>, not the checker's echo of it, in insertion order:
/// caller-supplied medications first, then the subprofile's reported medications, then
/// cross-prescription medications, then the family sweep.
///
/// <para><b>This is <c>(string|object)[]</c>, not <c>string[]</c>.</b> Node maps a stored
/// medication with <c>m.drugName || m</c> (prescriptions.js:780), so an element that is an object
/// WITHOUT a <c>drugName</c> flows through as the whole object and is echoed verbatim — which is
/// why the array is modelled as raw JSON. Possibly empty, never null.</para>
/// </param>
/// <param name="Message">
/// Four possible literals, and all four are exact. The early return sends
/// <c>"No active medications found"</c> for zero drugs and
/// <c>"Only 1 active medication found — no interactions to check"</c> for one — that is an EM DASH
/// (U+2014) with a single space on each side, and the host serializes with
/// <c>UnsafeRelaxedJsonEscaping</c>, so a hyphen here is a byte-level divergence. The checker path
/// sends <c>$"{n} interaction(s) found"</c> — literal parenthesised <c>(s)</c>, never pluralized —
/// or <c>"No known interactions detected"</c>, which is the COMMON case (aiGateway.js:732-736).
/// </param>
public sealed record RxAiCheckInteractionsResponse(
    JsonArray Interactions,
    JsonArray DrugsChecked,
    string Message);

// ── POST /api/prescriptions/extract-from-plan ─────────────────────────────────

/// <summary>
/// <c>POST /api/prescriptions/extract-from-plan</c> (prescriptions.js:1138). Exactly ONE key.
///
/// <para>There is no <c>parseError</c> key on this endpoint and no <c>count</c> or
/// <c>message</c> — unlike <c>transcribe-rx</c>, every AI failure short of a throw is INVISIBLE
/// here and reaches the client as an empty array with a 200.</para>
/// </summary>
/// <param name="Medications">
/// Always an array, never null. It is <c>[]</c> when the model's text contained no
/// <c>[...]</c> block, when the extracted block did not parse, when it parsed to a non-array, and
/// when every element was dropped for lacking a truthy <c>drugName</c> — the client cannot tell
/// those four apart, and that is the Node behaviour (PrescriptionPanel.jsx:277 renders
/// "No medications found in the Plan text" for all of them).
/// </param>
public sealed record RxAiExtractFromPlanResponse(IReadOnlyList<RxMedicationLine> Medications);

// ── POST /api/prescriptions/transcribe-rx ─────────────────────────────────────

/// <summary>
/// What <c>POST /api/prescriptions/transcribe-rx</c> answers with. The endpoint has TWO 200
/// bodies and they are not variants of one shape: both carry three keys, but the third is
/// <c>transcribeDuration</c> on success (prescriptions.js:1063) and <c>parseError</c> on a
/// medication-parse failure (prescriptions.js:1050). <b>A port must never emit both</b> — the
/// client branches on <c>res.data.parseError</c> (PrescriptionPanel.jsx:70-90), so a null
/// <c>parseError</c> alongside a duration would take the wrong branch.
///
/// <para>Same construction as <c>VisitAiSoapResult</c> in Visits: the polymorphism attributes
/// carry no <c>$type</c> discriminator and exist only so the concrete record's properties are
/// written when the value is handed to MVC as this base type.</para>
/// </summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(RxAiTranscribeResponse))]
[JsonDerivedType(typeof(RxAiTranscribeParseErrorResponse))]
public abstract record RxAiTranscribeResult;

/// <summary>
/// The happy path: transcript, parsed medications, and the transcription's own duration.
/// </summary>
/// <param name="Transcript">Whisper's text for THIS recording. Nothing is persisted — the client fills the Rx form and posts <c>POST /api/prescriptions</c> separately.</param>
/// <param name="Medications">
/// Possibly empty, never null. It is <c>[]</c> when the parse step returned a non-array (which
/// falls through to THIS shape, duration included — not to the <c>parseError</c> shape) and when
/// every element lacked a truthy <c>drugName</c>.
/// </param>
/// <param name="TranscribeDuration">
/// Whisper's float SECONDS, 0 when the provider omitted it. Note the key is
/// <c>transcribeDuration</c>, not <c>duration</c> — it is the only place the duration surfaces on
/// this route, and the client ignores it.
/// </param>
public sealed record RxAiTranscribeResponse(
    string Transcript,
    IReadOnlyList<RxMedicationLine> Medications,
    double TranscribeDuration) : RxAiTranscribeResult;

/// <summary>
/// The medication-parse failure path — still HTTP <b>200</b>, and reached only when the parse
/// chat call SUCCEEDED and its text could not be read as JSON. A throw from either model call is
/// a 500 instead, and discards a transcript that was already paid for.
/// <c>transcribeDuration</c> is ABSENT here, not null.
/// </summary>
/// <param name="Transcript">Returned even though the medications could not be extracted, so the physician can transcribe by hand.</param>
/// <param name="Medications">Always empty on this path — Node writes the literal <c>medications: []</c>.</param>
/// <param name="ParseError">
/// Always the literal
/// <c>"Could not parse medications from transcript. Please add them manually."</c>
/// </param>
public sealed record RxAiTranscribeParseErrorResponse(
    string Transcript,
    IReadOnlyList<RxMedicationLine> Medications,
    string ParseError) : RxAiTranscribeResult;
