using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Visits.Application.Abstractions.Persistence;
using Tebrazi.Visits.Persistence;
using Tebrazi.Visits.Persistence.Services;
using Tebrazi.Visits.Persistence.Stores;

namespace Tebrazi.Visits.Infrastructure.DependencyInjection;

public static class VisitsModule
{
    public static IServiceCollection AddVisitsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("TebraziDb")
            ?? throw new InvalidOperationException("Connection string 'TebraziDb' is not configured.");

        services.AddDbContext<VisitsDbContext>(options =>
            options.ApplyGlobalSettings(
                connectionString,
                migrationsHistoryTable: "__EFMigrationsHistory_Visits",
                isDevelopment));

        services.AddScoped<IVisitsDbContext>(sp => sp.GetRequiredService<VisitsDbContext>());

        services.AddScoped<IVisitStore, VisitStore>();
        services.AddScoped<IInvestigationStore, InvestigationStore>();

        // The published read port Prescriptions resolves visits through. Without this
        // registration every prescription endpoint fails to construct its handler.
        services.AddScoped<IVisitDirectory, VisitDirectory>();

        // The one write port onto visits, for the cascade in
        // DELETE /api/connections/clinic-patients/{id}.
        services.AddScoped<IVisitClinicPatientWriter, VisitClinicPatientWriter>();

        services.AddHandlersFromAssembly(typeof(IVisitsDbContext).Assembly);

        return services;
    }
}
