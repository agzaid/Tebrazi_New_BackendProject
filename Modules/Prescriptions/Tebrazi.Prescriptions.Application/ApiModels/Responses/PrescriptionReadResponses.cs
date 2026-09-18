using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tebrazi.Prescriptions.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Prescriptions.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response shapes for the READ group of server/src/routes/prescriptions.js:
//
//      GET /api/prescriptions                      (L31-L89)
//      GET /api/prescriptions/summary              (L144-L159)
//      GET /api/prescriptions/interaction-history  (L165-L184)
//      GET /api/prescriptions/{id}                 (L190-L233)
//      GET /api/prescriptions/{id}/pdf             (L874-L946)
//
//  Everything here is prefixed `RxRead` because the four Prescriptions handler groups are being
//  written into the same two namespaces in parallel — see docs/prescriptions-surface.md §10.
//
//  THREE DIFFERENT PROJECTIONS OF THE SAME RELATIONS. `visit`, `physician` and `subprofile` are
//  included with different `select`s on each of the three GET endpoints, so the nested records are
//  deliberately NOT shared between the list, the detail and the PDF:
//
//                | GET /                                  | GET /{id}                    | /{id}/pdf
//     visit      | id, chiefComplaint, visitDate,         | + clinic{phone, address}     | patientUserId,
//                | patientUserId, clinic{name}            |                              | clinicPatient{name},
//                |                                        |                              | clinic{name,address,
//                |                                        |                              | city,phone,logo}
//     physician  | specialty, user{displayName}           | + licenseNumber,             | specialty,
//                |                                        |   user{email}                | licenseNumber,
//                |                                        |                              | user{displayName}
//     subprofile | name, relation                         | + dateOfBirth                | name, relation
//
//  The PDF route has no JSON shape at all — it renders HTML — so it contributes only
//  RxReadPdfResponse below.
//
//  KEY ORDER IS THE CONTRACT. Records serialize in declaration order, so every prescription-row
//  record below lists the sixteen scalars in Prisma model order (id, visitId, physicianId,
//  subprofileId, status, medications, notes, signedAt, sentToPatientAt, pdfUrl, refillRequestedAt,
//  refillStatus, refillNotes, createdAt, updatedAt, deletedAt), then the included relations in
//  include order, then anything the JS object spread appends.
// ═════════════════════════════════════════════════════════════════════════════

// ── GET /api/prescriptions — relation projections ────────────────────────────

/// <summary>
/// The list route's clinic projection — <c>clinic: { select: { name: true } }</c>
/// (prescriptions.js:58). One key, no id, no phone, no address.
/// </summary>
public sealed record RxReadListClinic(string Name);

/// <summary>
/// The list route's physician projection — <c>{ specialty, user: { displayName } }</c>
/// (prescriptions.js:61). No id, no licenceNumber, no email: those belong to
/// <see cref="RxReadDetailPhysician"/>.
/// </summary>
public sealed record RxReadListPhysician(string? Specialty, RxReadListPhysicianUser User);

/// <summary>The one-key <c>user</c> object nested inside the list route's physician.</summary>
public sealed record RxReadListPhysicianUser(string DisplayName);

/// <summary>
/// The list route's subprofile projection — <c>{ name, relation }</c> (prescriptions.js:63).
/// <c>relation</c> stays the RAW uppercase enum string (SELF / SPOUSE / CHILD / PARENT / SIBLING /
/// OTHER); it is never localised or prettified.
/// </summary>
public sealed record RxReadListSubprofile(string Name, string Relation);

/// <summary>
/// The list route's visit projection (prescriptions.js:53-59).
///
/// <para><c>clinic</c> is typed nullable only for completeness: <c>Visit.clinicId</c> is a
/// REQUIRED column, so Node's include can never produce <c>"clinic": null</c> and the client's
/// optional chaining on it is defensive. Node does not dereference it either, so an unresolvable
/// clinic emits null here rather than failing the request — unlike the visit itself, which the
/// physician branch dereferences and which therefore raises the route's 500 (see
/// <c>RxReadListHandler</c>).</para>
/// </summary>
public sealed record RxReadListVisit(
    string Id,
    string? ChiefComplaint,
    DateTime VisitDate,
    string PatientUserId,
    RxReadListClinic? Clinic);

/// <summary>
/// One element of the PHYSICIAN branch of <c>GET /api/prescriptions</c> (prescriptions.js:75-78):
/// the sixteen scalars, the three relations, then <c>patientName</c> appended by the object
/// spread.
///
/// <para><b><c>patientName</c> is an ABSENT key, not null.</b> Node writes
/// <c>patientMap[...] || undefined</c> and <c>JSON.stringify</c> drops <c>undefined</c>, so a
/// prescription whose patient <c>User</c> row is gone (or whose display name is the empty string)
/// carries no <c>patientName</c> key at all. The host sets
/// <c>DefaultIgnoreCondition = Never</c>, which is why the condition has to be spelled out on the
/// property.</para>
///
/// <para><c>medications</c> is the opaque JSON column echoed VERBATIM — normally an array of
/// medication objects, but it keeps whatever extra keys the client originally posted, grows
/// <c>stoppedAt</c>/<c>stoppedByPatient</c> from the stop-medication route, and may legally be any
/// JSON value including a scalar. Never round-trip it through a typed record.</para>
///
/// <para><c>updatedAt</c> is NON-nullable, matching Prisma. <c>updatedAt DateTime @updatedAt</c>
/// (server/prisma/schema.prisma:693) is stamped on INSERT as well as on UPDATE, so Node never
/// emits null, whereas <c>MutableEntity.UpdatedAt</c> stays null until the first modification
/// (BaseDbContext stamps it only for <c>EntityState.Modified</c>). The mappers therefore emit
/// <c>UpdatedAt ?? CreatedAt</c> — byte-identical to Prisma for a never-modified row, and the same
/// idiom the finished Visits module already applies to this very row
/// (VisitReadResponses.cs:479).</para>
///
/// <para>No <c>deletedAt</c> filter runs anywhere on this route, so soft-deleted prescriptions are
/// listed and their <c>deletedAt</c> is on the wire.</para>
/// </summary>
public sealed record RxReadListPhysicianItem(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    PrescriptionStatus Status,
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
    RxReadListVisit Visit,
    RxReadListPhysician? Physician,
    RxReadListSubprofile? Subprofile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PatientName);

/// <summary>
/// One element of the NON-PHYSICIAN branch of <c>GET /api/prescriptions</c>
/// (prescriptions.js:81): byte-identical to <see cref="RxReadListPhysicianItem"/> minus the
/// <c>patientName</c> key entirely, because the enrichment block at prescriptions.js:69-79 runs
/// only for <c>userType === 'PHYSICIAN'</c>.
///
/// <para>A separate record rather than a nullable property, because "the key is absent for every
/// row on this branch" and "the key is absent for this row only" are different contracts and the
/// second one is what <see cref="RxReadListPhysicianItem.PatientName"/> models.</para>
/// </summary>
public sealed record RxReadListPatientItem(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    PrescriptionStatus Status,
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
    RxReadListVisit Visit,
    RxReadListPhysician? Physician,
    RxReadListSubprofile? Subprofile);

// ── GET /api/prescriptions/summary ───────────────────────────────────────────

/// <summary>
/// The dashboard badge counts (prescriptions.js:158). Exactly three keys, in this order, always
/// integers and never null.
/// </summary>
/// <param name="Signed">
/// <b>TWO statuses, despite the name</b>: the Node filter is
/// <c>status: { in: ['CONFIRMED', 'SIGNED'] }</c> (prescriptions.js:151). Creation writes SIGNED
/// and <c>PUT /{id}/sign</c> writes CONFIRMED, so both legitimately belong in this bucket and a
/// port that counts only SIGNED under-reports every signed prescription.
/// </param>
/// <param name="Sent">Status SENT alone.</param>
/// <param name="Total">
/// EVERY status, DRAFT and DISPENSED included, so <c>signed + sent != total</c>. No
/// <c>deletedAt</c> filter, so soft-deleted rows are counted in all three numbers.
/// </param>
public sealed record RxReadSummaryResponse(int Signed, int Sent, int Total);

// ── GET /api/prescriptions/interaction-history ───────────────────────────────

/// <summary>
/// One raw <c>InteractionAlert</c> row, echoed with NO reshaping (prescriptions.js:182). Keys are
/// in Prisma model order, and <c>checkedAt</c> — not <c>createdAt</c> — is the timestamp on the
/// wire.
/// </summary>
/// <param name="Id">The alert id.</param>
/// <param name="PhysicianId">
/// The physician PROFILE id, or null when a patient ran the check. Always non-null in a row this
/// endpoint can return, because it filters on it.
/// </param>
/// <param name="PatientUserId">The patient's USER id, or null when a physician ran the check.</param>
/// <param name="VisitId">
/// <b>Always null in practice.</b> The column exists but <c>POST /check-interactions</c> never
/// writes it (prescriptions.js:849-856).
/// </param>
/// <param name="Drugs">
/// Raw JSON — the <c>allDrugs</c> array. It can legally hold non-string objects, because
/// <c>.map(m =&gt; m.drugName || m)</c> (prescriptions.js:780) maps a medication object with no
/// <c>drugName</c> to the whole object. Emitted as a JSON value, never as an escaped string.
/// </param>
/// <param name="Interactions">Raw JSON straight from the AI gateway; <c>[]</c> by column default.</param>
/// <param name="AlertCount">The gateway's <c>interactions.length</c> at the time of the check.</param>
/// <param name="CheckedAt">The sort key, newest first.</param>
public sealed record RxReadInteractionHistoryItem(
    string Id,
    string? PhysicianId,
    string? PatientUserId,
    string? VisitId,
    JsonNode? Drugs,
    JsonNode? Interactions,
    int AlertCount,
    DateTime CheckedAt);

// ── GET /api/prescriptions/{id} — relation projections ───────────────────────

/// <summary>
/// The detail route's clinic projection — <c>{ name, phone, address }</c>
/// (prescriptions.js:198). Two keys wider than the list route's, and it has no city or logo,
/// which only the PDF needs.
/// </summary>
public sealed record RxReadDetailClinic(string Name, string? Phone, string? Address);

/// <summary>
/// The detail route's physician projection — <c>{ specialty, licenseNumber, user }</c>
/// (prescriptions.js:201-206).
/// </summary>
public sealed record RxReadDetailPhysician(
    string? Specialty,
    string? LicenseNumber,
    RxReadDetailPhysicianUser User);

/// <summary>The detail route's nested <c>user</c> — <c>{ displayName, email }</c>.</summary>
public sealed record RxReadDetailPhysicianUser(string DisplayName, string? Email);

/// <summary>
/// The detail route's subprofile projection — <c>{ name, relation, dateOfBirth }</c>
/// (prescriptions.js:207). <c>relation</c> is the raw uppercase enum string.
/// </summary>
public sealed record RxReadDetailSubprofile(string Name, string Relation, DateTime? DateOfBirth);

/// <summary>
/// The detail route's visit projection (prescriptions.js:193-199): the list route's four keys with
/// a three-key clinic instead of a one-key one.
/// </summary>
public sealed record RxReadDetailVisit(
    string Id,
    string? ChiefComplaint,
    DateTime VisitDate,
    string PatientUserId,
    RxReadDetailClinic? Clinic);

/// <summary>
/// <c>patientUser</c> on the detail route — <c>{ displayName, email, phone }</c>
/// (prescriptions.js:226-229), looked up separately after the authorization gate rather than
/// included. Null when the <c>User</c> row is missing, which Node emits as
/// <c>"patientUser": null</c>.
/// </summary>
public sealed record RxReadDetailPatientUser(string DisplayName, string? Email, string? Phone);

/// <summary>
/// <c>GET /api/prescriptions/{id}</c> (prescriptions.js:231):
/// <c>{ ...prescription, patientUser, isPhysician }</c> — the sixteen scalars, the three
/// relations, then the two appended keys.
///
/// <para><b><c>isPhysician</c> serializes as <c>null</c>, not <c>false</c>, for a caller with no
/// physician profile.</b> Node computes
/// <c>const isPhysician = physician &amp;&amp; prescription.physicianId === physician.id</c>
/// (prescriptions.js:215), which is the truthy-AND RESULT: when <c>physician</c> is null the whole
/// expression is <c>null</c> and that is what reaches the client. It is only <c>false</c> for a
/// caller who HAS a profile that did not author this prescription — which, given the <c>403</c>
/// gate, means the patient of the visit who also happens to be a physician. Emitting <c>false</c>
/// in the first case is a defect, so the property is <c>bool?</c> and the handler sets it to null
/// deliberately.</para>
///
/// <para>The same <c>medications</c>, <c>updatedAt</c> and <c>deletedAt</c> notes as
/// <see cref="RxReadListPhysicianItem"/> apply. This route is not called by the React client and
/// is ported for parity — see docs/port-contracts/audit-1.json, missedRoutes.</para>
/// </summary>
public sealed record RxReadDetailResponse(
    string Id,
    string VisitId,
    string PhysicianId,
    string? SubprofileId,
    PrescriptionStatus Status,
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
    RxReadDetailVisit Visit,
    RxReadDetailPhysician? Physician,
    RxReadDetailSubprofile? Subprofile,
    RxReadDetailPatientUser? PatientUser,
    bool? IsPhysician);

// ── GET /api/prescriptions/{id}/pdf ──────────────────────────────────────────

/// <summary>
/// <c>GET /api/prescriptions/{id}/pdf</c> — the ONE endpoint in this module whose success body is
/// not JSON.
///
/// <para><b>It returns HTML, not a PDF.</b> Node sets
/// <c>res.setHeader('Content-Type', 'text/html')</c> and <c>res.send(html)</c>
/// (prescriptions.js:939-940); Express's <c>res.send</c> appends the charset, so the header on the
/// wire is <c>text/html; charset=utf-8</c>. There are no PDF bytes, no
/// <c>application/pdf</c> and no <c>Content-Disposition</c> — the client fetches this with
/// <c>responseType: 'text'</c> and <c>document.write()</c>s it into a new window.</para>
///
/// <para><b>Note for the controller author.</b> Return the payload as content, not as JSON:
/// <c>return Content(response.Html, RxReadPdfResponse.ContentType);</c> — pass
/// <see cref="ContentType"/> verbatim so the charset is present, and do not add a
/// <c>Content-Disposition</c>. ERRORS on this route stay JSON: the 404, the 403 and the 500 come
/// out of the exception middleware as <c>{"error": "..."}</c> with
/// <c>application/json</c>, exactly as in Node, where the content type flips per branch because
/// the <c>setHeader</c> call only runs on the happy path.</para>
///
/// <para>The HTML must be served as UTF-8 unchanged: it carries the <c>🖨</c> and <c>℞</c> glyphs
/// and the U+2014 em-dash cell fallback.</para>
/// </summary>
/// <param name="Html">
/// The document from <c>RxDocumentRenderer.RenderHtml</c>, byte-for-byte what
/// <c>generateRxPDFHtml</c> produces. Never null and never empty.
/// </param>
public sealed record RxReadPdfResponse(string Html)
{
    /// <summary>
    /// The exact content type Express ends up sending. Emitting a bare <c>text/html</c> would not
    /// match the Node response.
    /// </summary>
    public const string ContentType = "text/html; charset=utf-8";
}

// ═════════════════════════════════════════════════════════════════════════════
//  Mapping
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Projects prescriptions and cross-module port summaries onto the read responses. Every method
/// takes its related data ALREADY RESOLVED — the handlers batch those lookups once per response —
/// and nothing in here may issue a query.
/// </summary>
public static class RxReadMapper
{
    /// <summary>
    /// Opaque JSON columns (<c>medications</c>, <c>drugs</c>, <c>interactions</c>) must reach the
    /// client as JSON VALUES, not as quoted strings.
    ///
    /// <para>Invalid stored JSON degrades to null rather than failing the request, matching
    /// Prisma, which can only ever have written a valid document into the column. A stored JSON
    /// <c>null</c> also parses to a null node and therefore serializes as <c>null</c>, which is
    /// what Node emits for it.</para>
    /// </summary>
    /// <param name="json">The raw column text.</param>
    /// <returns>The parsed node, or null for null, blank or unparseable text.</returns>
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

    /// <summary>The list route's one-key clinic, or null when the clinic could not be resolved.</summary>
    /// <param name="clinic">The resolved clinic, if any.</param>
    public static RxReadListClinic? ToListClinic(ClinicSummary? clinic)
        => clinic is null ? null : new RxReadListClinic(clinic.Name);

    /// <summary>
    /// The list route's physician, or null when the author's profile could not be resolved. Node
    /// never dereferences this object, so a null is emitted rather than raising the route's 500.
    /// </summary>
    /// <param name="physician">The AUTHOR's physician profile, if any.</param>
    public static RxReadListPhysician? ToListPhysician(PhysicianSummary? physician)
        => physician is null
            ? null
            : new RxReadListPhysician(
                physician.Specialty,
                new RxReadListPhysicianUser(physician.DisplayName));

    /// <summary>The list route's two-key subprofile, or null when the prescription has none.</summary>
    /// <param name="subprofile">The resolved subprofile, if any.</param>
    public static RxReadListSubprofile? ToListSubprofile(SubprofileSummary? subprofile)
        => subprofile is null
            ? null
            : new RxReadListSubprofile(subprofile.Name, subprofile.Relation);

    /// <summary>The list route's visit object.</summary>
    /// <param name="visit">The resolved visit. Required — see <see cref="RxReadListVisit"/>.</param>
    /// <param name="clinic">The visit's clinic, if resolved.</param>
    public static RxReadListVisit ToListVisit(VisitSummary visit, ClinicSummary? clinic)
        => new(visit.Id, visit.ChiefComplaint, visit.VisitDate, visit.PatientUserId,
            ToListClinic(clinic));

    /// <summary>
    /// One physician-branch list element.
    /// </summary>
    /// <param name="p">The prescription row.</param>
    /// <param name="visit">Its visit projection.</param>
    /// <param name="physician">Its author projection, or null.</param>
    /// <param name="subprofile">Its subprofile projection, or null.</param>
    /// <param name="patientName">
    /// The patient's display name, or null to DROP the key entirely — which is what Node's
    /// <c>|| undefined</c> does for a missing <c>User</c> row.
    /// </param>
    public static RxReadListPhysicianItem ToPhysicianListItem(
        Prescription p,
        RxReadListVisit visit,
        RxReadListPhysician? physician,
        RxReadListSubprofile? subprofile,
        string? patientName)
        => new(
            p.Id, p.VisitId, p.PhysicianId, p.SubprofileId, p.Status, ParseJson(p.Medications),
            p.Notes, p.SignedAt, p.SentToPatientAt, p.PdfUrl, p.RefillRequestedAt, p.RefillStatus,
            p.RefillNotes, p.CreatedAt, p.UpdatedAt ?? p.CreatedAt, p.DeletedAt,
            visit, physician, subprofile, patientName);

    /// <summary>One non-physician-branch list element — the same keys with no <c>patientName</c>.</summary>
    /// <param name="p">The prescription row.</param>
    /// <param name="visit">Its visit projection.</param>
    /// <param name="physician">Its author projection, or null.</param>
    /// <param name="subprofile">Its subprofile projection, or null.</param>
    public static RxReadListPatientItem ToPatientListItem(
        Prescription p,
        RxReadListVisit visit,
        RxReadListPhysician? physician,
        RxReadListSubprofile? subprofile)
        => new(
            p.Id, p.VisitId, p.PhysicianId, p.SubprofileId, p.Status, ParseJson(p.Medications),
            p.Notes, p.SignedAt, p.SentToPatientAt, p.PdfUrl, p.RefillRequestedAt, p.RefillStatus,
            p.RefillNotes, p.CreatedAt, p.UpdatedAt ?? p.CreatedAt, p.DeletedAt,
            visit, physician, subprofile);

    /// <summary>The detail route's three-key clinic, or null when it could not be resolved.</summary>
    /// <param name="clinic">The resolved clinic, if any.</param>
    public static RxReadDetailClinic? ToDetailClinic(ClinicSummary? clinic)
        => clinic is null
            ? null
            : new RxReadDetailClinic(clinic.Name, clinic.Phone, clinic.Address);

    /// <summary>
    /// The detail route's physician. <c>user.displayName</c> and <c>user.email</c> come from the
    /// author's <c>User</c> row, falling back to the profile's own display name when that row
    /// could not be read — the same shape Visits uses for its detail projection.
    /// </summary>
    /// <param name="physician">The AUTHOR's physician profile, if any.</param>
    /// <param name="owner">The author's user account, if resolved.</param>
    public static RxReadDetailPhysician? ToDetailPhysician(
        PhysicianSummary? physician, UserSummary? owner)
        => physician is null
            ? null
            : new RxReadDetailPhysician(
                physician.Specialty,
                physician.LicenseNumber,
                new RxReadDetailPhysicianUser(
                    owner?.DisplayName ?? physician.DisplayName, owner?.Email));

    /// <summary>The detail route's three-key subprofile, or null when the prescription has none.</summary>
    /// <param name="subprofile">The resolved subprofile, if any.</param>
    public static RxReadDetailSubprofile? ToDetailSubprofile(SubprofileSummary? subprofile)
        => subprofile is null
            ? null
            : new RxReadDetailSubprofile(
                subprofile.Name, subprofile.Relation, subprofile.DateOfBirth);

    /// <summary>The detail route's visit object.</summary>
    /// <param name="visit">The resolved visit.</param>
    /// <param name="clinic">The visit's clinic, if resolved.</param>
    public static RxReadDetailVisit ToDetailVisit(VisitSummary visit, ClinicSummary? clinic)
        => new(visit.Id, visit.ChiefComplaint, visit.VisitDate, visit.PatientUserId,
            ToDetailClinic(clinic));

    /// <summary>
    /// <c>patientUser</c>, or null when the row is missing — Node's <c>findUnique</c> returns null
    /// and the key is present with a null value.
    /// </summary>
    /// <param name="patient">The visit's patient account, if resolved.</param>
    public static RxReadDetailPatientUser? ToDetailPatientUser(UserSummary? patient)
        => patient is null
            ? null
            : new RxReadDetailPatientUser(patient.DisplayName, patient.Email, patient.Phone);

    /// <summary>The whole detail body.</summary>
    /// <param name="p">The prescription row.</param>
    /// <param name="visit">Its visit projection.</param>
    /// <param name="physician">Its author projection, or null.</param>
    /// <param name="subprofile">Its subprofile projection, or null.</param>
    /// <param name="patientUser">The visit patient's account projection, or null.</param>
    /// <param name="isPhysician">
    /// <b>null</b> when the caller has no physician profile at all — never <c>false</c>. See
    /// <see cref="RxReadDetailResponse"/>.
    /// </param>
    public static RxReadDetailResponse ToDetail(
        Prescription p,
        RxReadDetailVisit visit,
        RxReadDetailPhysician? physician,
        RxReadDetailSubprofile? subprofile,
        RxReadDetailPatientUser? patientUser,
        bool? isPhysician)
        => new(
            p.Id, p.VisitId, p.PhysicianId, p.SubprofileId, p.Status, ParseJson(p.Medications),
            p.Notes, p.SignedAt, p.SentToPatientAt, p.PdfUrl, p.RefillRequestedAt, p.RefillStatus,
            p.RefillNotes, p.CreatedAt, p.UpdatedAt ?? p.CreatedAt, p.DeletedAt,
            visit, physician, subprofile, patientUser, isPhysician);

    /// <summary>
    /// One interaction-history element, with both JSON columns re-parsed so they land on the wire
    /// as JSON values rather than escaped strings.
    /// </summary>
    /// <param name="alert">The stored alert.</param>
    public static RxReadInteractionHistoryItem ToInteractionHistoryItem(InteractionAlert alert)
        => new(
            alert.Id, alert.PhysicianId, alert.PatientUserId, alert.VisitId,
            ParseJson(alert.Drugs), ParseJson(alert.Interactions),
            alert.AlertCount, alert.CheckedAt);
}
