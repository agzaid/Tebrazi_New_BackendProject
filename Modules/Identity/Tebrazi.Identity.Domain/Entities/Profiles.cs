using Tebrazi.SharedKernel.Base;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>PhysicianProfile</c> model (table <c>physician_profiles</c>).
/// A user holds at most one. Its existence is what makes someone a physician for the purposes
/// of clinic authorization, which is why the clinic-access evaluator looks for it.
/// </summary>
public sealed class PhysicianProfile : MutableEntity<string>
{
    private PhysicianProfile() { }

    public string UserId { get; private set; } = null!;
    public string LicenseNumber { get; private set; } = null!;
    public string Specialty { get; private set; } = null!;
    public string? Qualifications { get; private set; }
    public string? Bio { get; private set; }
    public string? ScratchpadNotes { get; private set; }
    public int? YearsOfExperience { get; private set; }
    public bool Verified { get; private set; }
    public DateTime? VerifiedAt { get; private set; }

    public User User { get; private set; } = null!;

    public static PhysicianProfile Create(string userId, string licenseNumber, string specialty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(specialty);

        return new PhysicianProfile
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            LicenseNumber = licenseNumber.Trim(),
            Specialty = specialty.Trim()
        };
    }

    public void MarkVerified(DateTime utcNow)
    {
        Verified = true;
        VerifiedAt = utcNow;
    }

    public void UpdateDetails(
        string? qualifications,
        string? bio,
        int? yearsOfExperience,
        string? specialty = null)
    {
        Qualifications = qualifications;
        Bio = bio;
        YearsOfExperience = yearsOfExperience;
        if (!string.IsNullOrWhiteSpace(specialty)) Specialty = specialty.Trim();
    }

    public void SetScratchpad(string? notes) => ScratchpadNotes = notes;
}

