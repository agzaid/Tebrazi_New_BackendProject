using Microsoft.EntityFrameworkCore;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Persistence.Services;

/// <summary>
/// Identity's implementation of the organization ports. These are the only writes another module
/// can make to identity data, and each one commits on its own.
/// </summary>
public sealed class OrganizationProvisioner(IdentityDbContext context)
    : IOrganizationProvisioner, IOrganizationMembershipWriter
{
    public async Task<string> ProvisionAsync(string name, string ownerUserId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        var slug = await ReserveSlugAsync(name, ct);
        var organization = Organization.Create(name, slug, """{"theme":"dark","language":"en"}""");

        var now = DateTime.UtcNow;

        await context.ExecuteInTransactionAsync(async token =>
        {
            context.Organizations.Add(organization);
            context.OrganizationMembers.Add(
                OrganizationMember.Create(organization.Id, ownerUserId, OrgRole.OWNER));

            context.Subscriptions.Add(Subscription.Create(
                organization.Id,
                SubscriptionPlan.FREE,
                SubscriptionStatus.ACTIVE,
                now,
                // Self-service plans do not expire; the Node route uses +100 years.
                now.AddYears(100)));

            await context.SaveChangesAsync(token);
        }, ct);

        return organization.Id;
    }

    public async Task DeleteAsync(string organizationId, CancellationToken ct = default)
    {
        var organization = await context.Organizations
            .FirstOrDefaultAsync(o => o.Id == organizationId, ct);

        // Already gone, most likely by cascade. Not an error.
        if (organization is null) return;

        context.Organizations.Remove(organization);
        await context.SaveChangesAsync(ct);
    }

    public Task<bool> IsMemberAsync(string organizationId, string userId, CancellationToken ct = default)
        => context.OrganizationMembers
            .AsNoTracking()
            .AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct);

    public async Task EnsureMemberAsync(string organizationId, string userId, CancellationToken ct = default)
    {
        if (await IsMemberAsync(organizationId, userId, ct)) return;

        context.OrganizationMembers.Add(
            OrganizationMember.Create(organizationId, userId, OrgRole.MEMBER));

        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Same slug loop as registration. The unique index on <c>organizations.slug</c> is the real
    /// guarantee; this only avoids the common collision.
    /// </summary>
    private async Task<string> ReserveSlugAsync(string name, CancellationToken ct)
    {
        var baseSlug = Organization.Slugify(name);
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "clinic";

        var slug = baseSlug;
        var suffix = 0;

        while (await context.Organizations.AsNoTracking().AnyAsync(o => o.Slug == slug, ct))
        {
            suffix++;
            slug = $"{baseSlug}-{suffix}";
        }

        return slug;
    }
}
