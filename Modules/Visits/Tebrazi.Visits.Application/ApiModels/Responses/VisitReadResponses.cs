using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Pagination;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits — relation projections
//
//  The Node `include` on visits.js:113-121 selects a NARROWER set of columns than
//  GET /api/visits/{id} does, so these records are deliberately not shared with the
//  VisitReadDetail* family below. See docs/port-contracts/audit-3.json, GET / entry.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>The list route's clinic projection — <c>{ id, name, specialty }</c>, no phone.</summary>
public sealed record VisitReadListClinic(string Id, string Name, string? Specialty);

/// <summary>The list route's physician projection — no licenceNumber, and the user carries only a name.</summary>
public sealed record VisitReadListPhysician(string Id, string Specialty, VisitReadListPhysicianUser User);

public sealed record VisitReadListPhysicianUser(string DisplayName);

public sealed record VisitReadListSubprofile(string Id, string Name, string Relation);

public sealed record VisitReadListClinicPatient(string Id, string Name, string? Phone);

/// <summary>
/// The body of the literal <c>_count</c> key. Both figures ignore soft deletes, because the
/// Prisma <c>_count</c> applies no <c>where</c>.
/// </summary>
public sealed record VisitReadCounts(int Prescriptions, int Investigations);

/// <summary>
/// <c>patientUser</c> for a visit backed by a clinic-owned chart: exactly two keys, fabricated
/// from the ClinicPatient row (visits.js:157). There is NO id and NO email — the client cannot
/// navigate to an account from these rows, and normalising the shape would change the bytes.
/// </summary>
public sealed record VisitReadListChartPatientUser(string DisplayName, string? Phone);

/// <summary>
/// <c>patientUser</c> for a visit backed by a real account (visits.js:147-148): three keys, with
/// an id and an email but NO phone. The mirror-image key set of
/// <see cref="VisitReadListChartPatientUser"/>, and different again from
/// <see cref="VisitReadDetailPatientUser"/> on GET /api/visits/{id}.
/// </summary>
public sealed record VisitReadListAccountPatientUser(string Id, string DisplayName, string? Email);

/// <summary>
/// One row of the PHYSICIAN branch of <c>GET /api/visits</c>: every Visit scalar in schema order,
/// then the four relations, then <c>_count</c>, then <c>patientUser</c> appended by the spread.
/// </summary>
/// <param name="Diagnosis">A real <c>String[]</c> column — never null, <c>[]</c> when empty.</param>
/// <param name="DeletedAt">
/// Always null on this route (the list is the one Node query that applies the soft-delete
/// filter), but the KEY IS PRESENT.
/// </param>
/// <param name="PatientUser">
/// Typed <c>object</c> because the two variants above have incompatible key sets and
/// System.Text.Json resolves the runtime type only for a declared type of <c>object</c>.
/// </param>
public sealed record VisitReadPhysicianListItem(
    string Id,
    string OrganizationId,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    string? AudioUrl,
    string? RawTranscript,
    string? RawNotes,
    string? Subjective,
    string? Objective,
    string? Assessment,
    string? Plan,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis,
    JsonNode? DiagnosisCodes,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    JsonNode? SharedSections,
    string? PatientFeedback,
    DateTime? PatientFeedbackAt,
    DateTime? PatientDismissedAt,
    DateTime VisitDate,
    JsonNode? SpecialtyData,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    VisitReadListClinic? Clinic,
    VisitReadListPhysician? Physician,
    VisitReadListSubprofile? Subprofile,
    VisitReadListClinicPatient? ClinicPatient,
    [property: JsonPropertyName("_count")] VisitReadCounts Count,
    object? PatientUser);

/// <summary>
/// One row of the NON-physician branch of <c>GET /api/visits</c> — PATIENT, RECEPTIONIST and
/// STAFF all land here.
///
/// Seven keys are ABSENT rather than null (<c>subjective</c>, <c>objective</c>,
/// <c>assessment</c>, <c>plan</c>, <c>rawNotes</c>, <c>rawTranscript</c>, <c>audioUrl</c>):
/// visits.js:139 removes them by rest-destructuring, and a client testing
/// <c>'subjective' in visit</c> can tell the difference. There is no <c>patientUser</c> key
/// either — that enrichment is physician-only (visits.js:146-161). Hence a separate record
/// rather than conditional serialization.
///
/// <c>sharedSections</c> is NOT honoured here: the SOAP note is stripped wholesale even when the
/// physician shared some of it. Only <c>GET /api/visits/{id}</c> reads sharedSections.
/// </summary>
public sealed record VisitReadPatientListItem(
    string Id,
    string OrganizationId,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis,
    JsonNode? DiagnosisCodes,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    JsonNode? SharedSections,
    string? PatientFeedback,
    DateTime? PatientFeedbackAt,
    DateTime? PatientDismissedAt,
    DateTime VisitDate,
    JsonNode? SpecialtyData,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    VisitReadListClinic? Clinic,
    VisitReadListPhysician? Physician,
    VisitReadListSubprofile? Subprofile,
    VisitReadListClinicPatient? ClinicPatient,
    [property: JsonPropertyName("_count")] VisitReadCounts Count);

/// <summary>
/// The <c>{ data, pagination }</c> envelope of <c>GET /api/visits</c>.
/// </summary>
/// <param name="Data">
/// Holds <see cref="VisitReadPhysicianListItem"/> or <see cref="VisitReadPatientListItem"/> — one
/// or the other for the whole page, never a mix. The element type is <c>object</c> on purpose:
/// System.Text.Json serializes a value by its DECLARED type unless that type is <c>object</c>, so
/// a shared interface or base record here would silently emit empty rows.
/// </param>
public sealed record VisitReadListResponse(IReadOnlyList<object> Data, PaginationMeta Pagination);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits/{id} — a wider include, and its own relation projections
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>Adds <c>phone</c> to the list route's clinic projection (visits.js:281).</summary>
public sealed record VisitReadDetailClinic(string Id, string Name, string? Specialty, string? Phone);

public sealed record VisitReadDetailPhysician(
    string Id, string Specialty, string LicenseNumber, VisitReadDetailPhysicianUser User);

public sealed record VisitReadDetailPhysicianUser(string DisplayName, string? Email);

public sealed record VisitReadDetailSubprofile(string Id, string Name, string Relation);

/// <summary>
/// <c>patientUser</c> on the detail route. Unlike the list route this ONE shape covers both
/// branches — a chart-backed visit and an account-backed one both yield
/// <c>{ displayName, email, phone }</c> (visits.js:334 and :337) — and neither carries an
/// <c>id</c>, which the list route's account variant does. Null when the row it is built from
/// has gone.
/// </summary>
public sealed record VisitReadDetailPatientUser(string DisplayName, string? Email, string? Phone);

/// <summary>
/// A whole prescription row. The include is a bare <c>prescriptions: true</c> with no
/// <c>where</c>, so SOFT-DELETED prescriptions appear here with <c>deletedAt</c> set.
/// </summary>
public sealed record VisitReadDetailPrescription(
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
/// A whole investigation row, in Prisma schema order. There is no <c>createdAt</c> on this model
/// — <c>requestedAt</c> is the clinical timestamp the client reads.
/// </summary>
public sealed record VisitReadDetailInvestigation(
    string Id,
    string VisitId,
    string? SubprofileId,
    string Type,
    string Name,
    string? Instructions,
    string Status,
    string? ResultUrl,
    string? ResultNotes,
    DateTime RequestedAt,
    DateTime? CompletedAt,
    DateTime UpdatedAt);

/// <summary>
/// <c>GET /api/visits/{id}</c> as the OWNING PHYSICIAN sees it: every scalar including the raw
/// clinical input, <c>sharedSections</c> passed through verbatim, and <c>isPhysician: true</c>.
/// </summary>
/// <param name="DeletedAt">
/// Can be non-null here. This route is a bare <c>findUnique</c> with no soft-delete filter and no
/// status filter, so archived and soft-deleted visits are fully readable by id.
/// </param>
public sealed record VisitReadDetailPhysicianResponse(
    string Id,
    string OrganizationId,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    string? AudioUrl,
    string? RawTranscript,
    string? RawNotes,
    string? Subjective,
    string? Objective,
    string? Assessment,
    string? Plan,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis,
    JsonNode? DiagnosisCodes,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    JsonNode? SharedSections,
    string? PatientFeedback,
    DateTime? PatientFeedbackAt,
    DateTime? PatientDismissedAt,
    DateTime VisitDate,
    JsonNode? SpecialtyData,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    VisitReadDetailClinic? Clinic,
    VisitReadDetailPhysician? Physician,
    VisitReadDetailSubprofile? Subprofile,
    IReadOnlyList<VisitReadDetailPrescription> Prescriptions,
    IReadOnlyList<VisitReadDetailInvestigation> Investigations,
    VisitReadDetailPatientUser? PatientUser,
    bool IsPhysician);

/// <summary>
/// <c>GET /api/visits/{id}</c> as the PATIENT sees it (visits.js:341-364).
///
/// Three keys are ABSENT: <c>rawNotes</c>, <c>rawTranscript</c>, <c>audioUrl</c>. The four SOAP
/// keys are PRESENT but nulled when <c>sharedSections</c> is an array that omits them — absent
/// and null are different things to the client, which is why this record keeps them and the list
/// record does not.
///
/// <c>sharedSections</c> is the NORMALISED value: a stored value that is not a JSON array is
/// reported as null, whereas the physician branch returns whatever is stored. Its position is
/// unchanged — JS reassignment keeps a key where it already was, so it is not moved to the tail.
///
/// Prescriptions and investigations are returned IN FULL: sharedSections gates the four SOAP text
/// fields and nothing else.
/// </summary>
public sealed record VisitReadDetailPatientResponse(
    string Id,
    string OrganizationId,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    string? Subjective,
    string? Objective,
    string? Assessment,
    string? Plan,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis,
    JsonNode? DiagnosisCodes,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    JsonNode? SharedSections,
    string? PatientFeedback,
    DateTime? PatientFeedbackAt,
    DateTime? PatientDismissedAt,
    DateTime VisitDate,
    JsonNode? SpecialtyData,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    VisitReadDetailClinic? Clinic,
    VisitReadDetailPhysician? Physician,
    VisitReadDetailSubprofile? Subprofile,
    IReadOnlyList<VisitReadDetailPrescription> Prescriptions,
    IReadOnlyList<VisitReadDetailInvestigation> Investigations,
    VisitReadDetailPatientUser? PatientUser,
    bool IsPhysician);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits/inbox
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The inbox physician projection. It carries <c>userId</c>, which the list projection does not,
/// because the one caller — client/src/pages/ConnectionsPage.jsx:216 — keys its
/// last-visit-per-doctor map on <c>v.physicianUserId || v.physician?.userId</c> and can resolve
/// nothing without it.
/// </summary>
public sealed record VisitReadInboxPhysician(
    string Id, string UserId, string Specialty, VisitReadInboxPhysicianUser User);

public sealed record VisitReadInboxPhysicianUser(string DisplayName);

/// <summary>
/// One row of the patient's visit inbox. The patient key set — the same seven raw/SOAP keys are
/// absent as on the non-physician branch of <c>GET /api/visits</c>, and there is no
/// <c>patientUser</c> (the reader IS the patient).
/// </summary>
public sealed record VisitReadInboxItem(
    string Id,
    string OrganizationId,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis,
    JsonNode? DiagnosisCodes,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    JsonNode? SharedSections,
    string? PatientFeedback,
    DateTime? PatientFeedbackAt,
    DateTime? PatientDismissedAt,
    DateTime VisitDate,
    JsonNode? SpecialtyData,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    VisitReadListClinic? Clinic,
    VisitReadInboxPhysician? Physician,
    VisitReadListSubprofile? Subprofile,
    VisitReadListClinicPatient? ClinicPatient,
    [property: JsonPropertyName("_count")] VisitReadCounts Count);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits/follow-ups-due
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One flattened follow-up row (visits.js:1286-1299). Eleven keys and nothing else — the Prisma
/// include pulls subprofile and clinicPatient only to derive two strings, then throws both
/// objects away.
/// </summary>
/// <param name="PatientName">
/// Derived, never null: the chart's name, else the account's display name, else the literal
/// "Patient".
/// </param>
/// <param name="SubprofileName">
/// The KEY VANISHES when there is no subprofile — <c>v.subprofile?.name</c> is <c>undefined</c>
/// and JSON.stringify drops it. The explicit condition is required here because the host sets
/// <c>DefaultIgnoreCondition = Never</c>.
/// </param>
public sealed record VisitReadFollowUpDueItem(
    string Id,
    string PatientName,
    string PatientUserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubprofileName,
    string? ChiefComplaint,
    DateTime FollowUpDate,
    string? FollowUpNotes,
    DateTime VisitDate,
    bool IsOverdue,
    bool IsToday,
    string Urgency);

// ═════════════════════════════════════════════════════════════════════════════
//  Mapping
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Projects entities and port summaries onto the read responses. Every method takes its related
/// data already resolved: the handlers batch those lookups per page, and nothing in here may
/// issue a query.
/// </summary>
public static class VisitReadMapper
{
    /// <summary>
    /// Opaque JSON columns must reach the client as JSON VALUES, not as quoted strings. Invalid
    /// stored JSON degrades to null rather than failing the request, matching Prisma, which can
    /// only ever have written a valid document into the column.
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

    public static VisitReadListClinic? ToListClinic(ClinicSummary? clinic)
        => clinic is null
            ? null
            : new VisitReadListClinic(clinic.Id, clinic.Name, clinic.Specialty);

    public static VisitReadListPhysician? ToListPhysician(PhysicianSummary? physician)
        => physician is null
            ? null
            : new VisitReadListPhysician(
                physician.Id, physician.Specialty,
                new VisitReadListPhysicianUser(physician.DisplayName));

    public static VisitReadInboxPhysician? ToInboxPhysician(PhysicianSummary? physician)
        => physician is null
            ? null
            : new VisitReadInboxPhysician(
                physician.Id, physician.UserId, physician.Specialty,
                new VisitReadInboxPhysicianUser(physician.DisplayName));

    public static VisitReadListSubprofile? ToListSubprofile(SubprofileSummary? subprofile)
        => subprofile is null
            ? null
            : new VisitReadListSubprofile(subprofile.Id, subprofile.Name, subprofile.Relation);

    public static VisitReadListClinicPatient? ToListClinicPatient(ClinicPatientSummary? chart)
        => chart is null
            ? null
            : new VisitReadListClinicPatient(chart.Id, chart.Name, chart.Phone);

    public static VisitReadDetailClinic? ToDetailClinic(ClinicSummary? clinic)
        => clinic is null
            ? null
            : new VisitReadDetailClinic(clinic.Id, clinic.Name, clinic.Specialty, clinic.Phone);

    public static VisitReadDetailPhysician? ToDetailPhysician(PhysicianSummary? physician, UserSummary? owner)
        => physician is null
            ? null
            : new VisitReadDetailPhysician(
                physician.Id, physician.Specialty, physician.LicenseNumber,
                new VisitReadDetailPhysicianUser(
                    owner?.DisplayName ?? physician.DisplayName, owner?.Email));

    public static VisitReadDetailSubprofile? ToDetailSubprofile(SubprofileSummary? subprofile)
        => subprofile is null
            ? null
            : new VisitReadDetailSubprofile(subprofile.Id, subprofile.Name, subprofile.Relation);

    public static VisitReadDetailPrescription ToDetailPrescription(PrescriptionRow p)
        => new(p.Id, p.VisitId, p.PhysicianId, p.SubprofileId, p.Status, ParseJson(p.Medications),
               p.Notes, p.SignedAt, p.SentToPatientAt, p.PdfUrl, p.RefillRequestedAt,
               p.RefillStatus, p.RefillNotes, p.CreatedAt, p.UpdatedAt ?? p.CreatedAt, p.DeletedAt);

    public static VisitReadDetailInvestigation ToDetailInvestigation(Investigation i)
        => new(i.Id, i.VisitId, i.SubprofileId, i.Type, i.Name, i.Instructions, i.Status.ToString(),
               i.ResultUrl, i.ResultNotes, i.RequestedAt, i.CompletedAt, i.UpdatedAt ?? i.CreatedAt);

    public static VisitReadPhysicianListItem ToPhysicianListItem(
        Visit v,
        ClinicSummary? clinic,
        PhysicianSummary? physician,
        SubprofileSummary? subprofile,
        ClinicPatientSummary? chart,
        VisitReadCounts counts,
        object? patientUser)
        => new(v.Id, v.OrganizationId, v.ClinicId, v.PhysicianId, v.PatientUserId, v.SubprofileId,
               v.ClinicPatientId, v.Status.ToString(), v.AudioUrl, v.RawTranscript, v.RawNotes,
               v.Subjective, v.Objective, v.Assessment, v.Plan, v.ChiefComplaint, v.Diagnosis,
               ParseJson(v.DiagnosisCodes), v.FollowUpDate, v.FollowUpNotes,
               ParseJson(v.SharedSections), v.PatientFeedback, v.PatientFeedbackAt,
               v.PatientDismissedAt, v.VisitDate, ParseJson(v.SpecialtyData), v.CompletedAt,
               v.CreatedAt, v.UpdatedAt ?? v.CreatedAt, v.DeletedAt,
               ToListClinic(clinic), ToListPhysician(physician), ToListSubprofile(subprofile),
               ToListClinicPatient(chart), counts, patientUser);

    public static VisitReadPatientListItem ToPatientListItem(
        Visit v,
        ClinicSummary? clinic,
        PhysicianSummary? physician,
        SubprofileSummary? subprofile,
        ClinicPatientSummary? chart,
        VisitReadCounts counts)
        => new(v.Id, v.OrganizationId, v.ClinicId, v.PhysicianId, v.PatientUserId, v.SubprofileId,
               v.ClinicPatientId, v.Status.ToString(), v.ChiefComplaint, v.Diagnosis,
               ParseJson(v.DiagnosisCodes), v.FollowUpDate, v.FollowUpNotes,
               ParseJson(v.SharedSections), v.PatientFeedback, v.PatientFeedbackAt,
               v.PatientDismissedAt, v.VisitDate, ParseJson(v.SpecialtyData), v.CompletedAt,
               v.CreatedAt, v.UpdatedAt ?? v.CreatedAt, v.DeletedAt,
               ToListClinic(clinic), ToListPhysician(physician), ToListSubprofile(subprofile),
               ToListClinicPatient(chart), counts);

    public static VisitReadInboxItem ToInboxItem(
        Visit v,
        ClinicSummary? clinic,
        PhysicianSummary? physician,
        SubprofileSummary? subprofile,
        ClinicPatientSummary? chart,
        VisitReadCounts counts)
        => new(v.Id, v.OrganizationId, v.ClinicId, v.PhysicianId, v.PatientUserId, v.SubprofileId,
               v.ClinicPatientId, v.Status.ToString(), v.ChiefComplaint, v.Diagnosis,
               ParseJson(v.DiagnosisCodes), v.FollowUpDate, v.FollowUpNotes,
               ParseJson(v.SharedSections), v.PatientFeedback, v.PatientFeedbackAt,
               v.PatientDismissedAt, v.VisitDate, ParseJson(v.SpecialtyData), v.CompletedAt,
               v.CreatedAt, v.UpdatedAt ?? v.CreatedAt, v.DeletedAt,
               ToListClinic(clinic), ToInboxPhysician(physician), ToListSubprofile(subprofile),
               ToListClinicPatient(chart), counts);
}
