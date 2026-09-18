using System.Text.Json.Nodes;
using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Patients.Application.ApiModels.Responses;

/// <summary>
/// The patient profile, with the nested collections the Node route included: allergies, ACTIVE
/// conditions, ACTIVE medications, and active subprofiles.
/// </summary>
public sealed record PatientProfileResponse(
    string Id,
    string UserId,
    DateTime? DateOfBirth,
    string? Gender,
    string? Nationality,
    string? BloodType,
    string? EmergencyContact,
    string? EmergencyPhone,
    string? Address,
    string? WhatsappNumber,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    PatientUserResponse? User,
    IReadOnlyList<AllergyResponse> Allergies,
    IReadOnlyList<ConditionResponse> Conditions,
    IReadOnlyList<MedicationResponse> Medications,
    IReadOnlyList<SubprofileResponse> Subprofiles);

/// <summary>Only the three user fields the Node <c>select</c> exposed — no more.</summary>
public sealed record PatientUserResponse(string DisplayName, string? Email, string? Phone);

public sealed record SubprofileResponse(
    string Id,
    string PatientProfileId,
    string Name,
    DateTime? DateOfBirth,
    string? Gender,
    string Relation,
    string? BloodType,
    bool IsActive,
    DateTime CreatedAt);

public sealed record AllergyResponse(
    string Id,
    string? PatientProfileId,
    string? SubprofileId,
    string Allergen,
    string? Severity,
    string? Reaction,
    DateTime CreatedAt);

public sealed record ConditionResponse(
    string Id,
    string? PatientProfileId,
    string? SubprofileId,
    string Condition,
    DateTime? DiagnosedDate,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt);

public sealed record MedicationResponse(
    string Id,
    string? PatientProfileId,
    string? SubprofileId,
    string DrugName,
    string? Dosage,
    string? Frequency,
    string? PrescribedBy,
    DateTime? StartDate,
    DateTime? EndDate,
    bool IsActive,
    DateTime CreatedAt);

public sealed record ExternalVisitResponse(
    string Id,
    string PatientUserId,
    string? SubprofileId,
    string DoctorName,
    string? ClinicName,
    string? Specialty,
    DateTime VisitDate,
    string? ChiefComplaint,
    string? Diagnosis,
    string? Notes,
    JsonNode? Medications,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ExternalDoctorResponse(
    string Id,
    string PatientUserId,
    string? SubprofileId,
    string Name,
    string? Specialty,
    string? ClinicName,
    string? Phone,
    string? Email,
    string? Address,
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>Generic acknowledgement, matching the Node <c>{ message }</c> bodies.</summary>
public sealed record MessageResponse(string Message);

public static class PatientMapper
{
    public static PatientProfileResponse ToResponse(
        PatientProfile profile,
        UserSummary? user,
        IReadOnlyList<Allergy> allergies,
        IReadOnlyList<ChronicCondition> conditions,
        IReadOnlyList<CurrentMedication> medications,
        IReadOnlyList<FamilySubprofile> subprofiles)
        => new(
            profile.Id, profile.UserId, profile.DateOfBirth, profile.Gender?.ToString(),
            profile.Nationality, profile.BloodType, profile.EmergencyContact, profile.EmergencyPhone,
            profile.Address, profile.WhatsappNumber, profile.CreatedAt, profile.UpdatedAt,
            user is null ? null : new PatientUserResponse(user.DisplayName, user.Email, user.Phone),
            [.. allergies.Select(ToResponse)],
            [.. conditions.Select(ToResponse)],
            [.. medications.Select(ToResponse)],
            [.. subprofiles.Select(ToResponse)]);

    public static SubprofileResponse ToResponse(FamilySubprofile s)
        => new(s.Id, s.PatientProfileId, s.Name, s.DateOfBirth, s.Gender?.ToString(),
               s.Relation.ToString(), s.BloodType, s.IsActive, s.CreatedAt);

    public static AllergyResponse ToResponse(Allergy a)
        => new(a.Id, a.PatientProfileId, a.SubprofileId, a.Allergen, a.Severity, a.Reaction, a.CreatedAt);

    public static ConditionResponse ToResponse(ChronicCondition c)
        => new(c.Id, c.PatientProfileId, c.SubprofileId, c.Condition, c.DiagnosedDate,
               c.Notes, c.IsActive, c.CreatedAt);

    public static MedicationResponse ToResponse(CurrentMedication m)
        => new(m.Id, m.PatientProfileId, m.SubprofileId, m.DrugName, m.Dosage, m.Frequency,
               m.PrescribedBy, m.StartDate, m.EndDate, m.IsActive, m.CreatedAt);

    public static ExternalVisitResponse ToResponse(ExternalVisit v)
        => new(v.Id, v.PatientUserId, v.SubprofileId, v.DoctorName, v.ClinicName, v.Specialty,
               v.VisitDate, v.ChiefComplaint, v.Diagnosis, v.Notes, ParseJson(v.Medications),
               v.FollowUpDate, v.FollowUpNotes, v.CreatedAt, v.UpdatedAt);

    public static ExternalDoctorResponse ToResponse(ExternalDoctor d)
        => new(d.Id, d.PatientUserId, d.SubprofileId, d.Name, d.Specialty, d.ClinicName,
               d.Phone, d.Email, d.Address, d.Notes, d.CreatedAt, d.UpdatedAt);

    /// <summary>
    /// Stored JSON is returned as a node so it serializes as an array, not a quoted string —
    /// the medication list on an external visit was a Postgres <c>Json?</c> column.
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
