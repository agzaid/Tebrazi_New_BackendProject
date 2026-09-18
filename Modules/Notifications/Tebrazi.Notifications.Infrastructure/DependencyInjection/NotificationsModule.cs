using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.Notifications.Application.Abstractions.Persistence;
using Tebrazi.Notifications.Persistence;
using Tebrazi.Notifications.Persistence.Services;
using Tebrazi.Notifications.Persistence.Stores;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Notifications.Infrastructure.DependencyInjection;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("TebraziDb")
            ?? throw new InvalidOperationException("Connection string 'TebraziDb' is not configured.");

        services.AddDbContext<NotificationsDbContext>(options =>
            options.ApplyGlobalSettings(
                connectionString,
                migrationsHistoryTable: "__EFMigrationsHistory_Notifications",
                isDevelopment));

        services.AddScoped<INotificationsDbContext>(sp => sp.GetRequiredService<NotificationsDbContext>());

        services.AddScoped<INotificationStore, NotificationStore>();

        // The published port every other module raises notifications through.
        services.AddScoped<INotificationPublisher, NotificationPublisher>();

        services.AddHandlersFromAssembly(typeof(INotificationsDbContext).Assembly);

        return services;
    }
}
