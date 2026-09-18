using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Persistence.Services;
using Tebrazi.Identity.Persistence.Stores;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.Infrastructure.Shared.Persistence;

namespace Tebrazi.Identity.Persistence.DependencyInjection;

public static class IdentityPersistenceModule
{
    /// <summary>
    /// Registers the Identity context and its stores.
    ///
    /// Note this is the ONLY registration of <see cref="IdentityDbContext"/> in the solution and
    /// it routes through <see cref="DbContextOptionsExtensions.ApplyGlobalSettings"/>. Adding a
    /// second registration elsewhere with a bare <c>UseSqlServer</c> would quietly strip
    /// retry-on-failure in whichever host called it.
    /// </summary>
    public static IServiceCollection AddIdentityPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("TebraziDb")
            ?? throw new InvalidOperationException(
                "Connection string 'TebraziDb' is not configured. " +
                "Set ConnectionStrings:TebraziDb in appsettings or the environment.");

        services.AddDbContext<IdentityDbContext>(options =>
            options.ApplyGlobalSettings(
                connectionString,
                migrationsHistoryTable: "__EFMigrationsHistory_Identity",
                isDevelopment));

        // Handlers depend on the module's own interface, never on the concrete context.
        services.AddScoped<IIdentityDbContext>(sp => sp.GetRequiredService<IdentityDbContext>());

        services.AddScoped<IUserReadStore, UserReadStore>();
        services.AddScoped<IUserWriteStore, UserWriteStore>();
        services.AddScoped<IOrganizationStore, OrganizationStore>();
        services.AddScoped<ISessionStore, SessionStore>();
        services.AddScoped<ITokenStore, TokenStore>();

        // Cross-module ports Identity publishes. Other modules depend on these interfaces and
        // never on IdentityDbContext.
        services.AddScoped<IIdentityDirectory, IdentityDirectory>();
        services.AddScoped<OrganizationProvisioner>();
        services.AddScoped<IOrganizationProvisioner>(sp => sp.GetRequiredService<OrganizationProvisioner>());
        services.AddScoped<IOrganizationMembershipWriter>(sp => sp.GetRequiredService<OrganizationProvisioner>());

        // The one write port onto `users`, for POST /api/connections/create-patient.
        services.AddScoped<IPatientAccountProvisioner, PatientAccountProvisioner>();

        return services;
    }
}
