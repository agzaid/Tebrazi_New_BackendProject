using Tebrazi.SharedKernel.Base;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Patients.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>PatientProfile</c> model (table <c>patient_profiles</c>) — the root of
/// a patient's clinical record.
///
/// It lives in the Patients module, not Identity. <see cref="UserId"/> is a cross-module foreign
/// key held as a plain string; the user is resolved through <c>IIdentityDirectory</c>.
///
/// Registration does NOT create one — the Node routes create it lazily on first read or write,
/// so every handler here must be prepared to find none.
/// </summary>
public sealed class PatientProfile : MutableEntity<string>
{
    private PatientProfile() { }

    public string UserId { get; private set; } = null!;
    public DateTime? DateOfBirth { get; private set; }
    public Gender? Gender { get; private set; }
    public string? Nationality { get; private set; }
    public string? BloodType { get; private set; }
    public string? EmergencyContact { get; private set; }
    public string? EmergencyPhone { get; private set; }
    public string? Address { get; private set; }
    public string? WhatsappNumber { get; private set; }

    public ICollection<FamilySubprofile> Subprofiles { get; private set; } = [];
    public ICollection<Allergy> Allergies { get; private set; } = [];
    public ICollection<ChronicCondition> Conditions { get; private set; } = [];
    public ICollection<CurrentMedication> Medications { get; private set; } = [];

    public static PatientProfile Create(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new PatientProfile { Id = Guid.NewGuid().ToString(), UserId = userId };
    }

    /// <summary>Null means "not supplied" and leaves the field alone, matching the Node route.</summary>
    public void Update(
        DateTime? dateOfBirth,
        Gender? gender,
        string? nationality,
        string? bloodType,
        string? emergencyContact,
        string? emergencyPhone,
        string? address,
        string? whatsappNumber)
    {
        if (dateOfBirth.HasValue) DateOfBirth = dateOfBirth;
        if (gender.HasValue) Gender = gender;
        if (nationality is not null) Nationality = nationality;
        if (bloodType is not null) BloodType = bloodType;
        if (emergencyContact is not null) EmergencyContact = emergencyContact;
        if (emergencyPhone is not null) EmergencyPhone = emergencyPhone;
        if (address is not null) Address = address;
        if (whatsappNumber is not null) WhatsappNumber = whatsappNumber;
    }
}

/// <summary>Relationship of a family subprofile to the account holder. Ported from the Prisma enum.</summary>
public enum SubprofileRelation { SELF, SPOUSE, CHILD, PARENT, SIBLING, OTHER }

/// <summary>
/// Port of the Prisma <c>FamilySubprofile</c> model (table <c>family_subprofiles</c>) — a
/// dependant managed under one account, such as a child or an elderly parent.
///
/// A subprofile has NO user account of its own. That is what makes the nullable owner columns on
/// allergies, conditions and medications necessary: each row belongs to either the account holder
/// or one of these.
/// </summary>
public sealed class FamilySubprofile : MutableEntity<string>
{
    private FamilySubprofile() { }

    public string PatientProfileId { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public DateTime? DateOfBirth { get; private set; }
    public Gender? Gender { get; private set; }
    public SubprofileRelation Relation { get; private set; }
    public string? BloodType { get; private set; }
    public bool IsActive { get; private set; } = true;

    public PatientProfile PatientProfile { get; private set; } = null!;

    public static FamilySubprofile Create(
        string patientProfileId,
        string name,
        SubprofileRelation relation,
        DateTime? dateOfBirth = null,
        Gender? gender = null,
        string? bloodType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new FamilySubprofile
        {
            Id = Guid.NewGuid().ToString(),
            PatientProfileId = patientProfileId,
            Name = name.Trim(),
            Relation = relation,
            DateOfBirth = dateOfBirth,
            Gender = gender,
            BloodType = bloodType
        };
    }

    public void Update(
        string? name,
        DateTime? dateOfBirth,
        Gender? gender,
        SubprofileRelation? relation,
        string? bloodType)
    {
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (dateOfBirth.HasValue) DateOfBirth = dateOfBirth;
        if (gender.HasValue) Gender = gender;
        if (relation.HasValue) Relation = relation.Value;
        if (bloodType is not null) BloodType = bloodType;
    }

    /// <summary>Soft removal — the dependant's visits and prescriptions still reference them.</summary>
    public void Deactivate() => IsActive = false;
}
