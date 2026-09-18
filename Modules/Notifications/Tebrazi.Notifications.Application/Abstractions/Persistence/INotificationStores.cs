using Tebrazi.Notifications.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Persistence;

namespace Tebrazi.Notifications.Application.Abstractions.Persistence;

/// <summary>The Notifications module's unit of work.</summary>
public interface INotificationsDbContext : IDbContext;

public interface INotificationStore
{
    Task<Notification?> GetForUpdateAsync(string id, CancellationToken ct = default);

    Task<(IReadOnlyList<Notification> Items, int TotalCount)> PageAsync(
        string userId, bool unreadOnly, int page, int pageSize, CancellationToken ct = default);

    Task<int> CountUnreadAsync(string userId, CancellationToken ct = default);

    void Add(Notification notification);
    void AddRange(IEnumerable<Notification> notifications);
}
