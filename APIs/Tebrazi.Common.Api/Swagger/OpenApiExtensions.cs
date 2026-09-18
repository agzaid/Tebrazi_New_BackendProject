using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;

namespace Tebrazi.Common.Api.Swagger;

public static class OpenApiExtensions
{
    /// <summary>Document name, and therefore the path: <c>/openapi/v1.json</c>.</summary>
    public const string DocumentName = "v1";

    /// <summary>
    /// Adds the Tebrazi transformers — a JWT bearer scheme, the <c>X-Clinic-Id</c> header, and
    /// Node-shaped error responses — to an OpenAPI document.
    ///
    /// Call it from inside the host's own <c>AddOpenApi</c>:
    /// <code>
    /// builder.Services.AddOpenApi(OpenApiExtensions.DocumentName, o => o.AddTebraziTransformers());
    /// </code>
    ///
    /// <c>AddOpenApi</c> is deliberately NOT wrapped here. The XML-comment source generator hooks
    /// the <c>AddOpenApi</c> CALL SITE and can only see XML docs visible from that assembly, so
    /// calling it inside this shared library would leave every controller summary blank.
    ///
    /// Uses the built-in <c>Microsoft.AspNetCore.OpenApi</c> generator; Swashbuckle is present
    /// only for its UI middleware, not for document generation.
    /// </summary>
    public static OpenApiOptions AddTebraziTransformers(this OpenApiOptions options)
    {
        options.AddDocumentTransformer<TebraziDocumentTransformer>();
        options.AddOperationTransformer<TebraziOperationTransformer>();
        return options;
    }

    /// <summary>
    /// Serves the OpenAPI document and the Swagger UI at <c>/swagger</c>.
    ///
    /// Enabled in Development, or anywhere <c>Swagger:Enabled</c> is true. It is OFF by default
    /// outside Development on purpose: the document is a complete map of the API surface,
    /// including which endpoints are unauthenticated, and that is not something to publish
    /// without deciding to.
    /// </summary>
    public static WebApplication UseTebraziOpenApi(this WebApplication app)
    {
        var explicitlyEnabled = app.Configuration.GetValue<bool?>("Swagger:Enabled");
        var enabled = explicitlyEnabled ?? app.Environment.IsDevelopment();

        if (!enabled) return app;

        app.MapOpenApi();

        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint($"/openapi/{DocumentName}.json", "Tebrazi API v1");
            options.RoutePrefix = "swagger";
            options.DocumentTitle = "Tebrazi API";

            // Controllers collapsed on arrival: with 386 endpoints coming, an expanded default
            // makes the page unusable.
            options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
            options.DefaultModelsExpandDepth(0);
            options.EnableFilter();
            options.EnableTryItOutByDefault();
            options.DisplayRequestDuration();

            // Keeps the bearer token across page reloads so you are not re-pasting it all day.
            options.EnablePersistAuthorization();
        });

        return app;
    }
}
