using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Connections.Application.Abstractions.Persistence;
using Tebrazi.Connections.Application.Services;
using Tebrazi.Connections.Persistence;
using Tebrazi.Connections.Persistence.Services;
using Tebrazi.Connections.Persistence.Stores;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Connections.Infrastructure.DependencyInjection;

public static class ConnectionsModule
{
    /// <summary>
    /// Wires the Connections module: its context, stores, shared services and handler scan.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">
    /// The host configuration. Read for <c>Client:Url</c> (environment variable
    /// <c>Client__Url</c>), the port's equivalent of Node's <c>process.env.CLIENT_URL</c>. The key
    /// is absent from <c>appsettings.json</c> on purpose — unset falls back exactly as Node does,
    /// and the two fallbacks are different ports. See <see cref="ConnClientUrls"/>.
    /// </param>
    /// <param name="isDevelopment">Passed through to the shared context settings.</param>
    public static IServiceCollection AddConnectionsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("TebraziDb")
            ?? throw new InvalidOperationException("Connection string 'TebraziDb' is not configured.");

        services.AddDbContext<ConnectionsDbContext>(options =>
            options.ApplyGlobalSettings(
                connectionString,
                migrationsHistoryTable: "__EFMigrationsHistory_Connections",
                isDevelopment));

        services.AddScoped<IConnectionsDbContext>(sp => sp.GetRequiredService<ConnectionsDbContext>());

        services.AddScoped<IDoctorPatientConnectionStore, DoctorPatientConnectionStore>();
        services.AddScoped<IConnectionPinStore, ConnectionPinStore>();

        // The published read port. GET /api/patients/dashboard resolves doctors[] and
        // stats.totalDoctors through this; without it that endpoint stays one field short.
        services.AddScoped<IConnectionDirectory, ConnectionDirectory>();

        // The staff fallback, spelled out seven times in connections.js and resolved once here.
        services.AddScoped<ConnStaffResolver>();

        // Stateless and configuration-only, so singletons.
        services.AddSingleton(new ConnClientUrls(configuration["Client:Url"]));
        services.AddSingleton<ConnQrCodeRenderer>();

        services.AddHandlersFromAssembly(typeof(IConnectionsDbContext).Assembly);

        return services;
    }
}
