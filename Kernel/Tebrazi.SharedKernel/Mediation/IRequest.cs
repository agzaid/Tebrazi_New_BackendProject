namespace Tebrazi.SharedKernel.Mediation;

/// <summary>Marker interface for any mediated request.</summary>
public interface IRequest;

/// <summary>A mediated request that produces <typeparamref name="TResult"/>.</summary>
public interface IRequest<out TResult> : IRequest;

/// <summary>Represents "no return value" for commands that produce nothing.</summary>
public readonly struct NoResult
{
    public static readonly NoResult Instance = new();
}
