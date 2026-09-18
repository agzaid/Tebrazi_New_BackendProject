namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The published write port onto <c>notifications</c>, a direct port of
/// <c>server/src/services/notificationService.js</c>'s <c>createNotification</c>.
///
/// Visits, Appointments and Prescriptions all raise notifications, and none of them owns the
/// table — the Notifications module does.
///
/// <para><b>This port never throws.</b> Every call site in the Node backend wraps
/// <c>createNotification</c> in its own try/catch that logs and continues, because a failed
/// notification must not fail the clinical write that triggered it. Rather than repeat that
/// try/catch in twenty handlers, the guarantee lives here: an implementation logs and returns.
/// A handler calling this can therefore ignore failure, and MUST NOT treat a completed call as
/// proof a row was written.</para>
///
/// <para>Because it commits through the Notifications unit of work, a call from inside
/// <c>ExecuteInTransactionAsync</c> is NOT covered by that transaction, and the delegate there
/// may run more than once after a transient fault. Raise notifications after the transaction
/// returns, not inside it.</para>
/// </summary>
public interface INotificationPublisher
{
    Task PublishAsync(NotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Writes several notifications in one commit — one visit completing notifies the patient
    /// and every assistant at the clinic, and that should not be a round trip each.
    /// </summary>
    Task PublishAsync(IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default);
}

/// <param name="Type">
/// The notification type, e.g. <c>VISIT_COMPLETED</c>. Free text on purpose: the Node service
/// declares a <c>NOTIFICATION_TYPES</c> list but never validates against it, and live call sites
/// already pass values missing from that list (<c>FOLLOW_UP_SET</c>,
/// <c>VISIT_COMPLETED_STAFF</c>). Rejecting unknown types here would break those endpoints.
/// </param>
/// <param name="Data">Opaque JSON payload, stored verbatim. Null when there is none.</param>
/// <param name="SendEmail">
/// Requests an email alongside the row. Best-effort in Node, and equally so here — a failed
/// send is logged and the notification still stands.
/// </param>
public sealed record NotificationRequest(
    string UserId,
    string Type,
    string Title,
    string Message,
    string? Data = null,
    bool SendEmail = false);
