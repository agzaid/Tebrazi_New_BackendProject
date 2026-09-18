namespace Tebrazi.SharedKernel.Exceptions;

/// <summary>400. Node emits <c>{ error: "Validation failed", message: "..." }</c>.</summary>
public sealed class ValidationException : AppException
{
    public override int StatusCode => 400;
    public IReadOnlyDictionary<string, string[]>? Details { get; }

    public ValidationException(string message)
        : base("Validation failed", message) { }

    public ValidationException(string message, IReadOnlyDictionary<string, string[]> details)
        : base("Validation failed", message) => Details = details;
}
