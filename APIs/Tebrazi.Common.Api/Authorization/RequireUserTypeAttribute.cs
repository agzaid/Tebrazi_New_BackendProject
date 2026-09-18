using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Common.Api.Authorization;

/// <summary>
/// Port of <c>server/src/middleware/requireUserType.js</c>. Restricts an action to specific
/// user types, and reproduces that middleware's response bodies exactly:
/// 401 <c>{ error: "Authentication required" }</c> when no userType claim is present,
/// 403 <c>{ error: "Access denied. This endpoint is restricted to: A, B" }</c> otherwise.
///
/// Apply AFTER authentication: it assumes the JWT has already been validated.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireUserTypeAttribute(params string[] allowedTypes) : Attribute, IAsyncAuthorizationFilter
{
    private readonly string[] _allowedTypes =
        allowedTypes.Length > 0
            ? allowedTypes
            : throw new ArgumentException("At least one user type must be allowed.", nameof(allowedTypes));

    /// <summary>The permitted user types. Read by the OpenAPI transformer to document the guard.</summary>
    public IReadOnlyList<string> AllowedTypes => _allowedTypes;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var currentUser = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        var userType = currentUser.UserType;

        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(userType))
        {
            context.Result = new ObjectResult(new { error = "Authentication required" })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return Task.CompletedTask;
        }

        if (!_allowedTypes.Contains(userType, StringComparer.Ordinal))
        {
            context.Result = new ObjectResult(new
            {
                error = $"Access denied. This endpoint is restricted to: {string.Join(", ", _allowedTypes)}"
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        return Task.CompletedTask;
    }
}
