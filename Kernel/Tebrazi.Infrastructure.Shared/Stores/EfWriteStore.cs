using Microsoft.EntityFrameworkCore;
using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Infrastructure.Shared.Stores;

/// <summary>
/// Write-side base store. Stores STAGE changes only — they never call SaveChanges. The handler
/// owns the unit of work and commits, which is what lets one handler write several stores
/// inside a single transaction.
/// </summary>
public abstract class EfWriteStore<TEntity, TKey>(DbContext context)
    where TEntity : Entity<TKey>
{
    protected DbContext Context { get; } = context;

    protected DbSet<TEntity> Set => Context.Set<TEntity>();

    /// <summary>Tracked query — use when the entity will be modified in the same unit of work.</summary>
    public virtual Task<TEntity?> GetForUpdateAsync(TKey id, CancellationToken cancellationToken = default)
        => Set.FirstOrDefaultAsync(e => e.Id!.Equals(id), cancellationToken);

    public virtual void Add(TEntity entity) => Set.Add(entity);

    public virtual void AddRange(IEnumerable<TEntity> entities) => Set.AddRange(entities);

    public virtual void Update(TEntity entity) => Set.Update(entity);

    public virtual void Remove(TEntity entity) => Set.Remove(entity);

    public virtual void RemoveRange(IEnumerable<TEntity> entities) => Set.RemoveRange(entities);
}
