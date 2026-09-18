using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Authorization;

namespace Tebrazi.Common.Api.Authorization;

/// <summary>
/// Port of <c>requireClinicAccess(permission)</c> from
/// <c>server/src/middleware/clinicPermission.js</c>. Resolves the caller's standing at the
/// active clinic and requires one permission key from <see cref="ClinicPermission"/>.
///
/// Physicians bypass the check. Staff are measured against the preset for their role with any
/// per-staff overrides applied. On success the resolved access is stashed in
/// <c>HttpContext.Items</c> so the action can read it without a second database round trip.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireClinicPermissionAttribute(string permission) : Attribute, IAsyncAuthorizationFilter
{
    /// <summary>Key under which the resolved <see cref="ClinicAccess"/> is stored.</summary>
    public const string ClinicAccessItemKey = "Tebrazi.ClinicAccess";

    /// <summary>The required permission key. Read by the OpenAPI transformer to document the guard.</summary>
    public string Permission { get; } = permission;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var currentUser = services.GetRequiredService<ICurrentUser>();

        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.UserId))
        {
            context.Result = new ObjectResult(new { error = "Authentication required" })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        var clinicContext = services.GetRequiredService<IClinicContext>();
        var evaluator = services.GetRequiredService<IClinicAccessEvaluator>();

        var access = await evaluator.EvaluateAsync(
            currentUser.UserId,
            clinicContext.ClinicId,
            context.HttpContext.RequestAborted);

        if (!access.IsGranted)
        {
            context.Result = new ObjectResult(new { error = "You are not staff at this clinic" })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        if (!access.Grants(permission))
        {
            context.Result = new ObjectResult(new
            {
                error = $"Insufficient permissions: {permission} not allowed for {access.Role}",
                role = access.Role
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        context.HttpContext.Items[ClinicAccessItemKey] = access;
    }
}
