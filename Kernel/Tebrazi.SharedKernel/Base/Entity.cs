namespace Tebrazi.SharedKernel.Base;

public abstract class Entity<TKey>
{
    public TKey Id { get; protected set; } = default!;

    protected Entity() { }

    protected Entity(TKey id) => Id = id;

    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TKey> other) return false;
        if (ReferenceEquals(this, other)) return true;
        if (GetType() != other.GetType()) return false;
        return EqualityComparer<TKey>.Default.Equals(Id, other.Id);
    }

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
