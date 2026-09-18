using Microsoft.EntityFrameworkCore;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Domain.Entities;

namespace Tebrazi.Identity.Persistence.Stores;

/// <summary>
/// Stages user-graph writes. Like every write store here it never calls SaveChanges — the
/// handler owns the transaction so a registration's five inserts commit together.
/// </summary>
public sealed class UserWriteStore(IdentityDbContext context) : IUserWriteStore
{
    public Task<User?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public void Add(User user) => context.Users.Add(user);

    public void Add(PhysicianProfile profile) => context.PhysicianProfiles.Add(profile);


    public void Add(OrganizationMember member) => context.OrganizationMembers.Add(member);

    public void Remove(User user) => context.Users.Remove(user);
}

public sealed class OrganizationStore(IdentityDbContext context) : IOrganizationStore
{
    public Task<Organization?> GetByIdAsync(string id, CancellationToken ct = default)
        => context.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
        => context.Organizations.AsNoTracking().AnyAsync(o => o.Slug == slug, ct);

    public void Add(Organization organization) => context.Organizations.Add(organization);

    public void Add(Subscription subscription) => context.Subscriptions.Add(subscription);
}

public sealed class SessionStore(IdentityDbContext context) : ISessionStore
{
    public async Task<IReadOnlyList<Session>> ListForUserAsync(string userId, CancellationToken ct = default)
        => await context.Sessions
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

    public Task<Session?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public void Add(Session session) => context.Sessions.Add(session);

    public void Remove(Session session) => context.Sessions.Remove(session);

    public void RemoveRange(IEnumerable<Session> sessions) => context.Sessions.RemoveRange(sessions);
}

public sealed class TokenStore(IdentityDbContext context) : ITokenStore
{
    // Tracked, not AsNoTracking: both reset tokens and invitations are read in order to be
    // redeemed in the same unit of work.
    public Task<PasswordResetToken?> GetResetTokenAsync(string token, CancellationToken ct = default)
        => context.PasswordResetTokens.FirstOrDefaultAsync(t => t.Token == token, ct);

    public Task<Invitation?> GetInvitationAsync(string token, CancellationToken ct = default)
        => context.Invitations.FirstOrDefaultAsync(i => i.Token == token, ct);

    /// <summary>The most recent code for a phone and purpose; older ones are ignored, not deleted.</summary>
    public Task<OtpCode?> GetLatestOtpAsync(string phone, string purpose, CancellationToken ct = default)
        => context.OtpCodes
            .Where(o => o.Phone == phone && o.Purpose == purpose)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public void Add(PasswordResetToken token) => context.PasswordResetTokens.Add(token);

    public void Add(Invitation invitation) => context.Invitations.Add(invitation);

    public void Add(OtpCode code) => context.OtpCodes.Add(code);
}
