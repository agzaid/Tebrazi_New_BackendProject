namespace Tebrazi.SharedKernel.Logging;

/// <summary>Structured logging facade so application code never references a logging provider.</summary>
public interface IAppLogger<out T>
{
    void Debug(string message, object? context = null);
    void Information(string message, object? context = null);
    void Warning(string message, object? context = null);
    void Error(string message, Exception? exception = null, object? context = null);
}
