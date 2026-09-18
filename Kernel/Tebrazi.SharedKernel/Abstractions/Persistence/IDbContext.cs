namespace Tebrazi.SharedKernel.Abstractions.Persistence;

/// <summary>
/// Unit of work for a module. Each module publishes its OWN derived interface
/// (e.g. IIdentityDbContext) and injects that — never this one, and never a concrete DbContext.
/// Registering several contexts against this bare interface would make resolution
/// depend on registration order.
/// </summary>
public interface IDbContext
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> inside a transaction, committing on success and rolling
    /// back on any exception.
    ///
    /// USE THIS rather than the Begin/Commit/Rollback trio below. Because retry-on-failure is
    /// enabled, a manually opened transaction throws "the configured execution strategy does not
    /// support user-initiated transactions" — the transaction has to be the retriable unit, which
    /// is what this method arranges.
    ///
    /// The delegate may therefore RUN MORE THAN ONCE after a transient fault. Keep it to database
    /// work: side effects such as sending mail belong after the call returns, not inside it.
    /// </summary>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ExecuteInTransactionAsync(Func{CancellationToken,Task},CancellationToken)"/>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Low-level transaction control, for the rare case that a transaction must span more than
    /// one call. Only safe inside an execution strategy — prefer
    /// <see cref="ExecuteInTransactionAsync(Func{CancellationToken,Task},CancellationToken)"/>.
    /// </summary>
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task CommitTransactionAsync(CancellationToken cancellationToken = default);

    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}
