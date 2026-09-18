using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Application.DependencyInjection;
using Tebrazi.Identity.Infrastructure.Configuration;
using Tebrazi.Identity.Infrastructure.Security;
using Tebrazi.Identity.Persistence.DependencyInjection;

namespace Tebrazi.Identity.Infrastructure.DependencyInjection;

public static class IdentityInfrastructureModule
{
    /// <summary>
    /// Wires the whole Identity module — application handlers, persistence and security
    /// services — behind one call, so a host's Program.cs adds a module in one line.
    /// </summary>
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            // Fails at startup rather than on the first login attempt.
            .Validate(o => !string.IsNullOrWhiteSpace(o.Secret), "Jwt:Secret must be configured.")
            .ValidateOnStart();

        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        services.AddSingleton<ISecureTokenGenerator, SecureTokenGenerator>();

        services.AddIdentityPersistence(configuration, isDevelopment);
        services.AddIdentityApplication();

        return services;
    }
}
