using Microsoft.EntityFrameworkCore;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Identity.Persistence;

/// <summary>
/// The Identity module's context. Internal to the module — nothing outside injects this type,
/// or <see cref="IIdentityDbContext"/> either; other modules go through published service ports.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ICurrentUser currentUser)
    : BaseDbContext(options, currentUser), IIdentityDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<PhysicianProfile> PhysicianProfiles => Set<PhysicianProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityDbContext).Assembly);
    }
}
