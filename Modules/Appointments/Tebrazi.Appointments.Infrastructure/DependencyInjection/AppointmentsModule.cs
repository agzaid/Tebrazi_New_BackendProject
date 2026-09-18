using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Appointments.Application.Abstractions.Persistence;
using Tebrazi.Appointments.Persistence;
using Tebrazi.Appointments.Persistence.Services;
using Tebrazi.Appointments.Persistence.Stores;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Appointments.Infrastructure.DependencyInjection;

public static class AppointmentsModule
{
    public static IServiceCollection AddAppointmentsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("TebraziDb")
            ?? throw new InvalidOperationException("Connection string 'TebraziDb' is not configured.");

        services.AddDbContext<AppointmentsDbContext>(options =>
            options.ApplyGlobalSettings(
                connectionString,
                migrationsHistoryTable: "__EFMigrationsHistory_Appointments",
                isDevelopment));

        services.AddScoped<IAppointmentsDbContext>(sp => sp.GetRequiredService<AppointmentsDbContext>());

        services.AddScoped<IAppointmentStore, AppointmentStore>();
        services.AddScoped<ITimeSlotStore, TimeSlotStore>();

        // The published read port Visits uses to inherit a booking's subprofile.
        services.AddScoped<IAppointmentDirectory, AppointmentDirectory>();

        // The one write port onto appointments, for the cascade in
        // DELETE /api/connections/clinic-patients/{id}.
        services.AddScoped<IAppointmentClinicPatientWriter, AppointmentClinicPatientWriter>();

        services.AddHandlersFromAssembly(typeof(IAppointmentsDbContext).Assembly);

        return services;
    }
}
