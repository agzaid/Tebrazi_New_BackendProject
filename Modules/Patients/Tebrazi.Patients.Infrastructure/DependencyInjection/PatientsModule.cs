using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Application.Services;
using Tebrazi.Patients.Persistence;
using Tebrazi.Patients.Persistence.Services;
using Tebrazi.Patients.Persistence.Stores;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Patients.Infrastructure.DependencyInjection;

public static class PatientsModule
{
    public static IServiceCollection AddPatientsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("TebraziDb")
            ?? throw new InvalidOperationException("Connection string 'TebraziDb' is not configured.");

        services.AddDbContext<PatientsDbContext>(options =>
            options.ApplyGlobalSettings(
                connectionString,
                migrationsHistoryTable: "__EFMigrationsHistory_Patients",
                isDevelopment));

        services.AddScoped<IPatientsDbContext>(sp => sp.GetRequiredService<PatientsDbContext>());

        services.AddScoped<IPatientProfileStore, PatientProfileStore>();
        services.AddScoped<IFamilySubprofileStore, FamilySubprofileStore>();
        services.AddScoped<IHealthRecordStore, HealthRecordStore>();
        services.AddScoped<IExternalCareStore, ExternalCareStore>();

        services.AddScoped<PatientProfileResolver>();

        // The published read port onto dependants and their clinical records. Visits,
        // Appointments and Prescriptions all render a dependant's name through this.
        services.AddScoped<IPatientDirectory, PatientDirectory>();

        // The one write port onto patient_profiles / family_subprofiles, for
        // POST /api/connections/create-patient.
        services.AddScoped<IPatientProfileProvisioner, PatientProfileProvisioner>();

        services.AddHandlersFromAssembly(typeof(PatientProfileResolver).Assembly);

        return services;
    }
}
