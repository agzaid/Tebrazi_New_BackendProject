using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Tebrazi.Appointments.Infrastructure.DependencyInjection;
using Tebrazi.Clinics.Infrastructure.DependencyInjection;
using Tebrazi.Connections.Infrastructure.DependencyInjection;
using Tebrazi.Common.Api.Middlewares;
using Tebrazi.Common.Api.Security;
using Tebrazi.Common.Api.Serialization;
using Tebrazi.Common.Api.Swagger;
using Tebrazi.Documents.Application.Configuration;
using Tebrazi.Documents.Infrastructure.DependencyInjection;
using Tebrazi.Identity.Infrastructure.Configuration;
using Tebrazi.Notifications.Infrastructure.DependencyInjection;
using Tebrazi.Patients.Infrastructure.DependencyInjection;
using Tebrazi.Prescriptions.Infrastructure.DependencyInjection;
using Tebrazi.Identity.Infrastructure.DependencyInjection;
using Tebrazi.Infrastructure.Shared.DependencyInjection;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.Visits.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// ── Logging ──────────────────────────────────────────────────────────────────
// Serilog replaces the Node backend's pino: console for local work, a rolling daily file for
// anything that needs to be read after the fact.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName());

var configuration = builder.Configuration;
var isDevelopment = builder.Environment.IsDevelopment();

// ── Serialization ────────────────────────────────────────────────────────────
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        // camelCase and null-omission are what the React client already receives from Express.
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        // Enums cross the wire as their names ("PHYSICIAN"), never as integers.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

        // Node emits "2026-09-10T00:00:00.000Z" for every timestamp; System.Text.Json drops the
        // zero fractional digits and emits seven of them when they are not zero. Neither is what
        // the client already parses off Express.
        options.JsonSerializerOptions.Converters.Add(new NodeDateTimeJsonConverter());

        // Express emits UTF-8 verbatim; System.Text.Json's DEFAULT encoder escapes every
        // non-ASCII character plus & + < > ' " as \uXXXX. Without this the Arabic name fields,
        // the specialty-template emoji, and every ampersand in a clinic name ship as escapes —
        // functionally equivalent after JSON.parse, but not byte-identical, which is the whole
        // premise of this port. UnicodeRanges.All is NOT sufficient: it still escapes & + <.
        options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

// A binding failure must produce the Node error shape, not ASP.NET's ProblemDetails, or the
// client's `err.response.data.error` read comes back undefined.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        // An error added through ModelStateDictionary.TryAddModelException — the path a
        // ValueProviderException takes when reading the form or body throws — builds a
        // ModelError(Exception) whose ErrorMessage is string.EMPTY, not null. Reading ErrorMessage
        // alone therefore yields "" and never reaches the fallback, so the client is handed
        // {"error":"Validation failed","message":""} with the actual reason discarded. Fall back to
        // the exception's own message, then filter blanks so the literal fallback is reachable.
        var firstError = context.ModelState
            .SelectMany(kv => kv.Value?.Errors ?? [])
            .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.Exception?.Message : e.ErrorMessage)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "Invalid request body";

        return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(new
        {
            error = "Validation failed",
            message = firstError
        });
    };
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IClinicContext, HttpClinicContext>();

// ── Modules ──────────────────────────────────────────────────────────────────
builder.Services.AddInfrastructureShared();
builder.Services.AddIdentityModule(configuration, isDevelopment);
builder.Services.AddClinicsModule(configuration, isDevelopment);
builder.Services.AddPatientsModule(configuration, isDevelopment);
// Registered before the three clinical modules because they all raise notifications through
// its published port.
builder.Services.AddNotificationsModule(configuration, isDevelopment);
builder.Services.AddVisitsModule(configuration, isDevelopment);
builder.Services.AddAppointmentsModule(configuration, isDevelopment);
builder.Services.AddPrescriptionsModule(configuration, isDevelopment);
// The social graph. Registered after the clinical modules because it reads their published
// ports, and before Documents for no reason beyond keeping the module list in porting order.
builder.Services.AddConnectionsModule(configuration, isDevelopment);
builder.Services.AddDocumentsModule(configuration);

// ── Authentication ───────────────────────────────────────────────────────────
var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("The 'Jwt' configuration section is missing.");
jwtOptions.Validate();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),

            // Node signs with no `iss` or `aud`. Validating either would reject every token the
            // Node backend issued, so both stay off unless explicitly configured.
            ValidateIssuer = !string.IsNullOrWhiteSpace(jwtOptions.Issuer),
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = !string.IsNullOrWhiteSpace(jwtOptions.Audience),
            ValidAudience = jwtOptions.Audience,

            ValidateLifetime = true,
            // Default is 5 minutes of leeway; an expired token should be expired.
            ClockSkew = TimeSpan.Zero,

            // Read the Node payload's own key names rather than the WS-Federation URIs.
            NameClaimType = "displayName",
            RoleClaimType = "role"
        };

        // A rejected token must return the Node body, not the default empty 401.
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();

                if (context.Response.HasStarted) return;

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";

                var error = string.IsNullOrEmpty(context.Error) ? "No token provided" : "Invalid token";
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error }),
                    context.HttpContext.RequestAborted);
            }
        };
    });

builder.Services.AddAuthorization();

// ── CORS ─────────────────────────────────────────────────────────────────────
// X-Clinic-Id must be in the allowed headers: the axios interceptor sends it on every request
// once a clinic is active, and omitting it fails the preflight for the whole app.
const string CorsPolicy = "TebraziClient";
var corsOrigins = (configuration["Cors:Origins"] ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
    .WithOrigins(corsOrigins)
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
    .WithHeaders("Content-Type", "Authorization", "X-Clinic-Id")
    .AllowCredentials()));

// ── Uploads ──────────────────────────────────────────────────────────────────
var storageOptions = configuration.GetSection(FileStorageOptions.SectionName).Get<FileStorageOptions>()
    ?? new FileStorageOptions();

builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = storageOptions.MaxFileSizeBytes);

builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddOpenApi(OpenApiExtensions.DocumentName, options => options.AddTebraziTransformers());

var app = builder.Build();

// ── Pipeline ─────────────────────────────────────────────────────────────────
// Order matters: exception handling must be outermost so it catches everything below it.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSerilogRequestLogging();
app.UseResponseCompression();

// OpenAPI document + Swagger UI at /swagger. On in Development, or wherever Swagger:Enabled is set.
app.UseTebraziOpenApi();

if (!isDevelopment)
    app.UseHsts();

app.UseCors(CorsPolicy);

// Uploads are served at BOTH paths, as the Node backend did — /api/files exists for the
// Netlify proxy and stored URLs in the database use it.
var uploadsRoot = Path.GetFullPath(storageOptions.RootPath);
Directory.CreateDirectory(uploadsRoot);
var uploadsProvider = new PhysicalFileProvider(uploadsRoot);

app.UseStaticFiles(new StaticFileOptions { FileProvider = uploadsProvider, RequestPath = "/uploads" });
app.UseStaticFiles(new StaticFileOptions { FileProvider = uploadsProvider, RequestPath = "/api/files" });

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Anything unmatched answers in the Node 404 shape rather than an empty body.
app.MapFallback(context =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    context.Response.ContentType = "application/json; charset=utf-8";
    return context.Response.WriteAsync(JsonSerializer.Serialize(new
    {
        error = "Not Found",
        message = $"Route {context.Request.Method} {context.Request.Path} not found"
    }));
});

try
{
    Log.Information("Starting Tebrazi API");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Tebrazi API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so an integration-test host can reference this entry point.</summary>
public partial class Program;
