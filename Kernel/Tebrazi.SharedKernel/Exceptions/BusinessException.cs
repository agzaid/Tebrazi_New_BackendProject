namespace Tebrazi.SharedKernel.Exceptions;

/// <summary>A rule the domain refuses. Defaults to 400; modules may derive with another code.</summary>
public class BusinessException(string error, string message, int statusCode = 400)
    : AppException(error, message)
{
    public override int StatusCode { get; } = statusCode;
}
