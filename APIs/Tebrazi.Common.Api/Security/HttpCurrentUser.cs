using System.Security.Claims;
using Tebrazi.Common.Api.Constants;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Common.Api.Security;

/// <summary>
/// Reads the caller from the validated JWT. The claim names are the Node payload's own keys,
/// not the WS-Federation URIs — the token is issued by whichever backend is running and both
/// must read the same claims.
/// </summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public string? UserId => Claim(TebraziClaims.UserId) ?? Claim(ClaimTypes.NameIdentifier);

    public string? Email => Claim(TebraziClaims.Email) ?? Claim(ClaimTypes.Email);

    public string? DisplayName => Claim(TebraziClaims.DisplayName) ?? Claim(ClaimTypes.Name);

    public string? Role => Claim(TebraziClaims.Role);

    public string? UserType => Claim(TebraziClaims.UserType);

    public string? OrganizationId => Claim(TebraziClaims.OrganizationId);

    private string? Claim(string type)
    {
        var value = Principal?.FindFirst(type)?.Value;
        // A JSON null in the token payload deserializes to the literal "null" string.
        return string.IsNullOrEmpty(value) || value == "null" ? null : value;
    }
}
