using System.Text.Json.Nodes;

namespace Tebrazi.Prescriptions.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response records for the refill / medication-control group, ported from
//  server/src/routes/prescriptions.js:
//
//      POST /api/prescriptions/{id}/refill-request    (L526-L577)
//      PUT  /api/prescriptions/{id}/refill-respond    (L585-L637)
//      PUT  /api/prescriptions/{id}/stop-medication   (L646-L686)
//      PUT  /api/prescriptions/{id}/resume-medication (L694-L729)
//
//  NOT ONE OF THE FOUR RETURNS THE PRESCRIPTION ROW. The two refill routes answer a two-key
//  projection of the columns they just wrote; the two medication routes answer a message plus the
//  in-memory medications array. So none of them shares a key set with `GET /`, `POST /`, `/sign`,
//  `/send`, `/dispense` or `PUT /{id}`, and a shared prescription DTO would be wrong for all four.
//
//  Record declaration order IS the JSON key order, and the host's naming policy is camelCase
//  (Program.cs:46), so `RefillStatus` ships as `refillStatus`.
//
//  DateTime serialization note, true of the whole port and not of this group: Node emits
//  `new Date()` as ISO-8601 with exactly THREE fractional digits, while System.Text.Json writes a
//  round-trip DateTime with up to seven. That affects `refillRequestedAt` below and every other
//  DateTime in the port equally; it is a host-level concern, deliberately not worked around here.
//  `stoppedAt` inside `medications` is NOT affected — it is a hand-formatted STRING (see below).
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// <c>POST /api/prescriptions/{id}/refill-request</c> → 200 (prescriptions.js:571).
///
/// <para>Exactly two keys. Node answers
/// <c>{ refillStatus: updated.refillStatus, refillRequestedAt: updated.refillRequestedAt }</c> —
/// no <c>id</c>, no <c>medications</c>, no <c>message</c>, and none of the other fourteen
/// prescription columns. Both values come from the row the update just wrote, so in practice
/// <c>refillStatus</c> is always the literal <c>"PENDING"</c> and <c>refillRequestedAt</c> is
/// always the freshly stamped instant; they are still modelled as nullable because the column
/// types are, and because the values are echoed from the entity rather than re-asserted.</para>
///
/// <para>What is deliberately ABSENT: <c>refillNotes</c>. The route writes that column too — and
/// wipes it to null when no notes were supplied — but never reports it, so a patient who
/// re-requests after a decision cannot see from this body that the physician's note is gone.</para>
/// </summary>
/// <param name="RefillStatus">
/// The value just written: the literal string <c>"PENDING"</c>. A plain <c>String</c> column in
/// Prisma, not an enum — never model it as a non-nullable enum on the wire.
/// </param>
/// <param name="RefillRequestedAt">
/// The instant of THIS request. Overwritten on every re-request, so it is the latest request time,
/// not the first.
/// </param>
public sealed record RxRefillRequestResponse(
    string? RefillStatus,
    DateTime? RefillRequestedAt);

/// <summary>
/// <c>PUT /api/prescriptions/{id}/refill-respond</c> → 200 (prescriptions.js:631).
///
/// <para>Exactly two keys, and a DIFFERENT pair from refill-request's:
/// <c>{ refillStatus, refillNotes }</c>. <c>refillRequestedAt</c> is absent here even though the
/// column still holds the original request timestamp — the response never reports it, and the
/// route never clears it.</para>
///
/// <para><c>refillStatus</c> is the caller's own <c>action</c> string stored verbatim, so it is
/// <c>"APPROVED"</c> or <c>"DENIED"</c> — nothing else can reach the write, because the action
/// gate rejects every other value with a 400 before the row is even loaded.</para>
/// </summary>
/// <param name="RefillStatus">
/// The raw uppercase action string as supplied by the physician and written verbatim into the
/// free-text column.
/// </param>
/// <param name="RefillNotes">
/// <c>notes || prescription.refillNotes</c> — the supplied note when truthy, otherwise the note
/// already on the row. **This is the PRESERVE half of the refillNotes asymmetry**: null here means
/// neither this response nor any earlier one carried notes. There is no way to clear the column
/// through this route.
/// </param>
public sealed record RxRefillRespondResponse(
    string? RefillStatus,
    string? RefillNotes);

/// <summary>
/// <c>PUT /api/prescriptions/{id}/stop-medication</c> → 200 (prescriptions.js:682).
///
/// <para><c>{ message, medications }</c>, in that order. <c>medications</c> is the WHOLE
/// in-memory array the handler just built — not the changed element, and not a re-read of the DB
/// row. Untouched elements pass through verbatim, with every original key in its original
/// order.</para>
///
/// <para>The targeted element is replaced by <c>{...meds[i], stoppedAt, stoppedByPatient}</c>, so
/// its original keys keep their order and the two flags are appended last (unless a key of that
/// name already existed, in which case JS overwrites the value in place and the position is
/// kept).</para>
///
/// <para><c>stoppedAt</c> is a <b>string</b>, not a date: Node builds it with
/// <c>new Date().toISOString()</c> inside the handler, so it is a string in the response AND in the
/// persisted JSON column, with exactly three fractional digits and a trailing <c>Z</c>.
/// <c>stoppedByPatient</c> is the literal boolean <c>true</c>.</para>
/// </summary>
/// <param name="Message">
/// <c>`Stopped ${meds[medicationIndex].drugName}`</c>. A template literal over whatever the element
/// carries, so an element with no <c>drugName</c> yields the literal text
/// <c>"Stopped undefined"</c> — with a 200. That is also what the NaN-index no-op path produces.
/// </param>
/// <param name="Medications">
/// The rebuilt array, opaque JSON echoed verbatim. Always an array and never null: when the stored
/// column held a non-array value Node's <c>Array.isArray(...) ? ... : []</c> makes it the EMPTY
/// array — which is then also written back over the column.
/// </param>
public sealed record RxRefillStopMedicationResponse(
    string Message,
    JsonArray Medications);

/// <summary>
/// <c>PUT /api/prescriptions/{id}/resume-medication</c> → 200 (prescriptions.js:723).
///
/// <para>The same two keys as stop-medication and a separate record on purpose: the two routes pin
/// opposite invariants on the element they touch, and audit-1 files a correction against exactly
/// one of them (see <c>Medications</c>).</para>
///
/// <para>The targeted element is rebuilt from
/// <c>const { stoppedAt, stoppedByPatient, ...cleanMed } = meds[i]</c>, so both flags are
/// <b>REMOVED</b> — absent keys, never nulls — and every surviving key keeps its original relative
/// order. Resuming an element that was never stopped is a legal 200 no-op.</para>
/// </summary>
/// <param name="Message">
/// <c>`Resumed ${meds[medicationIndex].drugName}`</c>, computed AFTER the element was replaced by
/// the cleaned copy. An element carrying only the two stop flags therefore yields
/// <c>"Resumed undefined"</c> with a 200.
/// </param>
/// <param name="Medications">
/// The rebuilt array. <b>audit-1 correction:</b> unlike stop-medication this can NEVER be the empty
/// array on a 200 — when the stored value is not an array every numeric index fails the bounds test
/// with a 400 and every non-numeric one destructures <c>undefined</c> into the route's 500, so no
/// 200 on this endpoint can carry <c>medications: []</c>. The chunk file's claim to the contrary is
/// unreachable.
/// </param>
public sealed record RxRefillResumeMedicationResponse(
    string Message,
    JsonArray Medications);
