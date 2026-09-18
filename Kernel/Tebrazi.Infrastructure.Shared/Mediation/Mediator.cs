using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Infrastructure.Shared.Mediation;

/// <summary>
/// Resolves the single <see cref="IRequestHandler{TRequest,TResult}"/> for a request and invokes it.
/// Unlike the KACCC original this DOES forward the CancellationToken; dropping it there meant
/// no handler could ever observe a cancelled request.
/// </summary>
public sealed class Mediator(IServiceProvider provider) : IMediator
{
    // Closed handler type per request type. Reflection cost is paid once per type, not per call.
    private static readonly Dictionary<Type, HandlerInvoker> Invokers = [];
    private static readonly Lock InvokerLock = new();

    public Task<TResult> Send<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invoker = GetInvoker(request.GetType(), typeof(TResult));
        var handler = provider.GetService(invoker.HandlerType)
            ?? throw new InvalidOperationException(
                $"No handler registered for '{request.GetType().Name}'. " +
                $"Expected a registration for '{invoker.HandlerType}'. " +
                "Handlers are registered by AddHandlersFromAssembly in the module's Application DI module.");

        return (Task<TResult>)invoker.Invoke(handler, request, cancellationToken);
    }

    public async Task Publish<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Notification handlers run sequentially: one failing handler must not leave the
        // remainder in an indeterminate state mid-flight.
        foreach (var handler in provider.GetServices<INotificationHandler<TNotification>>())
            await handler.Handle(notification, cancellationToken);
    }

    private static HandlerInvoker GetInvoker(Type requestType, Type resultType)
    {
        lock (InvokerLock)
        {
            if (Invokers.TryGetValue(requestType, out var cached))
                return cached;

            var handlerType = typeof(IRequestHandler<,>).MakeGenericType(requestType, resultType);
            var method = handlerType.GetMethod(nameof(IRequestHandler<IRequest<object>, object>.Handle))
                ?? throw new InvalidOperationException($"'{handlerType}' has no Handle method.");

            var invoker = new HandlerInvoker(
                handlerType,
                (handler, request, ct) =>
                {
                    try
                    {
                        return method.Invoke(handler, [request, ct])!;
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is not null)
                    {
                        // MethodInfo.Invoke wraps anything thrown SYNCHRONOUSLY by the handler.
                        // An `async` Handle captures its exceptions in the returned Task and is
                        // unaffected, but a handler declared without `async` — or one that
                        // validates before its first await — throws straight out of Invoke. Left
                        // wrapped, a NotFoundException would reach ExceptionHandlingMiddleware as
                        // a TargetInvocationException and answer 500 instead of 404.
                        //
                        // Rethrow via ExceptionDispatchInfo so the original stack trace survives.
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        throw; // unreachable; the compiler cannot see that Throw() never returns.
                    }
                });

            Invokers[requestType] = invoker;
            return invoker;
        }
    }

    private sealed record HandlerInvoker(Type HandlerType, Func<object, object, CancellationToken, object> Invoke);
}
