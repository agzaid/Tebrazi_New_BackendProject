using Tebrazi.SharedKernel.Base;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Clinics.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>ClinicPatient</c> model (table <c>clinic_patients</c>) — the
/// physician-owned patient chart, entered by the clinic rather than by the patient.
///
/// It exists because most people walking into a clinic have no Tebrazi account. A chart can be
/// created from a name and a phone number alone, and later linked to a real account by setting
/// <see cref="LinkedUserId"/>. Until then it is the ONLY identity a visit or appointment has,
/// which is why <c>visits.clinic_patient_id</c> and <c>appointments.clinic_patient_id</c> exist
/// alongside their <c>patient_user_id</c> columns.
///
/// It lives in Clinics rather than Patients on purpose: it is clinic-side data keyed by the
/// owning physician, and it is not the patient's own record. <c>PatientProfile</c>, in Patients,
/// is keyed by <c>user_id</c> and belongs to the account holder.
/// </summary>
public sealed class ClinicPatient : MutableEntity<string>
{
    private ClinicPatient() { }

    /// <summary>The physician who owns the chart. This is a USER id, not a physician-profile id.</summary>
    public string PhysicianUserId { get; private set; } = null!;

    /// <summary>Nullable in the Node schema: a chart can exist before it is filed under a clinic.</summary>
    public string? ClinicId { get; private set; }

    // ── Demographics ─────────────────────────────────────────────────────────
    public string Name { get; private set; } = null!;
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public DateTime? DateOfBirth { get; private set; }
    public Gender? Gender { get; private set; }
    public string? NationalId { get; private set; }
    public string? BloodType { get; private set; }

    // ── Clinical context ─────────────────────────────────────────────────────
    public string? Notes { get; private set; }

    /// <summary>Free-text allergies. A primitive collection, stored as one JSON array column.</summary>
    public List<string> Allergies { get; private set; } = [];

    /// <summary>Free-text chronic conditions, stored the same way.</summary>
    public List<string> ChronicConditions { get; private set; } = [];

    // ── Link to a real account ───────────────────────────────────────────────

    /// <summary>Set once the chart is matched to a Tebrazi user. Null while unlinked.</summary>
    public string? LinkedUserId { get; private set; }

    public DateTime? LinkedAt { get; private set; }

    // ── Status ───────────────────────────────────────────────────────────────
    public bool IsActive { get; private set; } = true;

    /// <summary>Denormalised for the patient list, which sorts by it. Written when a visit closes.</summary>
    public DateTime? LastVisitDate { get; private set; }

    public static ClinicPatient Create(
        string physicianUserId,
        string name,
        string? clinicId = null,
        string? phone = null,
        string? email = null,
        DateTime? dateOfBirth = null,
        Gender? gender = null,
        string? nationalId = null,
        string? bloodType = null,
        string? notes = null,
        IEnumerable<string>? allergies = null,
        IEnumerable<string>? chronicConditions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicianUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new ClinicPatient
        {
            Id = Guid.NewGuid().ToString(),
            PhysicianUserId = physicianUserId,
            ClinicId = clinicId,
            Name = name.Trim(),
            Phone = phone,
            Email = email,
            DateOfBirth = dateOfBirth,
            Gender = gender,
            NationalId = nationalId,
            BloodType = bloodType,
            Notes = notes,
            Allergies = allergies is null ? [] : [.. allergies],
            ChronicConditions = chronicConditions is null ? [] : [.. chronicConditions]
        };
    }

    /// <summary>Partial update: null leaves a field alone.</summary>
    public void Update(
        string? name = null,
        string? phone = null,
        string? email = null,
        DateTime? dateOfBirth = null,
        Gender? gender = null,
        string? nationalId = null,
        string? bloodType = null,
        string? notes = null,
        IEnumerable<string>? allergies = null,
        IEnumerable<string>? chronicConditions = null,
        string? clinicId = null)
    {
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (phone is not null) Phone = phone;
        if (email is not null) Email = email;
        if (dateOfBirth.HasValue) DateOfBirth = dateOfBirth;
        if (gender.HasValue) Gender = gender;
        if (nationalId is not null) NationalId = nationalId;
        if (bloodType is not null) BloodType = bloodType;
        if (notes is not null) Notes = notes;
        if (allergies is not null) Allergies = [.. allergies];
        if (chronicConditions is not null) ChronicConditions = [.. chronicConditions];
        if (clinicId is not null) ClinicId = clinicId;
    }

    /// <summary>Matches the chart to a Tebrazi account. Idempotent — re-linking keeps the first stamp.</summary>
    public void LinkTo(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        LinkedUserId = userId;
        LinkedAt ??= DateTime.UtcNow;
    }

    public void Unlink()
    {
        LinkedUserId = null;
        LinkedAt = null;
    }

    /// <summary>Moves the denormalised marker forward only — an old visit must not roll it back.</summary>
    public void RecordVisit(DateTime visitDate)
    {
        if (LastVisitDate is null || visitDate > LastVisitDate) LastVisitDate = visitDate;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
