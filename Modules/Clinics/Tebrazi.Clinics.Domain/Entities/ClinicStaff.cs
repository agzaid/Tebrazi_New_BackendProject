using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Clinics.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>ClinicStaff</c> model (table <c>clinic_staff</c>) — a person's
/// membership of a clinic, and the row the permission matrix is resolved against.
///
/// <see cref="Role"/> is a STRING, not an enum, because the Node schema stores it that way and
/// the values must keep matching <c>ClinicStaffRole</c> exactly.
/// <see cref="Permissions"/> is the raw JSON override document, merged over the role preset.
/// </summary>
public sealed class ClinicStaff : ImmutableEntity<string>
{
    private ClinicStaff() { }

    public string ClinicId { get; private set; } = null!;
    public string UserId { get; private set; } = null!;
    public string Role { get; private set; } = SharedKernel.Authorization.ClinicStaffRole.Receptionist;

    /// <summary>Per-staff overrides as a JSON object, or null to use the role preset unchanged.</summary>
    public string? Permissions { get; private set; }

    public bool IsActive { get; private set; } = true;

    public Clinic Clinic { get; private set; } = null!;

    public static ClinicStaff Create(string clinicId, string userId, string role, string? permissions = null)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            ClinicId = clinicId,
            UserId = userId,
            Role = role,
            Permissions = permissions,
            IsActive = true
        };

    public void ChangeRole(string role, string? permissions)
    {
        Role = role;
        Permissions = permissions;
    }

    /// <summary>Soft removal, so the membership can be restored by re-joining.</summary>
    public void Deactivate() => IsActive = false;

    public void Reactivate(string role, string? permissions)
    {
        IsActive = true;
        Role = role;
        Permissions = permissions;
    }
}
