using Tebrazi.SharedKernel.Base;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>User</c> model (table <c>users</c>).
///
/// Two things carry over from the Node schema and must not be "tidied":
/// <list type="bullet">
///   <item>Email is OPTIONAL — Egyptian patients are onboarded phone-first.</item>
///   <item>Uniqueness is on (Email, UserType), not on Email alone, so one person can hold both
///         a physician and a patient account under the same address.</item>
/// </list>
/// </summary>
public sealed class User : MutableEntity<string>
{
    private User() { }

    public string? Email { get; private set; }
    public string PasswordHash { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string? Phone { get; private set; }
    public string? ProfilePictureUrl { get; private set; }
    public UserRole Role { get; private set; } = UserRole.USER;
    public UserType UserType { get; private set; } = UserType.PATIENT;
    public bool Active { get; private set; } = true;
    public string? CurrentOrganizationId { get; private set; }
    public string SubscriptionTier { get; private set; } = "FREE";

    public ICollection<Session> Sessions { get; private set; } = [];
    public ICollection<PasswordResetToken> PasswordResetTokens { get; private set; } = [];
    public ICollection<OrganizationMember> Memberships { get; private set; } = [];
    public PhysicianProfile? PhysicianProfile { get; private set; }

    public static User Create(
        string displayName,
        string passwordHash,
        UserType userType,
        string? email = null,
        string? phone = null,
        UserRole role = UserRole.USER,
        string? currentOrganizationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        return new User
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHash,
            UserType = userType,
            Role = role,
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            CurrentOrganizationId = currentOrganizationId,
            Active = true
        };
    }

    public void ChangePassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void SetProfilePicture(string? url) => ProfilePictureUrl = url;

    public void SetCurrentOrganization(string? organizationId) => CurrentOrganizationId = organizationId;

    public void Deactivate() => Active = false;

    public void Activate() => Active = true;

    public void Rename(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
    }

    public void SetPhone(string? phone)
        => Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
}
