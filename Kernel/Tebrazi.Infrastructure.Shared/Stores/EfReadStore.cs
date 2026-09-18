using Microsoft.EntityFrameworkCore;
using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Infrastructure.Shared.Stores;

/// <summary>
/// Read-side base store. Every query it exposes is <c>AsNoTracking</c>, and paging is done in
/// the database via Skip/Take — never by materialising a set and slicing it in memory.
/// </summary>
public abstract class EfReadStore<TEntity, TKey>(DbContext context)
    where TEntity : Entity<TKey>
{
    protected DbContext Context { get; } = context;

    protected IQueryable<TEntity> Query => Context.Set<TEntity>().AsNoTracking();

    public virtual Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
        => Query.FirstOrDefaultAsync(e => e.Id!.Equals(id), cancellationToken);

    public virtual Task<bool> ExistsAsync(TKey id, CancellationToken cancellationToken = default)
        => Query.AnyAsync(e => e.Id!.Equals(id), cancellationToken);

    public virtual Task<List<TEntity>> ListAsync(CancellationToken cancellationToken = default)
        => Query.ToListAsync(cancellationToken);

    /// <summary>
    /// One count and one page, both translated to SQL. <paramref name="page"/> is 1-based to
    /// match the query strings the React client sends.
    /// </summary>
    protected static async Task<(List<T> Items, int TotalCount)> PageAsync<T>(
        IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }
}
