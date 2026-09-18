using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Tebrazi.Common.Api.Swagger;

/// <summary>
/// Sets the document's title and description, and declares the JWT bearer scheme so the Swagger
/// UI shows an Authorize button.
/// </summary>
public sealed class TebraziDocumentTransformer : IOpenApiDocumentTransformer
{
    /// <summary>Referenced by name from each operation's security requirement.</summary>
    public const string BearerSchemeName = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Tebrazi API",
            Version = "v1",
            Description =
                "Physician-first healthcare platform API (.NET port of the Node backend).\n\n" +
                "**Responses are unwrapped.** Endpoints return their payload at the top level and " +
                "errors as `{ \"error\": \"...\", \"message\": \"...\" }`, matching the Express " +
                "backend exactly so the React client can switch over by changing `VITE_API_URL`.\n\n" +
                "**To call a protected endpoint:** POST `/api/auth/login`, copy the `token` from " +
                "the response, click **Authorize**, and paste it. Do not prefix it with `Bearer` — " +
                "that is added for you.\n\n" +
                "**Clinic-scoped endpoints** additionally read the `X-Clinic-Id` header, which " +
                "selects which clinic the request applies to."
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[BearerSchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description =
                "HS256 token from `/api/auth/login`. Carries the claims " +
                "`id`, `email`, `role`, `userType`, `displayName`, `organizationId`."
        };

        return Task.CompletedTask;
    }
}
