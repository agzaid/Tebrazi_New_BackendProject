using System.Text.Json.Nodes;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Application.ApiModels.Responses;

// ─────────────────────────────────────────────────────────────────────────────
// Response records for the visit WRITE endpoints (POST /, PUT /{id},
// PUT /{id}/complete, DELETE /{id}, PATCH /{id}/patient-note,
// PUT /{id}/patient-feedback, DELETE /{id}/patient-dismiss).
//
// Five distinct shapes, deliberately not one shared DTO. visits.js projects a different key set
// per endpoint and the client sees the difference: POST / returns three narrow relation objects
// nobody else returns, PATCH /{id}/patient-note returns three keys, PUT /{id}/patient-feedback
// returns two of the same three but WITHOUT `id`, and PUT /{id}/complete is the visit row with a
// trailing `message`. Record declaration order is the JSON key order, so it mirrors the Prisma
// model field order the Express responses had.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <c>POST /api/visits</c> → 201. The created visit plus the three relations of the Prisma
/// <c>include</c> at visits.js:231-236.
///
/// The relation projections here are NARROWER than every other visit endpoint's and must not be
/// swapped for the GET ones: <c>clinic</c> is <c>{ name }</c> alone (no id, no specialty, no
/// phone) and <c>subprofile</c> is <c>{ name, relation }</c> with NO id.
/// </summary>
public sealed record VisitWriteCreatedResponse(
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
    VisitWriteCreatedClinicResponse Clinic,
    VisitWriteCreatedSubprofileResponse? Subprofile,
    VisitWriteCreatedClinicPatientResponse? ClinicPatient);

/// <summary>Only the clinic's name — <c>select: { name: true }</c>, visits.js:232.</summary>
public sealed record VisitWriteCreatedClinicResponse(string Name);

/// <summary>Name and relation only. No <c>id</c>, unlike the GET endpoints' subprofile object.</summary>
public sealed record VisitWriteCreatedSubprofileResponse(string Name, string Relation);

public sealed record VisitWriteCreatedClinicPatientResponse(string Id, string Name, string? Phone);

/// <summary>
/// <c>PUT /api/visits/{id}</c> → 200. The raw visit row: every scalar, no relations, no
/// <c>message</c>. Both branches of the handler (the completed-visit follow-up-only path and the
/// ordinary documentation patch) answer with this same shape.
/// </summary>
public sealed record VisitWriteVisitResponse(
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
    DateTime? DeletedAt);

/// <summary>
/// <c>PUT /api/visits/{id}/complete</c> → 200. The same scalars as
/// <see cref="VisitWriteVisitResponse"/> with <c>message</c> appended LAST — visits.js:598 spreads
/// the updated row and then writes the literal, so the key lands at the end and the client's
/// stored copy of the visit picks up a stray <c>message</c> property.
///
/// Declared as its own record rather than derived from the shared one, because inheritance would
/// not pin the trailing position of <c>message</c>.
/// </summary>
public sealed record VisitWriteCompletedResponse(
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
    string Message);

/// <summary>
/// <c>PATCH /api/visits/{id}/patient-note</c> → 200. The Prisma <c>select</c> at visits.js:1400
/// exposes exactly these three keys. The request field is <c>note</c>; the response key is
/// <c>patientFeedback</c> — the two endpoints that write this column disagree on the name.
/// </summary>
public sealed record VisitWritePatientNoteResponse(
    string Id,
    string? PatientFeedback,
    DateTime? PatientFeedbackAt);

/// <summary>
/// <c>PUT /api/visits/{id}/patient-feedback</c> → 200. A hand-built literal of exactly two keys
/// (visits.js:658) — note there is NO <c>id</c> here, unlike the PATCH sibling that writes the
/// same two columns. Neither value can be null on a 200: the handler rejects empty text and always
/// stamps the timestamp.
/// </summary>
public sealed record VisitWritePatientFeedbackResponse(
    string PatientFeedback,
    DateTime PatientFeedbackAt);

/// <summary>Maps the <see cref="Visit"/> aggregate onto the write endpoints' response shapes.</summary>
public static class VisitWriteMapper
{
    public static VisitWriteCreatedResponse ToCreatedResponse(
        Visit visit,
        ClinicSummary clinic,
        SubprofileSummary? subprofile,
        ClinicPatientSummary? clinicPatient)
        => new(
            visit.Id, visit.OrganizationId, visit.ClinicId, visit.PhysicianId, visit.PatientUserId,
            visit.SubprofileId, visit.ClinicPatientId, visit.Status.ToString(), visit.AudioUrl,
            visit.RawTranscript, visit.RawNotes, visit.Subjective, visit.Objective, visit.Assessment,
            visit.Plan, visit.ChiefComplaint, [.. visit.Diagnosis], ParseJson(visit.DiagnosisCodes),
            visit.FollowUpDate, visit.FollowUpNotes, ParseJson(visit.SharedSections),
            visit.PatientFeedback, visit.PatientFeedbackAt, visit.PatientDismissedAt, visit.VisitDate,
            ParseJson(visit.SpecialtyData), visit.CompletedAt, visit.CreatedAt,
            // Prisma's @updatedAt is set on INSERT too, so a row that has never been modified must
            // still report a timestamp rather than the null MutableEntity leaves behind.
            visit.UpdatedAt ?? visit.CreatedAt, visit.DeletedAt,
            new VisitWriteCreatedClinicResponse(clinic.Name),
            subprofile is null
                ? null
                : new VisitWriteCreatedSubprofileResponse(subprofile.Name, subprofile.Relation),
            clinicPatient is null
                ? null
                : new VisitWriteCreatedClinicPatientResponse(
                    clinicPatient.Id, clinicPatient.Name, clinicPatient.Phone));

    public static VisitWriteVisitResponse ToVisitResponse(Visit visit)
        => new(
            visit.Id, visit.OrganizationId, visit.ClinicId, visit.PhysicianId, visit.PatientUserId,
            visit.SubprofileId, visit.ClinicPatientId, visit.Status.ToString(), visit.AudioUrl,
            visit.RawTranscript, visit.RawNotes, visit.Subjective, visit.Objective, visit.Assessment,
            visit.Plan, visit.ChiefComplaint, [.. visit.Diagnosis], ParseJson(visit.DiagnosisCodes),
            visit.FollowUpDate, visit.FollowUpNotes, ParseJson(visit.SharedSections),
            visit.PatientFeedback, visit.PatientFeedbackAt, visit.PatientDismissedAt, visit.VisitDate,
            ParseJson(visit.SpecialtyData), visit.CompletedAt, visit.CreatedAt,
            visit.UpdatedAt ?? visit.CreatedAt, visit.DeletedAt);

    public static VisitWriteCompletedResponse ToCompletedResponse(Visit visit, string message)
        => new(
            visit.Id, visit.OrganizationId, visit.ClinicId, visit.PhysicianId, visit.PatientUserId,
            visit.SubprofileId, visit.ClinicPatientId, visit.Status.ToString(), visit.AudioUrl,
            visit.RawTranscript, visit.RawNotes, visit.Subjective, visit.Objective, visit.Assessment,
            visit.Plan, visit.ChiefComplaint, [.. visit.Diagnosis], ParseJson(visit.DiagnosisCodes),
            visit.FollowUpDate, visit.FollowUpNotes, ParseJson(visit.SharedSections),
            visit.PatientFeedback, visit.PatientFeedbackAt, visit.PatientDismissedAt, visit.VisitDate,
            ParseJson(visit.SpecialtyData), visit.CompletedAt, visit.CreatedAt,
            visit.UpdatedAt ?? visit.CreatedAt, visit.DeletedAt, message);

    /// <summary>
    /// The opaque JSON columns are stored as text and must serialize as JSON VALUES. Returning the
    /// raw <c>string?</c> would put a quoted, escaped document on the wire where the client expects
    /// an array or an object.
    /// </summary>
    private static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
