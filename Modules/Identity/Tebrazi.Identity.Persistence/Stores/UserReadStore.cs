using Microsoft.EntityFrameworkCore;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Persistence.Stores;

public sealed class UserReadStore(IdentityDbContext context) : IUserReadStore
{
    private IQueryable<User> Users => context.Users.AsNoTracking();

    public Task<User?> GetByIdAsync(string id, CancellationToken ct = default)
        => Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByPhoneAsync(string phone, CancellationToken ct = default)
        => Users.FirstOrDefaultAsync(u => u.Phone == phone, ct);

    public Task<User?> GetByEmailAsync(string email, UserType? userType = null, CancellationToken ct = default)
    {
        // SQL Server collations are case-insensitive by default, so equality already behaves the
        // way Prisma's mode:'insensitive' did. EF.Functions.Like is avoided deliberately: it
        // would not use the index on email.
        var query = Users.Where(u => u.Email == email);

        if (userType is { } type)
            query = query.Where(u => u.UserType == type);

        return query.FirstOrDefaultAsync(ct);
    }

    public Task<bool> EmailExistsAsync(string email, UserType userType, CancellationToken ct = default)
        => Users.AnyAsync(u => u.Email == email && u.UserType == userType, ct);

    public Task<bool> PhoneExistsAsync(string phone, CancellationToken ct = default)
        => Users.AnyAsync(u => u.Phone == phone, ct);

    public async Task<IReadOnlyList<(OrganizationMember Membership, Organization Organization)>>
        GetMembershipsAsync(string userId, CancellationToken ct = default)
    {
        // Ordered by creation so "the primary organization" is the first one joined, which is
        // what the Node route's memberships[0] resolved to. Without an explicit order the row
        // order is whatever the query plan happens to produce, and the answer would drift.
        var rows = await context.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .Join(
                context.Organizations.AsNoTracking(),
                member => member.OrganizationId,
                organization => organization.Id,
                (member, organization) => new { member, organization })
            .ToListAsync(ct);

        return rows.Select(r => (r.member, r.organization)).ToList();
    }
}
