using Tebrazi.SharedKernel.Abstractions.Auditing;

namespace Tebrazi.SharedKernel.Base;

/// <summary>An entity stamped once on creation. Stamping happens in BaseDbContext, never by hand.</summary>
public abstract class ImmutableEntity<TKey> : Entity<TKey>, ICreationAuditable
{
    public DateTime CreatedAt { get; protected set; }
    public string CreatedBy { get; protected set; } = "UNKNOWN";

    protected ImmutableEntity() { }
    protected ImmutableEntity(TKey id) : base(id) { }

    public void ApplyCreationAudit(DateTime createdAt, string createdBy)
    {
        CreatedAt = createdAt;
        CreatedBy = createdBy;
    }
}
