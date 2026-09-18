using System.Text.Json;
using System.Text.Json.Nodes;
using Tebrazi.Prescriptions.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Prescriptions.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response records for the five lifecycle endpoints, ported from
//  server/src/routes/prescriptions.js:
//
//      POST /api/prescriptions               (L99-L138)   -> 201
//      PUT  /api/prescriptions/{id}          (L242-L275)  -> 200
//      PUT  /api/prescriptions/{id}/sign     (L280-L302)  -> 200
//      PUT  /api/prescriptions/{id}/send     (L308-L435)  -> 200
//      PUT  /api/prescriptions/{id}/dispense (L446-L512)  -> 200
//
//  All five bodies open with the SAME sixteen Prescription scalars in PRISMA DECLARATION ORDER
//  (schema.prisma:679-694) — id, visitId, physicianId, subprofileId, status, medications, notes,
//  signedAt, sentToPatientAt, pdfUrl, refillRequestedAt, refillStatus, refillNotes, createdAt,
//  updatedAt, deletedAt — and then diverge:
//
//      POST /            appends a two-key `visit` object    (from the create call's `include`)
//      PUT  /{id}        appends NOTHING — the bare row      (no include, no message)
//      /sign, /send      append `message`
//      /dispense         appends `message` THEN `inventoryDeducted`, in that order
//
//  Record declaration order IS the JSON key order, so nothing may be reordered and the appended
//  keys must stay last.
//
//  FIVE RECORDS, NOT ONE SHARED ROW. Deliberate, and stated in docs/prescriptions-surface.md §10:
//  no two of the six prescription-carrying responses in this file's router are byte-identical, so
//  a shared base would force one of them to be wrong. Each record below also pins a different
//  invariant — which status is guaranteed, which `*At` column is guaranteed non-null, and which
//  message literal is appended — and a shared record would let an edit to one route silently
//  reshape the other four.
//
//  `medications` is an OPAQUE JSON column. It must reach the client as a JSON VALUE, not as a
//  quoted string, which is why it is a JsonNode? and not a string — see RxLifecycleMapper.ParseJson
//  below.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The nested <c>visit</c> object of <c>POST /api/prescriptions</c> — and it is
/// <b>only these two keys</b>. The create call's include is
/// <c>visit: { select: { chiefComplaint: true, visitDate: true } }</c> (prescriptions.js:128-130), so
/// there is no <c>id</c>, no <c>patientUserId</c> and no <c>clinic</c>.
///
/// <para>This is the FOURTH distinct visit projection in the router: <c>GET /</c>,
/// <c>GET /{id}</c> and <c>GET /{id}/pdf</c> each select a different column set. Never share a
/// nested visit DTO between them.</para>
///
/// <para><c>visitDate</c> is a required column, so the object itself is never null — the response
/// always carries a <c>visit</c> key with both fields present, and only
/// <c>chiefComplaint</c> can be null.</para>
/// </summary>
public sealed record RxLifecycleCreatedVisit(
    string? ChiefComplaint,
    DateTime VisitDate);

/// <summary>
/// <c>POST /api/prescriptions</c> → <b>201</b> (prescriptions.js:118-137).
///
/// <para><b>The row is created AUTO-SIGNED.</b> <c>status</c> is always the literal
/// <c>"SIGNED"</c> and <c>signedAt</c> is always a fresh timestamp, never null — the physician's
/// authenticated account implicitly signs at creation (prescriptions.js:125-126). The schema
/// default of DRAFT is overridden, which makes DRAFT unreachable through any ported endpoint.
/// A port that respected the default would break the whole downstream flow.</para>
///
/// <para><c>subprofileId</c> is copied from the VISIT, never from the request body — a client that
/// sends <c>subprofileId</c>, <c>status</c> or <c>signedAt</c> is silently ignored.</para>
///
/// <para><c>sentToPatientAt</c>, <c>pdfUrl</c>, <c>refillRequestedAt</c>, <c>refillStatus</c>,
/// <c>refillNotes</c> and <c>deletedAt</c> are all null on this path — they take their schema
/// defaults and nothing in the create writes them. There is no <c>physician</c> and no
/// <c>subprofile</c> object, and no <c>message</c> key.</para>
///
/// <para>The status is <b>201</b>, not 200 — the only 2xx in this router that is not 200, and it
/// is client-visible.</para>
/// </summary>
public sealed record RxLifecycleCreatedResponse(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    string Status,
    JsonNode? Medications,
    string? Notes,
    DateTime? SignedAt,
    DateTime? SentToPatientAt,
    string? PdfUrl,
    DateTime? RefillRequestedAt,
    string? RefillStatus,
    string? RefillNotes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    RxLifecycleCreatedVisit Visit);

/// <summary>
/// <c>PUT /api/prescriptions/{id}</c> → 200 (prescriptions.js:267-269).
///
/// <para><b>The bare row.</b> <c>prisma.update</c> here carries no <c>include</c> and the handler
/// appends nothing, so this is exactly the sixteen scalars: no nested <c>visit</c> (unlike
/// <c>POST /</c>) and no <c>message</c> (unlike /sign, /send and /dispense). It is the only
/// prescription-carrying response in the router with nothing appended.</para>
///
/// <para><c>status</c> is UNCHANGED by this route — whatever the row already had, minus SENT,
/// which the handler rejects with 400. So this can report SIGNED, CONFIRMED, DISPENSED or DRAFT
/// but never SENT.</para>
///
/// <para>An EMPTY patch is a successful 200 here: Node builds <c>updateData</c> with
/// <c>!== undefined</c> guards, so <c>{}</c> issues <c>prisma.update({ data: {} })</c>, which
/// still succeeds and still bumps <c>updatedAt</c>. The handler reproduces that with
/// <c>IPrescriptionStore.MarkModified</c>.</para>
///
/// <para>This route is not called by the React client; it is ported for parity because it holds
/// the file's only status precondition (docs/port-contracts/audit-1.json, missedRoutes).</para>
/// </summary>
public sealed record RxLifecycleUpdatedResponse(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    string Status,
    JsonNode? Medications,
    string? Notes,
    DateTime? SignedAt,
    DateTime? SentToPatientAt,
    string? PdfUrl,
    DateTime? RefillRequestedAt,
    string? RefillStatus,
    string? RefillNotes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt);

/// <summary>
/// <c>PUT /api/prescriptions/{id}/sign</c> → 200 (prescriptions.js:293-298).
///
/// <para><b>THE BIG ONE: <c>status</c> is always the literal <c>"CONFIRMED"</c>, never
/// <c>"SIGNED"</c></b> (prescriptions.js:294). Creation already wrote SIGNED, so "signing" is the
/// rung above it. A port that emitted SIGNED here would silently drop the prescription out of the
/// patient list filter (<c>CONFIRMED | SENT | DISPENSED</c>) while leaving <c>GET /summary</c>
/// unaffected, because its <c>signed</c> bucket counts BOTH statuses.</para>
///
/// <para><c>signedAt</c> is always a fresh timestamp, never null: the update writes
/// <c>signedAt: new Date()</c> UNCONDITIONALLY, so re-signing loses the original signature
/// instant and this body reports the new one.</para>
///
/// <para>There is NO status precondition, so a SENT or DISPENSED row can be signed and regresses
/// to CONFIRMED while keeping its <c>sentToPatientAt</c> — this record can legitimately carry
/// <c>status: "CONFIRMED"</c> alongside a non-null <c>sentToPatientAt</c>.</para>
///
/// <para><c>message</c> is <c>"Prescription signed"</c> and serializes LAST, after
/// <c>deletedAt</c>, because Node appends it with an object spread.</para>
/// </summary>
public sealed record RxLifecycleSignedResponse(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    string Status,
    JsonNode? Medications,
    string? Notes,
    DateTime? SignedAt,
    DateTime? SentToPatientAt,
    string? PdfUrl,
    DateTime? RefillRequestedAt,
    string? RefillStatus,
    string? RefillNotes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    string Message);

/// <summary>
/// <c>PUT /api/prescriptions/{id}/send</c> → 200 (prescriptions.js:322-326, :430).
///
/// <para><c>status</c> is always the literal <c>"SENT"</c> and <c>sentToPatientAt</c> always a
/// fresh timestamp. <b><c>signedAt</c> is NOT touched here</b> — the update writes only those two
/// columns — so a row that was never signed still reports <c>signedAt: null</c> while reading
/// SENT.</para>
///
/// <para>There is no status precondition (the Node comment "Auto-sign is now done at creation —
/// no DRAFT block needed" documents the removal of the guard), so a DISPENSED prescription can be
/// re-sent and REGRESSES to SENT.</para>
///
/// <para><b>Nothing about the side effects appears here.</b> The patient notification, the email,
/// the push, the reminder purge and the created reminder rows contribute no key, no count and no
/// id, and both of Node's side-effect blocks are individually try/catch-swallowed. That is what
/// makes this body byte-identical whether or not the no-op reminder port did anything.</para>
///
/// <para><c>message</c> is <c>"Prescription sent to patient"</c>, appended LAST.</para>
/// </summary>
public sealed record RxLifecycleSentResponse(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    string Status,
    JsonNode? Medications,
    string? Notes,
    DateTime? SignedAt,
    DateTime? SentToPatientAt,
    string? PdfUrl,
    DateTime? RefillRequestedAt,
    string? RefillStatus,
    string? RefillNotes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    string Message);

/// <summary>
/// One element of <c>PUT /api/prescriptions/{id}/dispense</c>'s <c>inventoryDeducted</c> array —
/// <b>exactly four keys</b> (prescriptions.js:503).
///
/// <para><c>itemId</c> is echoed from the REQUEST and is deliberately not called <c>id</c>: it
/// does not match the inventory item's own primary-key field name. <c>name</c> is the item's name
/// from the PRE-decrement read, the one value in the whole body that comes from a different model.
/// <c>quantity</c> is the POSITIVE number taken, even though the inventory transaction row stores
/// its negation — opposite signs for the same number. <c>newStock</c> is read back AFTER the
/// atomic decrement and is not clamped, so under concurrency it can differ from the clamped value
/// on the transaction row.</para>
///
/// <para>A negative requested quantity is legal and INCREASES stock; the element then reports a
/// negative <c>quantity</c> against a raised <c>newStock</c>.</para>
///
/// <para>Declared here rather than reusing the kernel's <c>InventoryDeductionLine</c> so the wire
/// shape of this endpoint is pinned in this module and cannot drift with the port's record.</para>
/// </summary>
public sealed record RxLifecycleInventoryDeductedLine(
    string ItemId,
    string Name,
    int Quantity,
    int NewStock);

/// <summary>
/// <c>PUT /api/prescriptions/{id}/dispense</c> → 200 (prescriptions.js:459-462, :507).
///
/// <para><c>status</c> is always the literal <c>"DISPENSED"</c>. There is no precondition, so a
/// DRAFT or an already-DISPENSED prescription can be dispensed and each call re-runs the
/// deduction.</para>
///
/// <para>The two appended keys serialize in exactly this order: <c>message</c>
/// (<c>"Prescription dispensed"</c>) and then <c>inventoryDeducted</c>.</para>
///
/// <para><b><c>inventoryDeducted</c> is ALWAYS an array — never null and never an absent key.</b>
/// It is <c>[]</c> when the body carried no <c>inventoryItems</c>, when that value was not an
/// array, when it was empty, and when every item was skipped. Elements are in request order minus
/// the skipped ones. The live client sends no body at all, so <c>[]</c> is what it always
/// sees.</para>
/// </summary>
public sealed record RxLifecycleDispensedResponse(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    string Status,
    JsonNode? Medications,
    string? Notes,
    DateTime? SignedAt,
    DateTime? SentToPatientAt,
    string? PdfUrl,
    DateTime? RefillRequestedAt,
    string? RefillStatus,
    string? RefillNotes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    string Message,
    IReadOnlyList<RxLifecycleInventoryDeductedLine> InventoryDeducted);

// ═════════════════════════════════════════════════════════════════════════════
//  Mapping
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Projects a <see cref="Prescription"/> onto the five lifecycle responses. Every method takes its
/// related data already resolved — nothing in here may issue a query or read a clock.
///
/// <para>The five projections are written out longhand rather than funnelled through a shared
/// helper, because the whole point of the five records is that a change to one endpoint's shape
/// must not be able to reach the other four.</para>
/// </summary>
public static class RxLifecycleMapper
{
    /// <summary>
    /// The opaque <c>medications</c> column must reach the client as a JSON VALUE, not as a quoted
    /// string. Invalid stored JSON degrades to null rather than failing the request, matching
    /// Prisma, which can only ever have written a valid document into the column.
    ///
    /// <para>Note the column can legitimately hold a JSON SCALAR: <c>PUT /{id}</c> performs no
    /// array validation, so <c>{"medications": 5}</c> really is persisted as <c>5</c> and echoed
    /// back as <c>5</c>.</para>
    /// </summary>
    public static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>POST /api/prescriptions</c>'s 201 body. <paramref name="visit"/> supplies the two-key
    /// nested object; only <c>chiefComplaint</c> and <c>visitDate</c> are read from it.
    /// </summary>
    public static RxLifecycleCreatedResponse ToCreatedResponse(
        Prescription prescription, VisitSummary visit)
        => new(
            prescription.Id,
            prescription.VisitId,
            prescription.PhysicianId,
            prescription.SubprofileId,
            prescription.Status.ToString(),
            ParseJson(prescription.Medications),
            prescription.Notes,
            prescription.SignedAt,
            prescription.SentToPatientAt,
            prescription.PdfUrl,
            prescription.RefillRequestedAt,
            prescription.RefillStatus,
            prescription.RefillNotes,
            prescription.CreatedAt,
            prescription.UpdatedAt ?? prescription.CreatedAt,
            prescription.DeletedAt,
            new RxLifecycleCreatedVisit(visit.ChiefComplaint, visit.VisitDate));

    /// <summary><c>PUT /api/prescriptions/{id}</c>'s bare 200 body.</summary>
    public static RxLifecycleUpdatedResponse ToUpdatedResponse(Prescription prescription)
        => new(
            prescription.Id,
            prescription.VisitId,
            prescription.PhysicianId,
            prescription.SubprofileId,
            prescription.Status.ToString(),
            ParseJson(prescription.Medications),
            prescription.Notes,
            prescription.SignedAt,
            prescription.SentToPatientAt,
            prescription.PdfUrl,
            prescription.RefillRequestedAt,
            prescription.RefillStatus,
            prescription.RefillNotes,
            prescription.CreatedAt,
            prescription.UpdatedAt ?? prescription.CreatedAt,
            prescription.DeletedAt);

    /// <summary>
    /// <c>PUT /api/prescriptions/{id}/sign</c>'s 200 body. <paramref name="message"/> is always
    /// <c>"Prescription signed"</c> and lands last.
    /// </summary>
    public static RxLifecycleSignedResponse ToSignedResponse(
        Prescription prescription, string message)
        => new(
            prescription.Id,
            prescription.VisitId,
            prescription.PhysicianId,
            prescription.SubprofileId,
            prescription.Status.ToString(),
            ParseJson(prescription.Medications),
            prescription.Notes,
            prescription.SignedAt,
            prescription.SentToPatientAt,
            prescription.PdfUrl,
            prescription.RefillRequestedAt,
            prescription.RefillStatus,
            prescription.RefillNotes,
            prescription.CreatedAt,
            prescription.UpdatedAt ?? prescription.CreatedAt,
            prescription.DeletedAt,
            message);

    /// <summary>
    /// <c>PUT /api/prescriptions/{id}/send</c>'s 200 body. <paramref name="message"/> is always
    /// <c>"Prescription sent to patient"</c> and lands last.
    /// </summary>
    public static RxLifecycleSentResponse ToSentResponse(
        Prescription prescription, string message)
        => new(
            prescription.Id,
            prescription.VisitId,
            prescription.PhysicianId,
            prescription.SubprofileId,
            prescription.Status.ToString(),
            ParseJson(prescription.Medications),
            prescription.Notes,
            prescription.SignedAt,
            prescription.SentToPatientAt,
            prescription.PdfUrl,
            prescription.RefillRequestedAt,
            prescription.RefillStatus,
            prescription.RefillNotes,
            prescription.CreatedAt,
            prescription.UpdatedAt ?? prescription.CreatedAt,
            prescription.DeletedAt,
            message);

    /// <summary>
    /// <c>PUT /api/prescriptions/{id}/dispense</c>'s 200 body. <paramref name="message"/> is
    /// always <c>"Prescription dispensed"</c>, and <paramref name="inventoryDeducted"/> is emitted
    /// after it — empty, never null.
    /// </summary>
    public static RxLifecycleDispensedResponse ToDispensedResponse(
        Prescription prescription,
        string message,
        IReadOnlyList<RxLifecycleInventoryDeductedLine> inventoryDeducted)
        => new(
            prescription.Id,
            prescription.VisitId,
            prescription.PhysicianId,
            prescription.SubprofileId,
            prescription.Status.ToString(),
            ParseJson(prescription.Medications),
            prescription.Notes,
            prescription.SignedAt,
            prescription.SentToPatientAt,
            prescription.PdfUrl,
            prescription.RefillRequestedAt,
            prescription.RefillStatus,
            prescription.RefillNotes,
            prescription.CreatedAt,
            prescription.UpdatedAt ?? prescription.CreatedAt,
            prescription.DeletedAt,
            message,
            inventoryDeducted);
}
