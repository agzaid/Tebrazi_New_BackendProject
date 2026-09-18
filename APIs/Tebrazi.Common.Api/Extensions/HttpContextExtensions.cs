using Tebrazi.Common.Api.Authorization;
using Tebrazi.SharedKernel.Authorization;

namespace Tebrazi.Common.Api.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    /// The <see cref="ClinicAccess"/> resolved by <see cref="RequireClinicPermissionAttribute"/>.
    /// Null on an action that carries no such attribute.
    /// </summary>
    public static ClinicAccess? ResolvedClinicAccess(this HttpContext context)
        => context.Items.TryGetValue(RequireClinicPermissionAttribute.ClinicAccessItemKey, out var value)
            && value is ClinicAccess access
            ? access
            : null;
}
