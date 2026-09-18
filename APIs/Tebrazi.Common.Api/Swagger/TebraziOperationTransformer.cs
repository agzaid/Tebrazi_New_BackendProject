using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Tebrazi.Common.Api.Authorization;
using Tebrazi.Common.Api.Security;

namespace Tebrazi.Common.Api.Swagger;

/// <summary>
/// Per-operation metadata that the framework cannot infer:
/// which endpoints need a token, which read <c>X-Clinic-Id</c>, and what the error bodies
/// actually look like.
/// </summary>
public sealed class TebraziOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        var allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();
        var requiresAuth = !allowsAnonymous && metadata.OfType<IAuthorizeData>().Any();

        var userTypes = metadata.OfType<RequireUserTypeAttribute>().ToArray();
        var clinicPermissions = metadata.OfType<RequireClinicPermissionAttribute>().ToArray();

        // Either attribute implies authentication even without a separate [Authorize].
        if (userTypes.Length > 0 || clinicPermissions.Length > 0)
            requiresAuth = true;

        if (requiresAuth)
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(TebraziDocumentTransformer.BearerSchemeName)] = []
                }
            ];

            AddResponse(operation, "401", "No token provided, or the token is invalid or expired.");
        }

        if (clinicPermissions.Length > 0)
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = HttpClinicContext.HeaderName,
                In = ParameterLocation.Header,
                Required = false,
                Description =
                    "The clinic this request applies to. The React client sends it automatically " +
                    "from the active clinic. Falls back to a `clinicId` query parameter.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });

            AddResponse(operation, "403",
                "Not staff at this clinic, or the role lacks the required permission.");
        }

        // Surface the guards in the description, so the constraint is visible without opening
        // the source.
        var notes = new List<string>();

        foreach (var attribute in userTypes)
            notes.Add($"**Restricted to user types:** {string.Join(", ", attribute.AllowedTypes)}.");

        foreach (var attribute in clinicPermissions)
            notes.Add($"**Requires clinic permission:** `{attribute.Permission}` " +
                      "(physicians bypass this check).");

        if (notes.Count > 0)
        {
            operation.Description = string.IsNullOrWhiteSpace(operation.Description)
                ? string.Join("\n\n", notes)
                : $"{operation.Description}\n\n{string.Join("\n\n", notes)}";
        }

        return Task.CompletedTask;
    }

    /// <summary>Adds a documented status code, leaving an existing entry alone.</summary>
    private static void AddResponse(OpenApiOperation operation, string statusCode, string description)
    {
        operation.Responses ??= [];

        if (operation.Responses.ContainsKey(statusCode)) return;

        operation.Responses[statusCode] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["error"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["message"] = new OpenApiSchema { Type = JsonSchemaType.String }
                        }
                    }
                }
            }
        };
    }
}
