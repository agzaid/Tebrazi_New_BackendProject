using Microsoft.Extensions.Logging;
using Tebrazi.Notifications.Application.Abstractions.Persistence;
using Tebrazi.Notifications.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Notifications.Persistence.Services;

/// <summary>
/// Notifications' implementation of the published <see cref="INotificationPublisher"/> port.
///
/// It swallows every failure, by contract. In the Node backend each of the twenty-odd
/// <c>createNotification</c> call sites is individually wrapped in
/// <c>try { ... } catch (e) { logger.error(...) }</c>, so a notification that cannot be written
/// never fails the clinical write that triggered it. Centralising that here keeps the same
/// behaviour without repeating the guard in every handler.
///
/// Email and push dispatch are NOT wired: <c>emailService</c> and <c>pushService</c> have no
/// .NET equivalent yet. <c>SendEmail</c> is honoured only as a log line, which matches the Node
/// behaviour in an environment with no mail transport configured.
/// </summary>
public sealed class NotificationPublisher(
    NotificationsDbContext context,
    INotificationsDbContext unitOfWork,
    ILogger<NotificationPublisher> logger) : INotificationPublisher
{
    public Task PublishAsync(NotificationRequest request, CancellationToken ct = default)
        => PublishAsync([request], ct);

    public async Task PublishAsync(
        IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0) return;

        try
        {
            var rows = requests
                .Select(r => Notification.Create(r.UserId, r.Type, r.Title, r.Message, r.Data))
                .ToList();

            context.Notifications.AddRange(rows);
            await unitOfWork.SaveChangesAsync(ct);

            foreach (var pending in requests.Where(r => r.SendEmail))
            {
                logger.LogInformation(
                    "Notification {Type} for user {UserId} requested an email; no mail transport is configured.",
                    pending.Type, pending.UserId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away. Not a notification failure, and not ours to swallow.
            throw;
        }
        catch (Exception ex)
        {
            // By contract: log and return. The clinical write that triggered this has either
            // already committed or is about to, and must not be undone by a failed bell badge.
            logger.LogError(
                ex,
                "Failed to write {Count} notification(s); types: {Types}",
                requests.Count,
                string.Join(", ", requests.Select(r => r.Type).Distinct()));
        }
    }
}
