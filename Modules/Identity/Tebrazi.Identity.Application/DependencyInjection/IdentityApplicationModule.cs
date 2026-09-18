using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.DependencyInjection;

public static class IdentityApplicationModule
{
    /// <summary>
    /// Registers this module's command and query handlers. Called from the API host's
    /// composition root alongside <c>AddIdentityInfrastructure</c>.
    /// </summary>
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        services.AddHandlersFromAssembly(Assembly.GetExecutingAssembly());
        return services;
    }
}

/// <summary>Assembly marker for this module — used for handler scanning and localization lookup.</summary>
public sealed class IdentityApplicationMarker;
