using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Auditing;
using Tebrazi.SharedKernel.Abstractions.Persistence;

namespace Tebrazi.Infrastructure.Shared.Persistence;

/// <summary>
/// Every module context derives from this. It centralises audit stamping and UTC handling so
/// no module can bypass either. Do not override <see cref="SaveChangesAsync"/> in a derived
/// context without calling base — that is how auditing silently stops.
/// </summary>
public abstract class BaseDbContext(DbContextOptions options, ICurrentUser currentUser)
    : DbContext(options), IDbContext
{
    private IDbContextTransaction? _transaction;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Postgres stored timestamptz; SQL Server datetime2 carries no offset. Converting on the
        // way in and stamping Kind on the way out keeps every DateTime in the model UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditing();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyAuditing();
        return base.SaveChanges();
    }

    /// <summary>
    /// Runs the operation as one retriable unit: the execution strategy owns the transaction, so
    /// a transient fault retries the WHOLE unit rather than failing mid-way with a half-applied
    /// write. This is the only transaction API module handlers should use.
    /// </summary>
    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ExecuteInTransactionAsync<object?>(async ct =>
        {
            await operation(ct);
            return null;
        }, cancellationToken);
    }

    /// <inheritdoc cref="ExecuteInTransactionAsync(Func{CancellationToken,Task},CancellationToken)"/>
    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var strategy = Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await Database.BeginTransactionAsync(ct);

            try
            {
                var result = await operation(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Opens a transaction. The handle is held here so Commit/Rollback act on the transaction
    /// this context actually started, rather than on whatever the provider last opened.
    /// Only safe inside an execution strategy — see <see cref="ExecuteInTransactionAsync"/>.
    /// </summary>
    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("A transaction is already open on this context.");

        _transaction = await Database.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
            throw new InvalidOperationException("No transaction is open on this context.");

        try
        {
            await _transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null) return;

        try
        {
            await _transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    public override void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void ApplyAuditing()
    {
        var now = DateTime.UtcNow;
        var userId = currentUser.UserId ?? "SYSTEM";

        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is ICreationAuditable created)
                        created.ApplyCreationAudit(now, userId);

                    // Stamped on INSERT as well as on UPDATE, because Prisma's @updatedAt is a
                    // NON-NULLABLE DateTime that it sets on create too. Leaving UpdatedAt null
                    // on insert put a NULL in updated_at where Postgres holds a timestamp, and
                    // made every response echoing the column emit "updatedAt": null for a row
                    // that had simply never been modified. That is why ~30 mappers across Visits
                    // and Prescriptions carry a defensive UpdatedAt-or-CreatedAt coalesce; those
                    // stay correct for rows inserted before this fix, but a new mapper no longer
                    // has to remember it.
                    if (entry.Entity is IModificationAuditable inserted)
                        inserted.ApplyModificationAudit(now, userId);

                    break;

                case EntityState.Modified when entry.Entity is IModificationAuditable modified:
                    modified.ApplyModificationAudit(now, userId);
                    break;
            }
        }
    }
}
