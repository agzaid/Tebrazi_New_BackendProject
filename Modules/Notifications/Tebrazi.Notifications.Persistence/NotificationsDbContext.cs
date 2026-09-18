using Microsoft.EntityFrameworkCore;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.Notifications.Application.Abstractions.Persistence;
using Tebrazi.Notifications.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Notifications.Persistence;

/// <summary>The Notifications module's context: <c>notifications</c>.</summary>
public sealed class NotificationsDbContext(
    DbContextOptions<NotificationsDbContext> options,
    ICurrentUser currentUser)
    : BaseDbContext(options, currentUser), INotificationsDbContext
{
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
    }
}
