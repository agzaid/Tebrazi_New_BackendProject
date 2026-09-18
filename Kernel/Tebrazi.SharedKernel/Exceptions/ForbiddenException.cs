namespace Tebrazi.SharedKernel.Exceptions;

/// <summary>403. Node emits a bare <c>{ error: "..." }</c> for permission failures.</summary>
public sealed class ForbiddenException(string message) : AppException(message, message)
{
    public override int StatusCode => 403;
}
