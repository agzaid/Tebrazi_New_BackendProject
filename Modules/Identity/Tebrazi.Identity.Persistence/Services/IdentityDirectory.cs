using Microsoft.EntityFrameworkCore;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Identity.Persistence.Services;

/// <summary>
/// Identity's implementation of the cross-module read port. This is the ONLY way another module
/// reaches identity data, and every method here is non-tracking and read-only.
/// </summary>
public sealed class IdentityDirectory(IdentityDbContext context) : IIdentityDirectory
{
    public async Task<UserSummary?> GetUserAsync(string userId, CancellationToken ct = default)
        => await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => ToSummary(u))
            .FirstOrDefaultAsync(ct);

    public async Task<UserSummary?> GetUserByEmailAsync(
        string email, string? userType = null, CancellationToken ct = default)
    {
        var query = context.Users.AsNoTracking().Where(u => u.Email == email);

        if (!string.IsNullOrEmpty(userType))
            query = query.Where(u => u.UserType.ToString() == userType);

        return await query.Select(u => ToSummary(u)).FirstOrDefaultAsync(ct);
    }

    public async Task<string?> GetCurrentOrganizationIdAsync(
        string userId, CancellationToken ct = default)
        => await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.CurrentOrganizationId)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, UserSummary>> GetUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return new Dictionary<string, UserSummary>();

        var users = await context.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => ToSummary(u))
            .ToListAsync(ct);

        return users.ToDictionary(u => u.Id, StringComparer.Ordinal);
    }

    public async Task<PhysicianSummary?> GetPhysicianByUserIdAsync(string userId, CancellationToken ct = default)
        => await context.PhysicianProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new PhysicianSummary(
                p.Id, p.UserId, p.LicenseNumber, p.Specialty, p.Verified, p.User.DisplayName))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, PhysicianSummary>> GetPhysiciansAsync(
        IReadOnlyCollection<string> physicianProfileIds, CancellationToken ct = default)
    {
        if (physicianProfileIds.Count == 0) return new Dictionary<string, PhysicianSummary>();

        var physicians = await context.PhysicianProfiles
            .AsNoTracking()
            .Where(p => physicianProfileIds.Contains(p.Id))
            .Select(p => new PhysicianSummary(
                p.Id, p.UserId, p.LicenseNumber, p.Specialty, p.Verified, p.User.DisplayName))
            .ToListAsync(ct);

        return physicians.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public Task<PhysicianSummary?> GetPhysicianAsync(
        string physicianProfileId, CancellationToken ct = default)
        => context.PhysicianProfiles
            .AsNoTracking()
            .Where(p => p.Id == physicianProfileId)
            .Select(p => new PhysicianSummary(
                p.Id, p.UserId, p.LicenseNumber, p.Specialty, p.Verified, p.User.DisplayName))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, PhysicianDetail>> GetPhysicianDetailsAsync(
        IReadOnlyCollection<string> physicianProfileIds, CancellationToken ct = default)
    {
        if (physicianProfileIds.Count == 0) return new Dictionary<string, PhysicianDetail>();

        var ids = physicianProfileIds.Distinct().ToArray();

        var physicians = await context.PhysicianProfiles
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new PhysicianDetail(
                p.Id, p.UserId, p.LicenseNumber, p.Specialty, p.Qualifications, p.Bio,
                p.ScratchpadNotes, p.YearsOfExperience, p.Verified, p.VerifiedAt,
                p.CreatedAt, p.UpdatedAt, p.User.DisplayName))
            .ToListAsync(ct);

        return physicians.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public Task<string?> GetSubscriptionTierAsync(string userId, CancellationToken ct = default)
        => context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (string?)u.SubscriptionTier)
            .FirstOrDefaultAsync(ct);

    // Static so EF can translate it inside a Select projection rather than materialising rows.
    // ── Added for the Connections module ─────────────────────────────────────

    public async Task<UserSummary?> GetUserByPhoneAsync(
        string phone, string? userType = null, bool activeOnly = false, CancellationToken ct = default)
    {
        // Compared verbatim. The caller has already applied the route's own normalization
        // (ConnJs.NormalizePhone strips whitespace and hyphens only) and a second pass here would
        // match rows the Node lookup misses.
        var query = context.Users.AsNoTracking().Where(u => u.Phone == phone);

        if (!string.IsNullOrEmpty(userType))
            query = query.Where(u => u.UserType.ToString() == userType);

        if (activeOnly)
            query = query.Where(u => u.Active);

        return await query.Select(u => ToSummary(u)).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<UserSearchRow>> SearchUsersAsync(
        UserSearchFilter filter, CancellationToken ct = default)
    {
        var query = context.Users.AsNoTracking();

        if (!string.IsNullOrEmpty(filter.UserType))
            query = query.Where(u => u.UserType.ToString() == filter.UserType);

        if (filter.ActiveOnly)
            query = query.Where(u => u.Active);

        // Prisma's `{ contains: q, mode: 'insensitive' }` — a substring match anywhere in the
        // value, not a prefix. EF's Contains over a SQL Server column already uses the column's
        // collation, which is case-insensitive here, so no manual lowering is needed (and would
        // defeat any index).
        var term = filter.Query;

        query = (filter.MatchDisplayName, filter.MatchEmail, filter.MatchPhone) switch
        {
            (true, true, true) => query.Where(u =>
                u.DisplayName.Contains(term)
                || (u.Email != null && u.Email.Contains(term))
                || (u.Phone != null && u.Phone.Contains(term))),

            (true, false, false) => query.Where(u => u.DisplayName.Contains(term)),

            _ => query.Where(u =>
                (filter.MatchDisplayName && u.DisplayName.Contains(term))
                || (filter.MatchEmail && u.Email != null && u.Email.Contains(term))
                || (filter.MatchPhone && u.Phone != null && u.Phone.Contains(term)))
        };

        // Node has no orderBy on either call site, so Postgres returns physical order. On the
        // search path that is observable because `take: 10` makes it decide WHICH ten rows come
        // back. Ordering by created_at then id is the closest deterministic equivalent for a
        // mostly-append-only table. Recorded in docs/connections-surface.md §10.
        var ordered = query.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id);

        var limited = filter.Take is { } take ? ordered.Take(take) : ordered;

        return await limited
            .Select(u => new UserSearchRow(
                u.Id, u.DisplayName, u.Email, u.Phone, u.UserType.ToString(), u.CreatedAt))
            .ToListAsync(ct);
    }

    public Task<bool> ExistsByEmailOrPhoneAsync(
        string email, string? phone, CancellationToken ct = default)
    {
        // No userType and no active filter: a physician account or a deactivated account holding
        // that email or phone is still a collision (connections.js:649-651).
        if (string.IsNullOrEmpty(phone))
            return context.Users.AsNoTracking().AnyAsync(u => u.Email == email, ct);

        return context.Users
            .AsNoTracking()
            .AnyAsync(u => u.Email == email || u.Phone == phone, ct);
    }

    private static UserSummary ToSummary(Domain.Entities.User u) => new(
        u.Id, u.Email, u.DisplayName, u.Phone, u.ProfilePictureUrl,
        u.Role.ToString(), u.UserType.ToString(), u.Active);
}
