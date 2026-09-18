using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Application.Services;
using Tebrazi.Clinics.Persistence;
using Tebrazi.Clinics.Persistence.Services;
using Tebrazi.Clinics.Persistence.Stores;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Authorization;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Clinics.Infrastructure.DependencyInjection;

public static class ClinicsModule
{
    public static IServiceCollection AddClinicsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("TebraziDb")
            ?? throw new InvalidOperationException("Connection string 'TebraziDb' is not configured.");

        services.AddDbContext<ClinicsDbContext>(options =>
            options.ApplyGlobalSettings(
                connectionString,
                migrationsHistoryTable: "__EFMigrationsHistory_Clinics",
                isDevelopment));

        services.AddScoped<IClinicsDbContext>(sp => sp.GetRequiredService<ClinicsDbContext>());

        services.AddScoped<IClinicReadStore, ClinicReadStore>();
        services.AddScoped<IClinicWriteStore, ClinicWriteStore>();
        services.AddScoped<IClinicStaffStore, ClinicStaffStore>();
        services.AddScoped<IStaffPinStore, StaffPinStore>();
        services.AddScoped<IStaffInvitationStore, StaffInvitationStore>();
        services.AddScoped<IClinicPatientStore, ClinicPatientStore>();

        // Clinics supplies the authorization port the API layer's [RequireClinicPermission]
        // filter depends on. Without this registration that attribute cannot resolve.
        services.AddScoped<IClinicAccessEvaluator, ClinicAccessEvaluator>();

        // The published read port onto clinic_patients. Visits and Appointments both resolve
        // chart names through this rather than by touching this module's context.
        services.AddScoped<IClinicPatientDirectory, ClinicPatientDirectory>();

        // Clinic details and staffing, read by Visits, Appointments and Prescriptions. One
        // implementation serves both interfaces, so it is registered against each rather than
        // resolved twice — two AddScoped<TService, TImpl> lines would give a request two
        // instances and two change trackers.
        services.AddScoped<ClinicDirectory>();
        services.AddScoped<IClinicDirectory>(sp => sp.GetRequiredService<ClinicDirectory>());
        services.AddScoped<IClinicWorkingHoursWriter>(sp => sp.GetRequiredService<ClinicDirectory>());

        services.AddHandlersFromAssembly(typeof(ClinicAccessEvaluator).Assembly);
        services.AddHandlersFromAssembly(Assembly.GetExecutingAssembly());

        return services;
    }
}
