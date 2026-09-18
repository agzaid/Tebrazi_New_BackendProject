using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Clinics.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Clinic</c> model (table <c>clinics</c>).
///
/// <see cref="PhysicianId"/> references <c>physician_profiles.id</c>, which the Identity module
/// owns. It is a plain string, not an EF relationship: the two modules keep separate models, and
/// mapping another module's table into this one is exactly the leak this architecture avoids.
/// Resolve physicians through <c>IIdentityDirectory</c>.
/// </summary>
public sealed class Clinic : MutableEntity<string>
{
    private Clinic() { }

    public string OrganizationId { get; private set; } = null!;
    public string PhysicianId { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string? Address { get; private set; }
    public string? City { get; private set; }
    public string? Country { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }

    /// <summary>Opaque JSON, as in the Postgres <c>Json?</c> column.</summary>
    public string? WorkingHours { get; private set; }

    public string? Specialty { get; private set; }
    public string? Logo { get; private set; }
    public bool IsActive { get; private set; } = true;

    /// <summary>When false, only the physician or staff may book — patients cannot self-book.</summary>
    public bool AllowPatientBooking { get; private set; } = true;

    public string? TwilioPhoneNumber { get; private set; }
    public string? TwilioWhatsAppNumber { get; private set; }

    /// <summary>Consultation fees in EGP. Prisma defaults: 300 initial, 200 follow-up.</summary>
    public double? ConsultationFee { get; private set; } = 300;
    public double? FollowUpFee { get; private set; } = 200;

    public ICollection<ClinicStaff> StaffMembers { get; private set; } = [];
    public ICollection<StaffInvitation> StaffInvitations { get; private set; } = [];
    public ICollection<StaffPin> StaffPins { get; private set; } = [];

    public static Clinic Create(
        string organizationId,
        string physicianId,
        string name,
        string? specialty = null,
        string? address = null,
        string? city = null,
        string? country = null,
        string? phone = null,
        string? email = null,
        string? workingHours = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Clinic
        {
            Id = Guid.NewGuid().ToString(),
            OrganizationId = organizationId,
            PhysicianId = physicianId,
            Name = name.Trim(),
            Specialty = specialty,
            Address = address,
            City = city,
            Country = country,
            Phone = phone,
            Email = email,
            WorkingHours = workingHours,
            IsActive = true
        };
    }

    public void Update(
        string? name,
        string? address,
        string? city,
        string? country,
        string? phone,
        string? email,
        string? specialty,
        string? workingHours,
        bool? allowPatientBooking,
        double? consultationFee,
        double? followUpFee)
    {
        // Null means "not supplied" — the Node route only overwrites fields present in the body,
        // so a partial update must not blank the rest.
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (address is not null) Address = address;
        if (city is not null) City = city;
        if (country is not null) Country = country;
        if (phone is not null) Phone = phone;
        if (email is not null) Email = email;
        if (specialty is not null) Specialty = specialty;
        if (workingHours is not null) WorkingHours = workingHours;
        if (allowPatientBooking.HasValue) AllowPatientBooking = allowPatientBooking.Value;
        if (consultationFee.HasValue) ConsultationFee = consultationFee.Value;
        if (followUpFee.HasValue) FollowUpFee = followUpFee.Value;
    }

    public void SetLogo(string? logo) => Logo = logo;

    /// <summary>
    /// Replaces the working-hours JSON outright, null included.
    ///
    /// <see cref="Update"/> cannot do this: there, null means "not supplied" and leaves the
    /// stored value alone. <c>POST /api/appointments/slots/sync-from-hours</c> needs to be able
    /// to clear the value, so it goes through here instead.
    /// </summary>
    public void SetWorkingHours(string? workingHours) => WorkingHours = workingHours;

    public void SetTwilioNumbers(string? sms, string? whatsApp)
    {
        TwilioPhoneNumber = sms;
        TwilioWhatsAppNumber = whatsApp;
    }

    /// <summary>
    /// Soft delete. The Node route deactivates rather than removing, so visits, appointments and
    /// payments that reference the clinic keep resolving.
    /// </summary>
    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}
