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
public sealed class HealthController(IdentityDbContext dbContext)
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
            // Node sends `new Date().toISOString()` — three fractional digits. Handing the
            // serializer a DateTime rather than a pre-formatted string routes it through
            // NodeDateTimeJsonConverter; ToString("O") printed seven digits.
            timestamp = DateTime.UtcNow,
            service = "Tebrazi API",
            version = typeof(HealthController).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            uptime = $"{(int)(DateTime.UtcNow - StartedAtUtc).TotalSeconds}s",
            db = dbStatus,
            // Key order and key set match src/index.js:144-168 exactly — the contract-diff
            // harness diffs them (missing `redis` / extra `environment` were breaking
            // findings on 2026-09-19). No Redis client in this port, so the value is always
            // Node's "not configured" branch.
            redis = "not configured",
            latencyMs = stopwatch.ElapsedMilliseconds,
            memory = new
            {
                rss = $"{process.WorkingSet64 / 1048576}MB",
                heap = $"{GC.GetTotalMemory(forceFullCollection: false) / 1048576}MB"
            }
        });
    }

    /// <summary>
    /// <c>GET /api</c> — the service banner the Node app served at the API root
    /// (src/index.js:279-293). Key set and order match Node's literal; the dashboards/reports/
    /// documents entries name route families whose Node modules exist even where the port has
    /// not landed them — the banner advertises the surface, it does not implement it.
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
            auth = "/api/auth/*",
            dashboards = "/api/dashboards/*",
            reports = "/api/reports/*",
            documents = "/api/documents/*"
        }
    });
}
