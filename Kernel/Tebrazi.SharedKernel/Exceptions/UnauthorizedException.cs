namespace Tebrazi.SharedKernel.Exceptions;

/// <summary>401.</summary>
public sealed class UnauthorizedException(string message = "Invalid credentials")
    : AppException("Unauthorized", message)
{
    public override int StatusCode => 401;
}
