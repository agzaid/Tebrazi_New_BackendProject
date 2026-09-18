using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Application.ApiModels.Responses;
using Tebrazi.Patients.Application.Services;
using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Patients.Application.UseCases;

// ── POST /api/patients/allergies ─────────────────────────────────────────────

public sealed record AddAllergyCommand(
    string UserId, string? SubprofileId, string? Allergen, string? Severity, string? Reaction)
    : IRequest<AllergyResponse>;

public sealed class AddAllergyHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IHealthRecordStore records) : IRequestHandler<AddAllergyCommand, AllergyResponse>
{
    public async Task<AllergyResponse> Handle(
        AddAllergyCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Allergen))
            throw new ValidationException("Allergen is required");

        var profile = await resolver.GetOrCreateAsync(request.UserId, cancellationToken);
        await resolver.EnsureSubprofileOwnedAsync(request.SubprofileId, profile.Id, cancellationToken);

        var allergy = Allergy.Create(
            profile.Id, request.SubprofileId, request.Allergen, request.Severity, request.Reaction);

        records.Add(allergy);
        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(allergy);
    }
}

// ── DELETE /api/patients/allergies/{id} ──────────────────────────────────────

public sealed record RemoveAllergyCommand(string AllergyId, string UserId) : IRequest<MessageResponse>;

public sealed class RemoveAllergyHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IHealthRecordStore records,
    IFamilySubprofileStore subprofiles) : IRequestHandler<RemoveAllergyCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        RemoveAllergyCommand request, CancellationToken cancellationToken = default)
    {
        var profile = await resolver.FindAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException("Allergy not found");

        var allergy = await records.GetAllergyAsync(request.AllergyId, cancellationToken);

        if (allergy is null || allergy.IsDeleted ||
            !await HealthRecordGuard.OwnedByAsync(allergy, profile.Id, subprofiles, cancellationToken))
        {
            throw new NotFoundException("Allergy not found");
        }

        allergy.SoftDelete(DateTime.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Allergy removed");
    }
}

// ── POST /api/patients/conditions ────────────────────────────────────────────

public sealed record AddConditionCommand(
    string UserId, string? SubprofileId, string? Condition, DateTime? DiagnosedDate, string? Notes)
    : IRequest<ConditionResponse>;

public sealed class AddConditionHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IHealthRecordStore records) : IRequestHandler<AddConditionCommand, ConditionResponse>
{
    public async Task<ConditionResponse> Handle(
        AddConditionCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Condition))
            throw new ValidationException("Condition is required");

        var profile = await resolver.GetOrCreateAsync(request.UserId, cancellationToken);
        await resolver.EnsureSubprofileOwnedAsync(request.SubprofileId, profile.Id, cancellationToken);

        var condition = ChronicCondition.Create(
            profile.Id, request.SubprofileId, request.Condition, request.DiagnosedDate, request.Notes);

        records.Add(condition);
        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(condition);
    }
}

// ── DELETE /api/patients/conditions/{id} ─────────────────────────────────────

public sealed record RemoveConditionCommand(string ConditionId, string UserId) : IRequest<MessageResponse>;

public sealed class RemoveConditionHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IHealthRecordStore records,
    IFamilySubprofileStore subprofiles) : IRequestHandler<RemoveConditionCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        RemoveConditionCommand request, CancellationToken cancellationToken = default)
    {
        var profile = await resolver.FindAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException("Condition not found");

        var condition = await records.GetConditionAsync(request.ConditionId, cancellationToken);

        if (condition is null || condition.IsDeleted ||
            !await HealthRecordGuard.OwnedByAsync(condition, profile.Id, subprofiles, cancellationToken))
        {
            throw new NotFoundException("Condition not found");
        }

        condition.SoftDelete(DateTime.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Condition removed");
    }
}

// ── POST /api/patients/medications ───────────────────────────────────────────

public sealed record AddMedicationCommand(
    string UserId, string? SubprofileId, string? DrugName, string? Dosage, string? Frequency,
    string? PrescribedBy, DateTime? StartDate, DateTime? EndDate) : IRequest<MedicationResponse>;

public sealed class AddMedicationHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IHealthRecordStore records) : IRequestHandler<AddMedicationCommand, MedicationResponse>
{
    public async Task<MedicationResponse> Handle(
        AddMedicationCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DrugName))
            throw new ValidationException("Drug name is required");

        var profile = await resolver.GetOrCreateAsync(request.UserId, cancellationToken);
        await resolver.EnsureSubprofileOwnedAsync(request.SubprofileId, profile.Id, cancellationToken);

        var medication = CurrentMedication.Create(
            profile.Id, request.SubprofileId, request.DrugName, request.Dosage, request.Frequency,
            request.PrescribedBy, request.StartDate, request.EndDate);

        records.Add(medication);
        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(medication);
    }
}

// ── DELETE /api/patients/medications/{id} ────────────────────────────────────

public sealed record RemoveMedicationCommand(string MedicationId, string UserId) : IRequest<MessageResponse>;

public sealed class RemoveMedicationHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IHealthRecordStore records,
    IFamilySubprofileStore subprofiles) : IRequestHandler<RemoveMedicationCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        RemoveMedicationCommand request, CancellationToken cancellationToken = default)
    {
        var profile = await resolver.FindAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException("Medication not found");

        var medication = await records.GetMedicationAsync(request.MedicationId, cancellationToken);

        if (medication is null || medication.IsDeleted ||
            !await HealthRecordGuard.OwnedByAsync(medication, profile.Id, subprofiles, cancellationToken))
        {
            throw new NotFoundException("Medication not found");
        }

        medication.SoftDelete(DateTime.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Medication removed");
    }
}

/// <summary>
/// Ownership check shared by the three delete handlers.
///
/// A record belongs to the caller either directly, or through a dependant the caller owns.
/// Checking only the direct column would let anyone delete records attached to any subprofile.
/// </summary>
internal static class HealthRecordGuard
{
    public static async Task<bool> OwnedByAsync(
        HealthRecord record,
        string patientProfileId,
        IFamilySubprofileStore subprofiles,
        CancellationToken ct)
    {
        if (record.PatientProfileId == patientProfileId) return true;

        return record.SubprofileId is not null
            && await subprofiles.BelongsToAsync(record.SubprofileId, patientProfileId, ct);
    }
}
