using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Application.ApiModels.Responses;
using Tebrazi.Patients.Application.Services;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Patients.Application.UseCases;

// ── GET /api/patients/profile ────────────────────────────────────────────────

public sealed record GetPatientProfileQuery(string UserId) : IRequest<PatientProfileResponse>;

/// <summary>
/// Port of <c>GET /api/patients/profile</c>. Creates the profile on first read, as the Node
/// route does, so a new patient sees an empty record rather than a 404.
/// </summary>
public sealed class GetPatientProfileHandler(
    PatientProfileResolver resolver,
    IHealthRecordStore records,
    IFamilySubprofileStore subprofiles,
    IIdentityDirectory identity) : IRequestHandler<GetPatientProfileQuery, PatientProfileResponse>
{
    public async Task<PatientProfileResponse> Handle(
        GetPatientProfileQuery request, CancellationToken cancellationToken = default)
    {
        var profile = await resolver.GetOrCreateAsync(request.UserId, cancellationToken);
        return await LoadAsync(profile.Id, profile, records, subprofiles, identity, cancellationToken);
    }

    /// <summary>
    /// Loads the profile with its four collections. Conditions and medications are filtered to
    /// ACTIVE only, matching the Node include — an inactive one is history, not current state.
    /// </summary>
    internal static async Task<PatientProfileResponse> LoadAsync(
        string patientProfileId,
        Domain.Entities.PatientProfile profile,
        IHealthRecordStore records,
        IFamilySubprofileStore subprofiles,
        IIdentityDirectory identity,
        CancellationToken ct)
    {
        var allergies = await records.ListAllergiesAsync(patientProfileId, null, ct);
        var conditions = await records.ListConditionsAsync(patientProfileId, null, ct);
        var medications = await records.ListMedicationsAsync(patientProfileId, null, ct);
        var family = await subprofiles.ListActiveAsync(patientProfileId, ct);
        var user = await identity.GetUserAsync(profile.UserId, ct);

        return PatientMapper.ToResponse(
            profile, user, allergies,
            [.. conditions.Where(c => c.IsActive)],
            [.. medications.Where(m => m.IsActive)],
            family);
    }
}

// ── PUT /api/patients/profile ────────────────────────────────────────────────

public sealed record UpdatePatientProfileCommand(
    string UserId,
    DateTime? DateOfBirth,
    string? Gender,
    string? Nationality,
    string? BloodType,
    string? EmergencyContact,
    string? EmergencyPhone,
    string? Address,
    string? WhatsappNumber) : IRequest<PatientProfileResponse>;

/// <summary>Port of <c>PUT /api/patients/profile</c> — an upsert, as in the Node route.</summary>
public sealed class UpdatePatientProfileHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IHealthRecordStore records,
    IFamilySubprofileStore subprofiles,
    IIdentityDirectory identity) : IRequestHandler<UpdatePatientProfileCommand, PatientProfileResponse>
{
    public async Task<PatientProfileResponse> Handle(
        UpdatePatientProfileCommand request, CancellationToken cancellationToken = default)
    {
        var profile = await resolver.GetOrCreateAsync(request.UserId, cancellationToken);

        profile.Update(
            request.DateOfBirth,
            // An unrecognised gender is ignored rather than rejected — the Node route passes the
            // value straight through and Prisma would only fail on a truly invalid enum.
            Enum.TryParse<Gender>(request.Gender, ignoreCase: true, out var gender) ? gender : null,
            request.Nationality,
            request.BloodType,
            request.EmergencyContact,
            request.EmergencyPhone,
            request.Address,
            request.WhatsappNumber);

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetPatientProfileHandler.LoadAsync(
            profile.Id, profile, records, subprofiles, identity, cancellationToken);
    }
}
