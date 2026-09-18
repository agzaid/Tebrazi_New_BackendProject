using Tebrazi.SharedKernel.Abstractions.Auditing;

namespace Tebrazi.SharedKernel.Base;

/// <summary>An entity stamped on creation and on every modification.</summary>
public abstract class MutableEntity<TKey> : ImmutableEntity<TKey>, IModificationAuditable
{
    public DateTime? UpdatedAt { get; protected set; }
    public string? UpdatedBy { get; protected set; }

    protected MutableEntity() { }
    protected MutableEntity(TKey id) : base(id) { }

    public void ApplyModificationAudit(DateTime updatedAt, string? updatedBy)
    {
        UpdatedAt = updatedAt;
        UpdatedBy = updatedBy;
    }
}
