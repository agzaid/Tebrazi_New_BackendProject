using Microsoft.EntityFrameworkCore;
using Tebrazi.Notifications.Application.Abstractions.Persistence;
using Tebrazi.Notifications.Domain.Entities;

namespace Tebrazi.Notifications.Persistence.Stores;

public sealed class NotificationStore(NotificationsDbContext context) : INotificationStore
{
    public Task<Notification?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);

    public async Task<(IReadOnlyList<Notification> Items, int TotalCount)> PageAsync(
        string userId, bool unreadOnly, int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;

        var query = context.Notifications.AsNoTracking().Where(n => n.UserId == userId);

        if (unreadOnly) query = query.Where(n => !n.IsRead);

        query = query.OrderByDescending(n => n.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return (items, total);
    }

    public Task<int> CountUnreadAsync(string userId, CancellationToken ct = default)
        => context.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && !n.IsRead, ct);

    public void Add(Notification notification) => context.Notifications.Add(notification);

    public void AddRange(IEnumerable<Notification> notifications)
        => context.Notifications.AddRange(notifications);
}
