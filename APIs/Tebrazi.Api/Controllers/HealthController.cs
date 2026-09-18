using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tebrazi.Identity.Persistence;

namespace Tebrazi.Api.Controllers;

/// <summary>
/// <c>GET /api/health</c> — the port of the Node health endpoint, same body and same status
/// semantics: 200 when the database answers, 503 when it does not, so an orchestrator's probe
/// behaves identically against either backend.
/// </summary>
[ApiController]
[Route("api")]
public sealed class HealthController(IdentityDbContext dbContext, IHostEnvironment environment)
    : ControllerBase
{
    private static readonly DateTime StartedAtUtc = DateTime.UtcNow;

    [HttpGet("health")]
    [AllowAnonymous]
    public async Task<IActionResult> Health()
    {
        var stopwatch = Stopwatch.StartNew();

        string dbStatus;
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("SELECT 1", HttpContext.RequestAborted);
            dbStatus = "connected";
        }
        catch (Exception ex)
        {
            // The message is echoed exactly as the Node version did. In a hardened deployment
            // this endpoint should be restricted, since the text can name the server.
            dbStatus = "error: " + ex.Message;
        }

        var isHealthy = dbStatus == "connected";
        var process = Process.GetCurrentProcess();

        return StatusCode(isHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, new
        {
            status = isHealthy ? "ok" : "degraded",
            timestamp = DateTime.UtcNow.ToString("O"),
            service = "Tebrazi API (.NET)",
            version = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            environment = environment.EnvironmentName,
            uptime = $"{(int)(DateTime.UtcNow - StartedAtUtc).TotalSeconds}s",
            db = dbStatus,
            latencyMs = stopwatch.ElapsedMilliseconds,
            memory = new
            {
                rss = $"{process.WorkingSet64 / 1048576}MB",
                heap = $"{GC.GetTotalMemory(forceFullCollection: false) / 1048576}MB"
            }
        });
    }

    /// <summary>
    /// <c>GET /api</c> — the service banner the Node app served at the API root.
    /// </summary>
    [HttpGet("")]
    [AllowAnonymous]
    public IActionResult Index() => Ok(new
    {
        message = "Tebrazi API",
        version = "1.0.0",
        endpoints = new
        {
            health = "/api/health",
            auth = "/api/auth/*"
        }
    });
}
