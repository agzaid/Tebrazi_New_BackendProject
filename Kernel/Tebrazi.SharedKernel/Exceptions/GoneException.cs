namespace Tebrazi.SharedKernel.Exceptions;

/// <summary>410. Used for expired invitation and password-reset tokens.</summary>
public sealed class GoneException(string message) : AppException(message, message)
{
    public override int StatusCode => 410;
}
