using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Patients.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>ExternalVisit</c> model (table <c>external_visits</c>) — a consultation
/// the patient records themselves, with a doctor who is not on Tebrazi. Every field about the
/// clinician is free text by design.
///
/// Keyed by <c>patientUserId</c>, NOT by patient profile id, matching the Node schema: a patient
/// can log external care before a profile row exists.
/// </summary>
public sealed class ExternalVisit : MutableEntity<string>
{
    private ExternalVisit() { }

    public string PatientUserId { get; private set; } = null!;
    public string? SubprofileId { get; private set; }

    public string DoctorName { get; private set; } = null!;
    public string? ClinicName { get; private set; }
    public string? Specialty { get; private set; }

    public DateTime VisitDate { get; private set; }
    public string? ChiefComplaint { get; private set; }
    public string? Diagnosis { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>Opaque JSON array: <c>[{ drugName, dosage, frequency, duration }]</c>.</summary>
    public string? Medications { get; private set; }

    public DateTime? FollowUpDate { get; private set; }
    public string? FollowUpNotes { get; private set; }

    public static ExternalVisit Create(
        string patientUserId,
        string? subprofileId,
        string doctorName,
        DateTime? visitDate,
        string? clinicName = null,
        string? specialty = null,
        string? chiefComplaint = null,
        string? diagnosis = null,
        string? notes = null,
        string? medications = null,
        DateTime? followUpDate = null,
        string? followUpNotes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(doctorName);

        return new ExternalVisit
        {
            Id = Guid.NewGuid().ToString(),
            PatientUserId = patientUserId,
            SubprofileId = subprofileId,
            DoctorName = doctorName.Trim(),
            ClinicName = clinicName,
            Specialty = specialty,
            VisitDate = visitDate ?? DateTime.UtcNow,
            ChiefComplaint = chiefComplaint,
            Diagnosis = diagnosis,
            Notes = notes,
            Medications = medications,
            FollowUpDate = followUpDate,
            FollowUpNotes = followUpNotes
        };
    }

    public void Update(
        string? doctorName, string? clinicName, string? specialty, DateTime? visitDate,
        string? chiefComplaint, string? diagnosis, string? notes, string? medications,
        DateTime? followUpDate, string? followUpNotes)
    {
        if (!string.IsNullOrWhiteSpace(doctorName)) DoctorName = doctorName.Trim();
        if (clinicName is not null) ClinicName = clinicName;
        if (specialty is not null) Specialty = specialty;
        if (visitDate.HasValue) VisitDate = visitDate.Value;
        if (chiefComplaint is not null) ChiefComplaint = chiefComplaint;
        if (diagnosis is not null) Diagnosis = diagnosis;
        if (notes is not null) Notes = notes;
        if (medications is not null) Medications = medications;
        if (followUpDate.HasValue) FollowUpDate = followUpDate;
        if (followUpNotes is not null) FollowUpNotes = followUpNotes;
    }
}

/// <summary>
/// Port of the Prisma <c>ExternalDoctor</c> model (table <c>external_doctors</c>) — the
/// patient's own address book of clinicians outside Tebrazi.
/// </summary>
public sealed class ExternalDoctor : MutableEntity<string>
{
    private ExternalDoctor() { }

    public string PatientUserId { get; private set; } = null!;
    public string? SubprofileId { get; private set; }

    public string Name { get; private set; } = null!;
    public string? Specialty { get; private set; }
    public string? ClinicName { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Address { get; private set; }
    public string? Notes { get; private set; }

    public static ExternalDoctor Create(
        string patientUserId,
        string? subprofileId,
        string name,
        string? specialty = null,
        string? clinicName = null,
        string? phone = null,
        string? email = null,
        string? address = null,
        string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new ExternalDoctor
        {
            Id = Guid.NewGuid().ToString(),
            PatientUserId = patientUserId,
            SubprofileId = subprofileId,
            Name = name.Trim(),
            Specialty = specialty,
            ClinicName = clinicName,
            Phone = phone,
            Email = email,
            Address = address,
            Notes = notes
        };
    }

    public void Update(
        string? name, string? specialty, string? clinicName,
        string? phone, string? email, string? address, string? notes)
    {
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (specialty is not null) Specialty = specialty;
        if (clinicName is not null) ClinicName = clinicName;
        if (phone is not null) Phone = phone;
        if (email is not null) Email = email;
        if (address is not null) Address = address;
        if (notes is not null) Notes = notes;
    }
}
