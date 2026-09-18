using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Clinics.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>StaffPin</c> model (table <c>staff_pins</c>) — a short-lived 4-digit
/// code a physician reads out so staff can join a clinic on the spot.
///
/// Lifetime is FIVE MINUTES and generating a new PIN expires the clinic's outstanding ones, so
/// at most one is live per clinic at a time.
/// </summary>
public sealed class StaffPin : ImmutableEntity<string>
{
    /// <summary>Five minutes, matching the Node route.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private StaffPin() { }

    public string ClinicId { get; private set; } = null!;
    public string GeneratedByUserId { get; private set; } = null!;
    public string Role { get; private set; } = SharedKernel.Authorization.ClinicStaffRole.Receptionist;
    public string Pin { get; private set; } = null!;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? UsedAt { get; private set; }
    public string? UsedByUserId { get; private set; }

    public Clinic Clinic { get; private set; } = null!;

    public static StaffPin Issue(string clinicId, string generatedByUserId, string role, string pin, DateTime utcNow)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            ClinicId = clinicId,
            GeneratedByUserId = generatedByUserId,
            Role = role,
            Pin = pin,
            ExpiresAt = utcNow.Add(Lifetime)
        };

    public bool IsRedeemable(DateTime utcNow) => UsedAt is null && ExpiresAt > utcNow;

    public void Redeem(string userId, DateTime utcNow)
    {
        UsedAt = utcNow;
        UsedByUserId = userId;
    }

    /// <summary>Expires the PIN immediately by moving its expiry to now, as the Node route does.</summary>
    public void Expire(DateTime utcNow) => ExpiresAt = utcNow;
}

/// <summary>Status of a <see cref="StaffInvitation"/>, ported from the Prisma enum.</summary>
public enum StaffInvitationStatus { PENDING, ACCEPTED, EXPIRED, REVOKED }

/// <summary>
/// Port of the Prisma <c>StaffInvitation</c> model (table <c>staff_invitations</c>) — an emailed
/// invitation for someone not yet registered. Valid for seven days.
/// </summary>
public sealed class StaffInvitation : ImmutableEntity<string>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private StaffInvitation() { }

    public string ClinicId { get; private set; } = null!;
    public string InvitedByUserId { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string Role { get; private set; } = SharedKernel.Authorization.ClinicStaffRole.Receptionist;
    public string? Permissions { get; private set; }
    public string Token { get; private set; } = null!;
    public StaffInvitationStatus Status { get; private set; } = StaffInvitationStatus.PENDING;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? AcceptedAt { get; private set; }
    public string? AcceptedByUserId { get; private set; }

    public Clinic Clinic { get; private set; } = null!;

    public static StaffInvitation Create(
        string clinicId,
        string invitedByUserId,
        string email,
        string role,
        DateTime utcNow,
        string? permissions = null)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            ClinicId = clinicId,
            InvitedByUserId = invitedByUserId,
            Email = email.Trim().ToLowerInvariant(),
            Role = role,
            Permissions = permissions,
            // The Prisma default was a uuid; keeping that means an existing invite link format
            // stays valid.
            Token = Guid.NewGuid().ToString(),
            ExpiresAt = utcNow.Add(Lifetime),
            Status = StaffInvitationStatus.PENDING
        };

    public bool IsAcceptable(DateTime utcNow)
        => Status == StaffInvitationStatus.PENDING && ExpiresAt > utcNow;

    public void Accept(string userId, DateTime utcNow)
    {
        Status = StaffInvitationStatus.ACCEPTED;
        AcceptedAt = utcNow;
        AcceptedByUserId = userId;
    }

    public void Revoke() => Status = StaffInvitationStatus.REVOKED;

    public void MarkExpired() => Status = StaffInvitationStatus.EXPIRED;
}
