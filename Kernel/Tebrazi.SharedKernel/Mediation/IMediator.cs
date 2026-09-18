namespace Tebrazi.SharedKernel.Mediation;

/// <summary>
/// In-house dispatcher. Deliberately not MediatR: see the KACCC convention this mirrors.
/// Do not add a MediatR package reference — <c>using MediatR;</c> would compile and resolve
/// a different <c>IMediator</c>, which reads as correct and is not.
/// </summary>
public interface IMediator : IPublisher
{
    Task<TResult> Send<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default);
}
