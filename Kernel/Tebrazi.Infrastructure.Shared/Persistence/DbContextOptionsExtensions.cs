using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Tebrazi.Infrastructure.Shared.Persistence;

public static class DbContextOptionsExtensions
{
    /// <summary>
    /// The one place SQL Server options are set. EVERY <c>AddDbContext</c> in the solution must
    /// route through this — a context registered with a bare <c>UseSqlServer</c> silently loses
    /// retry-on-failure and the cartesian-explosion guard, and nothing in the build will say so.
    /// </summary>
    public static DbContextOptionsBuilder ApplyGlobalSettings(
        this DbContextOptionsBuilder builder,
        string connectionString,
        string migrationsHistoryTable,
        bool isDevelopment = false)
    {
        builder.UseSqlServer(connectionString, sql =>
        {
            // Transient faults become retries rather than user-facing 500s.
            sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null);

            sql.CommandTimeout(30);

            // Each module owns its own migrations history row set, so modules can be migrated
            // independently against the one shared database.
            sql.MigrationsHistoryTable(migrationsHistoryTable, "dbo");

            // Multiple collection includes are split rather than joined.
            sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
        });

        // The cartesian-explosion warning is promoted to an exception so an accidental one fails
        // loudly instead of quietly returning a multiplied row count.
        builder.ConfigureWarnings(w =>
            w.Throw(RelationalEventId.MultipleCollectionIncludeWarning));

        if (isDevelopment)
        {
            builder.EnableDetailedErrors();
            builder.EnableSensitiveDataLogging();
        }

        return builder;
    }
}
