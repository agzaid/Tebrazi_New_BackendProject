using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Tebrazi.SharedKernel.Mediation;

public static class HandlerRegistration
{
    /// <summary>
    /// Registers every <see cref="IRequestHandler{TRequest,TResult}"/> and
    /// <see cref="INotificationHandler{TNotification}"/> in an assembly, against their
    /// interfaces. Each module calls this from its own Application DI module.
    ///
    /// This lives in the kernel rather than in Infrastructure.Shared on purpose: it touches only
    /// mediation interfaces, so a module's Application layer can call it without taking a
    /// dependency on an infrastructure assembly.
    /// </summary>
    public static IServiceCollection AddHandlersFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(IsHandlerContract))
                services.AddScoped(contract, type);
        }

        return services;
    }

    private static bool IsHandlerContract(Type i)
    {
        if (!i.IsGenericType) return false;
        var definition = i.GetGenericTypeDefinition();
        return definition == typeof(IRequestHandler<,>)
            || definition == typeof(INotificationHandler<>);
    }
}
