using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.Prescriptions.Application.Abstractions.Persistence;
using Tebrazi.Prescriptions.Persistence;
using Tebrazi.Prescriptions.Persistence.Services;
using Tebrazi.Prescriptions.Persistence.Stores;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Prescriptions.Infrastructure.DependencyInjection;

public static class PrescriptionsModule
{
    public static IServiceCollection AddPrescriptionsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("TebraziDb")
            ?? throw new InvalidOperationException("Connection string 'TebraziDb' is not configured.");

        services.AddDbContext<PrescriptionsDbContext>(options =>
            options.ApplyGlobalSettings(
                connectionString,
                migrationsHistoryTable: "__EFMigrationsHistory_Prescriptions",
                isDevelopment));

        services.AddScoped<IPrescriptionsDbContext>(sp => sp.GetRequiredService<PrescriptionsDbContext>());

        services.AddScoped<IPrescriptionStore, PrescriptionStore>();
        services.AddScoped<IInteractionAlertStore, InteractionAlertStore>();

        // The published read port Visits uses for _count and the embedded prescription rows.
        services.AddScoped<IPrescriptionDirectory, PrescriptionDirectory>();

        services.AddHandlersFromAssembly(typeof(IPrescriptionsDbContext).Assembly);

        return services;
    }
}
