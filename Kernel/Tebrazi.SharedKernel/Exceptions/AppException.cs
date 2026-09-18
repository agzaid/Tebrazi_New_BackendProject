namespace Tebrazi.SharedKernel.Exceptions;

/// <summary>
/// Base for every exception the API translates into a response.
/// <see cref="Error"/> becomes the JSON <c>error</c> field and <see cref="Message"/> the
/// <c>message</c> field, matching the Node backend's error body exactly.
/// </summary>
public abstract class AppException : Exception
{
    public string Error { get; }
    public abstract int StatusCode { get; }

    /// <summary>
    /// Extra top-level fields to merge into the JSON body. A few Node endpoints return more than
    /// <c>error</c> and <c>message</c> — the login route's <c>redirectRole</c>, the clinic guard's
    /// <c>role</c> — and the client branches on them.
    /// </summary>
    public virtual IReadOnlyDictionary<string, object?>? AdditionalFields => null;

    protected AppException(string error, string message) : base(message) => Error = error;
}
