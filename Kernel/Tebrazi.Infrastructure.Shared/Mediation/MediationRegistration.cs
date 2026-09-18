using Microsoft.Extensions.DependencyInjection;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Infrastructure.Shared.Mediation;

public static class MediationRegistration
{
    /// <summary>
    /// Registers the dispatcher itself. Call once, from the API host.
    /// Handler registration is separate and lives in the kernel — see
    /// <see cref="HandlerRegistration.AddHandlersFromAssembly"/>, which each module calls.
    /// </summary>
    public static IServiceCollection AddMediator(this IServiceCollection services)
    {
        services.AddScoped<IMediator, Mediator>();
        services.AddScoped<IPublisher>(sp => sp.GetRequiredService<IMediator>());
        return services;
    }
}
