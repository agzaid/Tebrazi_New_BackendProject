using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tebrazi.Common.Api.Controllers;
using Tebrazi.Patients.Application.ApiModels.Responses;
using Tebrazi.Patients.Application.UseCases;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Api.Controllers;

/// <summary>
/// <c>/api/patients</c> — the port of <c>server/src/routes/patients.js</c>.
///
/// Route order mirrors the Node file: literal segments come before <c>{userId}</c>, so
/// <c>/patients/family</c> is not bound as a user id.
/// </summary>
[Route("api/patients")]
[Authorize]
public sealed class PatientsController(IMediator mediator, ICurrentUser currentUser)
    : BaseApiController(mediator)
{
    private string UserId => currentUser.UserId ?? throw new UnauthorizedException("Invalid token");

    // ── Profile ──────────────────────────────────────────────────────────────

    /// <summary>The caller's patient profile with allergies, active conditions, active medications and family.</summary>
    [HttpGet("profile")]
    [ProducesResponseType<PatientProfileResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile()
        => Payload(await Send(new GetPatientProfileQuery(UserId)));

    /// <summary>Updates the profile, creating it if this is the first write.</summary>
    [HttpPut("profile")]
    [ProducesResponseType<PatientProfileResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
        => Payload(await Send(new UpdatePatientProfileCommand(
            UserId, request.DateOfBirth, request.Gender, request.Nationality, request.BloodType,
            request.EmergencyContact, request.EmergencyPhone, request.Address, request.WhatsappNumber)));

    // ── Aggregates ───────────────────────────────────────────────────────────
    //
    // All three take no parameters: the Node routes read only req.user.id, and the handlers
    // resolve the caller from ICurrentUser themselves. Their paths are literals and are declared
    // above {userId}/family-members, which stays last.

    /// <summary>
    /// Everything the patient home screen renders in a single call: profile, connected doctors,
    /// medications from both sources, upcoming appointments, recent visits, family members,
    /// reminders, four counters and any outstanding follow-ups.
    ///
    /// <para>A patient with no profile row gets <b>200</b> with an empty shape, not a 404, and
    /// that body omits <c>upcomingFollowUps</c> entirely.</para>
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType<PatientDashboardResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard()
        => Payload(await Send(new GetPatientDashboardQuery()));

    /// <summary>
    /// A print-ready Markdown summary of the caller's record — for sharing with a new doctor, or
    /// as context for the AI chat.
    /// </summary>
    [HttpGet("health-summary")]
    [ProducesResponseType<PatientHealthSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> HealthSummary()
        => Payload(await Send(new GetPatientHealthSummaryQuery()));

    /// <summary>
    /// The caller's self-reported medications diffed against everything prescribed to them, with
    /// drugs appearing on both sides flagged as duplicates.
    /// </summary>
    [HttpGet("medications/reconciled")]
    [ProducesResponseType<ReconciledMedicationsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReconciledMedications()
        => Payload(await Send(new GetReconciledMedicationsQuery()));

    // ── Family ───────────────────────────────────────────────────────────────

    /// <summary>The caller's family subprofiles.</summary>
    [HttpGet("family")]
    [ProducesResponseType<IReadOnlyList<SubprofileResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Family()
        => Payload(await Send(new ListFamilyQuery(UserId)));

    /// <summary>Adds a dependant.</summary>
    [HttpPost("family")]
    [ProducesResponseType<SubprofileResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddFamily([FromBody] FamilyMemberRequest request)
        => CreatedPayload(await Send(new AddFamilyMemberCommand(
            UserId, request.Name, request.Relation, request.DateOfBirth,
            request.Gender, request.BloodType)));

    /// <summary>Updates a dependant.</summary>
    [HttpPut("family/{id}")]
    [ProducesResponseType<SubprofileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateFamily(string id, [FromBody] FamilyMemberRequest request)
        => Payload(await Send(new UpdateFamilyMemberCommand(
            id, UserId, request.Name, request.Relation, request.DateOfBirth,
            request.Gender, request.BloodType)));

    /// <summary>Deactivates a dependant. Their clinical history is retained.</summary>
    [HttpDelete("family/{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveFamily(string id)
        => Payload(await Send(new RemoveFamilyMemberCommand(id, UserId)));

    // ── Health records ───────────────────────────────────────────────────────

    /// <summary>Records an allergy for the caller or one of their dependants.</summary>
    [HttpPost("allergies")]
    [ProducesResponseType<AllergyResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddAllergy([FromBody] AllergyRequest request)
        => CreatedPayload(await Send(new AddAllergyCommand(
            UserId, request.SubprofileId, request.Allergen, request.Severity, request.Reaction)));

    /// <summary>Soft-deletes an allergy.</summary>
    [HttpDelete("allergies/{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveAllergy(string id)
        => Payload(await Send(new RemoveAllergyCommand(id, UserId)));

    /// <summary>Records a chronic condition.</summary>
    [HttpPost("conditions")]
    [ProducesResponseType<ConditionResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddCondition([FromBody] ConditionRequest request)
        => CreatedPayload(await Send(new AddConditionCommand(
            UserId, request.SubprofileId, request.Condition, request.DiagnosedDate, request.Notes)));

    /// <summary>Soft-deletes a condition.</summary>
    [HttpDelete("conditions/{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveCondition(string id)
        => Payload(await Send(new RemoveConditionCommand(id, UserId)));

    /// <summary>Records a medication the patient reports taking.</summary>
    [HttpPost("medications")]
    [ProducesResponseType<MedicationResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddMedication([FromBody] MedicationRequest request)
        => CreatedPayload(await Send(new AddMedicationCommand(
            UserId, request.SubprofileId, request.DrugName, request.Dosage, request.Frequency,
            request.PrescribedBy, request.StartDate, request.EndDate)));

    /// <summary>Soft-deletes a medication.</summary>
    [HttpDelete("medications/{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveMedication(string id)
        => Payload(await Send(new RemoveMedicationCommand(id, UserId)));

    // ── External care ────────────────────────────────────────────────────────

    /// <summary>Consultations the patient logged with clinicians outside Tebrazi.</summary>
    [HttpGet("external-visits")]
    [ProducesResponseType<IReadOnlyList<ExternalVisitResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExternalVisits([FromQuery] string? subprofileId)
        => Payload(await Send(new ListExternalVisitsQuery(UserId, subprofileId)));

    /// <summary>Logs an external consultation.</summary>
    [HttpPost("external-visits")]
    [ProducesResponseType<ExternalVisitResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddExternalVisit([FromBody] ExternalVisitRequest request)
        => CreatedPayload(await Send(new AddExternalVisitCommand(
            UserId, request.SubprofileId, request.DoctorName, request.ClinicName, request.Specialty,
            request.VisitDate, request.ChiefComplaint, request.Diagnosis, request.Notes,
            request.Medications, request.FollowUpDate, request.FollowUpNotes)));

    /// <summary>Updates an external consultation.</summary>
    [HttpPut("external-visits/{id}")]
    [ProducesResponseType<ExternalVisitResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateExternalVisit(string id, [FromBody] ExternalVisitRequest request)
        => Payload(await Send(new UpdateExternalVisitCommand(
            id, UserId, request.DoctorName, request.ClinicName, request.Specialty,
            request.VisitDate, request.ChiefComplaint, request.Diagnosis, request.Notes,
            request.Medications, request.FollowUpDate, request.FollowUpNotes)));

    /// <summary>Deletes an external consultation.</summary>
    [HttpDelete("external-visits/{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveExternalVisit(string id)
        => Payload(await Send(new RemoveExternalVisitCommand(id, UserId)));

    /// <summary>The patient's address book of clinicians outside Tebrazi.</summary>
    [HttpGet("external-doctors")]
    [ProducesResponseType<IReadOnlyList<ExternalDoctorResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExternalDoctors([FromQuery] string? subprofileId)
        => Payload(await Send(new ListExternalDoctorsQuery(UserId, subprofileId)));

    /// <summary>Adds an external clinician.</summary>
    [HttpPost("external-doctors")]
    [ProducesResponseType<ExternalDoctorResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddExternalDoctor([FromBody] ExternalDoctorRequest request)
        => CreatedPayload(await Send(new AddExternalDoctorCommand(
            UserId, request.SubprofileId, request.Name, request.Specialty, request.ClinicName,
            request.Phone, request.Email, request.Address, request.Notes)));

    /// <summary>Updates an external clinician.</summary>
    [HttpPut("external-doctors/{id}")]
    [ProducesResponseType<ExternalDoctorResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateExternalDoctor(string id, [FromBody] ExternalDoctorRequest request)
        => Payload(await Send(new UpdateExternalDoctorCommand(
            id, UserId, request.Name, request.Specialty, request.ClinicName,
            request.Phone, request.Email, request.Address, request.Notes)));

    /// <summary>Deletes an external clinician.</summary>
    [HttpDelete("external-doctors/{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveExternalDoctor(string id)
        => Payload(await Send(new RemoveExternalDoctorCommand(id, UserId)));

    // ── Another patient's family, for a clinician viewing their file ─────────

    /// <summary>
    /// A named patient's family members. Declared LAST so its <c>{userId}</c> segment cannot
    /// shadow the literal routes above.
    /// </summary>
    [HttpGet("{userId}/family-members")]
    [ProducesResponseType<IReadOnlyList<SubprofileResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> FamilyMembers(string userId)
        => Payload(await Send(new ListFamilyMembersQuery(userId, UserId)));
}

public sealed record UpdateProfileRequest(
    DateTime? DateOfBirth, string? Gender, string? Nationality, string? BloodType,
    string? EmergencyContact, string? EmergencyPhone, string? Address, string? WhatsappNumber);

public sealed record FamilyMemberRequest(
    string? Name, string? Relation, DateTime? DateOfBirth, string? Gender, string? BloodType);

public sealed record AllergyRequest(
    string? SubprofileId, string? Allergen, string? Severity, string? Reaction);

public sealed record ConditionRequest(
    string? SubprofileId, string? Condition, DateTime? DiagnosedDate, string? Notes);

public sealed record MedicationRequest(
    string? SubprofileId, string? DrugName, string? Dosage, string? Frequency,
    string? PrescribedBy, DateTime? StartDate, DateTime? EndDate);

/// <summary>
/// <c>Medications</c> is a raw <see cref="JsonElement"/>: the column is opaque JSON and the
/// server stores whatever shape the client sends without interpreting it.
/// </summary>
public sealed record ExternalVisitRequest(
    string? SubprofileId, string? DoctorName, string? ClinicName, string? Specialty,
    DateTime? VisitDate, string? ChiefComplaint, string? Diagnosis, string? Notes,
    JsonElement? Medications, DateTime? FollowUpDate, string? FollowUpNotes);

public sealed record ExternalDoctorRequest(
    string? SubprofileId, string? Name, string? Specialty, string? ClinicName,
    string? Phone, string? Email, string? Address, string? Notes);
