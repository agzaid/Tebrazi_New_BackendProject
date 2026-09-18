namespace Tebrazi.SharedKernel.Exceptions;

/// <summary>404.</summary>
public sealed class NotFoundException(string message) : AppException(message, message)
{
    public override int StatusCode => 404;
}
