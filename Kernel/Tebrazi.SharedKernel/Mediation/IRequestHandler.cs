namespace Tebrazi.SharedKernel.Mediation;

/// <summary>Handles a request and returns a result.</summary>
public interface IRequestHandler<in TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    Task<TResult> Handle(TRequest request, CancellationToken cancellationToken = default);
}
