using Tebrazi.SharedKernel.Base;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Invitation</c> model (table <c>invitations</c>). Note it has no
/// navigation to Organization or User in the Node schema either — it is looked up by token
/// and joined manually.
/// </summary>
public sealed class Invitation : ImmutableEntity<string>
{
    private Invitation() { }

    public string OrganizationId { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public OrgRole Role { get; private set; } = OrgRole.MEMBER;
    public string Token { get; private set; } = null!;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? AcceptedAt { get; private set; }
    public string InvitedBy { get; private set; } = null!;

    public static Invitation Create(
        string organizationId,
        string email,
        OrgRole role,
        string token,
        DateTime expiresAt,
        string invitedBy)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            OrganizationId = organizationId,
            Email = email.Trim().ToLowerInvariant(),
            Role = role,
            Token = token,
            ExpiresAt = expiresAt,
            InvitedBy = invitedBy
        };

    public bool IsPending => AcceptedAt is null;

    public bool IsExpired(DateTime utcNow) => ExpiresAt <= utcNow;

    public void Accept(DateTime utcNow) => AcceptedAt = utcNow;
}

/// <summary>
/// Port of the Prisma <c>OtpCode</c> model (table <c>otp_codes</c>). Used for the phone-first
/// account-claim flow. <see cref="Attempts"/> is incremented on every failed comparison so the
/// verify endpoint can lock a code out.
/// </summary>
public sealed class OtpCode : ImmutableEntity<string>
{
    public const string PurposeClaimAccount = "CLAIM_ACCOUNT";
    public const string PurposeVerifyPhone = "VERIFY_PHONE";

    private OtpCode() { }

    public string Phone { get; private set; } = null!;
    public string Code { get; private set; } = null!;
    public string Purpose { get; private set; } = PurposeClaimAccount;
    public DateTime ExpiresAt { get; private set; }
    public bool Verified { get; private set; }
    public int Attempts { get; private set; }

    public static OtpCode Issue(string phone, string code, string purpose, DateTime expiresAt)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Phone = phone,
            Code = code,
            Purpose = purpose,
            ExpiresAt = expiresAt
        };

    public bool IsUsable(DateTime utcNow) => !Verified && ExpiresAt > utcNow;

    public void RecordFailedAttempt() => Attempts++;

    public void MarkVerified() => Verified = true;
}
