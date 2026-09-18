namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The clinic the request is scoped to, resolved from the <c>X-Clinic-Id</c> header,
/// then the <c>clinicId</c> query string, matching the Node middleware's precedence.
/// </summary>
public interface IClinicContext
{
    string? ClinicId { get; }
}
