using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Application.Abstractions.Persistence;

/// <summary>Read-side access to users. Every method here is non-tracking.</summary>
public interface IUserReadStore
{
    Task<User?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Exact match on the normalized phone number, which is globally unique.</summary>
    Task<User?> GetByPhoneAsync(string phone, CancellationToken ct = default);

    /// <summary>
    /// Case-insensitive email lookup, optionally narrowed by user type.
    ///
    /// Email alone is NOT unique — the Node schema keys on (email, userType) so one address can
    /// hold both a physician and a patient account. Callers that omit
    /// <paramref name="userType"/> may match either, so pass it whenever the caller knows which
    /// portal is being logged into.
    /// </summary>
    Task<User?> GetByEmailAsync(string email, UserType? userType = null, CancellationToken ct = default);

    Task<bool> EmailExistsAsync(string email, UserType userType, CancellationToken ct = default);

    Task<bool> PhoneExistsAsync(string phone, CancellationToken ct = default);

    /// <summary>The user's organizations, ordered as the Node route returns them.</summary>
    Task<IReadOnlyList<(OrganizationMember Membership, Organization Organization)>> GetMembershipsAsync(
        string userId, CancellationToken ct = default);
}

/// <summary>Write-side access to users. Stages changes; the handler commits.</summary>
public interface IUserWriteStore
{
    Task<User?> GetForUpdateAsync(string id, CancellationToken ct = default);
    void Add(User user);
    void Add(PhysicianProfile profile);
    void Add(OrganizationMember member);
    void Remove(User user);
}

/// <summary>Read and write access to organizations and their subscriptions.</summary>
public interface IOrganizationStore
{
    Task<Organization?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);
    void Add(Organization organization);
    void Add(Subscription subscription);
}

/// <summary>Session issuance and revocation, backing the active-sessions screen.</summary>
public interface ISessionStore
{
    Task<IReadOnlyList<Session>> ListForUserAsync(string userId, CancellationToken ct = default);
    Task<Session?> GetForUpdateAsync(string id, CancellationToken ct = default);
    void Add(Session session);
    void Remove(Session session);
    void RemoveRange(IEnumerable<Session> sessions);
}

/// <summary>Password reset and invitation tokens.</summary>
public interface ITokenStore
{
    Task<PasswordResetToken?> GetResetTokenAsync(string token, CancellationToken ct = default);
    Task<Invitation?> GetInvitationAsync(string token, CancellationToken ct = default);
    Task<OtpCode?> GetLatestOtpAsync(string phone, string purpose, CancellationToken ct = default);
    void Add(PasswordResetToken token);
    void Add(Invitation invitation);
    void Add(OtpCode code);
}
