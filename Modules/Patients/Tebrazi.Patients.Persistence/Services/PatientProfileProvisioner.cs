using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.SharedKernel.Exceptions;

namespace Tebrazi.Patients.Persistence.Services;

/// <summary>
/// Patients' implementation of <see cref="IPatientProfileProvisioner"/> — the only write another
/// module may make to <c>patient_profiles</c> and <c>family_subprofiles</c>, and only for
/// <c>POST /api/connections/create-patient</c> (connections.js:680-698).
/// </summary>
public sealed class PatientProfileProvisioner(PatientsDbContext context) : IPatientProfileProvisioner
{
    public async Task<string> CreateProfileWithSelfSubprofileAsync(
        string patientUserId,
        string? address,
        string? whatsappNumber,
        string selfName,
        DateTime? dateOfBirth,
        string? gender,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patientUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(selfName);

        // Node writes `gender || null` straight into a Prisma enum column, so an unrecognised
        // string is a PrismaClientValidationError and the route answers its own 500. Throwing
        // here — rather than dropping the value to null — is what keeps that behaviour: the
        // Connections handler's catch-all guard turns it into
        // `500 {"error":"Failed to create patient"}`.
        Gender? parsedGender = null;
        if (!string.IsNullOrEmpty(gender))
        {
            if (!Enum.TryParse<Gender>(gender, ignoreCase: false, out var value))
            {
                throw new BusinessException(
                    "Invalid gender", $"'{gender}' is not a valid Gender value.", 500);
            }

            parsedGender = value;
        }

        var profile = PatientProfile.Create(patientUserId);

        // Only these two columns. dateOfBirth and gender go on the SELF subprofile, not here
        // (connections.js:681-687 vs :690-698).
        profile.Update(
            dateOfBirth: null,
            gender: null,
            nationality: null,
            bloodType: null,
            emergencyContact: null,
            emergencyPhone: null,
            address: address,
            whatsappNumber: whatsappNumber);

        var self = FamilySubprofile.Create(
            profile.Id,
            selfName,
            SubprofileRelation.SELF,
            dateOfBirth,
            parsedGender);

        context.PatientProfiles.Add(profile);
        context.FamilySubprofiles.Add(self);

        // One commit for both rows. Node issues two sequential creates with no transaction; the
        // difference is invisible on success and strictly safer on failure.
        await context.SaveChangesAsync(ct);

        return profile.Id;
    }
}
