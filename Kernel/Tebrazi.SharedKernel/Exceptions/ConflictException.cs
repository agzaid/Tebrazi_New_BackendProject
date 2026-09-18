namespace Tebrazi.SharedKernel.Exceptions;

/// <summary>409. Node emits <c>{ error: "Conflict", message: "..." }</c>.</summary>
public sealed class ConflictException(string message) : AppException("Conflict", message)
{
    public override int StatusCode => 409;
}
