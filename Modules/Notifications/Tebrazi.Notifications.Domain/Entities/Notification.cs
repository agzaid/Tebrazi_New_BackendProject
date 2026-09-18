using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Notifications.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Notification</c> model (table <c>notifications</c>).
///
/// This module currently exists to BACK the <c>INotificationPublisher</c> port that Visits,
/// Appointments and Prescriptions raise notifications through. Its own four client endpoints
/// (list, unread count, mark-read, mark-all-read) are not ported yet, so there is no controller
/// here — only the table, a store, and the port implementation.
/// </summary>
public sealed class Notification : ImmutableEntity<string>
{
    private Notification() { }

    public string UserId { get; private set; } = null!;

    /// <summary>
    /// e.g. <c>VISIT_COMPLETED</c>. Free text, not an enum: the Node service keeps a list of
    /// types but never validates against it, and live call sites already use values absent from
    /// that list.
    /// </summary>
    public string Type { get; private set; } = null!;

    public string Title { get; private set; } = null!;
    public string Message { get; private set; } = null!;

    /// <summary>Opaque JSON payload — usually the id of whatever the notification is about.</summary>
    public string? Data { get; private set; }

    public bool IsRead { get; private set; }
    public DateTime? ReadAt { get; private set; }

    public static Notification Create(
        string userId,
        string type,
        string title,
        string message,
        string? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        return new Notification
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            Type = type,
            Title = title,
            // Not validated as non-empty: the Node column is required but several call sites
            // build the message by interpolation and can legitimately produce an empty string.
            Message = message ?? string.Empty,
            Data = data,
            IsRead = false
        };
    }

    /// <summary>Marks it read. Idempotent — re-reading does not move the timestamp.</summary>
    public void MarkRead()
    {
        if (IsRead) return;

        IsRead = true;
        ReadAt = DateTime.UtcNow;
    }
}
