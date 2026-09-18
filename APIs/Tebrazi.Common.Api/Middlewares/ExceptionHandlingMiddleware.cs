using System.Text.Json;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;

namespace Tebrazi.Common.Api.Middlewares;

/// <summary>
/// Turns exceptions into the exact JSON bodies the Node backend produced, so the React error
/// handling (which reads <c>err.response.data.error</c> and <c>.message</c>) keeps working.
///
/// Unexpected exceptions log their full detail server-side and return a generic body: the
/// stack trace is exposed to the client in Development only, matching the Node handler.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context, IAppLogger<ExceptionHandlingMiddleware> logger)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            // Expected, modelled failures. Logged at warning — they are not defects.
            logger.Warning(ex.Message, new
            {
                Type = ex.GetType().Name,
                ex.StatusCode,
                context.Request.Method,
                Path = context.Request.Path.Value
            });

            await WriteAsync(context, ex.StatusCode, BuildBody(ex));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client hung up. Nothing to report and nothing to write.
            logger.Debug("Request aborted by client", new { Path = context.Request.Path.Value });
        }
        catch (Exception ex)
        {
            logger.Error("Unhandled exception", ex, new
            {
                context.Request.Method,
                Path = context.Request.Path.Value
            });

            var body = new Dictionary<string, object?>
            {
                ["error"] = "Internal Server Error",
                ["message"] = "An unexpected error occurred"
            };

            if (environment.IsDevelopment())
            {
                body["message"] = ex.Message;
                body["stack"] = ex.StackTrace;
            }

            await WriteAsync(context, StatusCodes.Status500InternalServerError, body);
        }
    }

    private static Dictionary<string, object?> BuildBody(AppException ex)
    {
        var body = new Dictionary<string, object?> { ["error"] = ex.Error };

        // Node omits `message` when it equals the error label (e.g. permission failures send
        // a bare `{ error }`); reproducing that keeps the two bodies byte-comparable.
        if (!string.Equals(ex.Error, ex.Message, StringComparison.Ordinal))
            body["message"] = ex.Message;

        if (ex is ValidationException { Details: not null } validation)
            body["details"] = validation.Details;

        if (ex.AdditionalFields is { } extra)
        {
            foreach (var (key, value) in extra)
                body[key] = value;
        }

        return body;
    }

    private static async Task WriteAsync(HttpContext context, int statusCode, object body)
    {
        if (context.Response.HasStarted)
            return; // Headers are already on the wire; overwriting them would corrupt the response.

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(body, SerializerOptions),
            context.RequestAborted);
    }
}
