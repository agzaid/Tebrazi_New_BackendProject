namespace Tebrazi.SharedKernel.Mediation;

/// <summary>Handles a published notification. Many handlers may observe one notification.</summary>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken cancellationToken = default);
}
