using Microsoft.Extensions.Logging;
using Tebrazi.SharedKernel.Logging;

namespace Tebrazi.Infrastructure.Shared.Logging;

/// <summary>
/// Adapts <see cref="ILogger{T}"/> to the kernel's facade. The optional context object is
/// attached as a logging scope, so structured sinks keep it as fields rather than flattening
/// it into the message text.
/// </summary>
public sealed class AppLogger<T>(ILogger<T> logger) : IAppLogger<T>
{
    public void Debug(string message, object? context = null)
        => Write(LogLevel.Debug, message, null, context);

    public void Information(string message, object? context = null)
        => Write(LogLevel.Information, message, null, context);

    public void Warning(string message, object? context = null)
        => Write(LogLevel.Warning, message, null, context);

    public void Error(string message, Exception? exception = null, object? context = null)
        => Write(LogLevel.Error, message, exception, context);

    private void Write(LogLevel level, string message, Exception? exception, object? context)
    {
        if (!logger.IsEnabled(level)) return;

        if (context is null)
        {
            logger.Log(level, exception, "{Message}", message);
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object?> { ["Context"] = context }))
            logger.Log(level, exception, "{Message}", message);
    }
}
