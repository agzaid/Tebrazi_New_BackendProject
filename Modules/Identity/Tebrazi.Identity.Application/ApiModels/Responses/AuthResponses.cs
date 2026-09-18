using System.Text.Json.Serialization;

namespace Tebrazi.Identity.Application.ApiModels.Responses;

/// <summary>
/// The user object inside a login/register response.
///
/// <c>name</c> and <c>displayName</c> both carry the display name and both must stay: the React
/// code reads <c>user.name</c> in some places and <c>user.displayName</c> in others. Dropping
/// either breaks a screen.
/// </summary>
public sealed record AuthUserResponse(
    string Id,
    string? Email,
    string Name,
    string DisplayName,
    string Role,
    string UserType,
    string? Phone,
    [property: JsonPropertyName("profilePictureUrl")] string? ProfilePictureUrl,
    string? OrganizationId,
    string? OrganizationName);

/// <summary>Body of <c>POST /api/auth/login</c>.</summary>
public sealed record LoginResponse(string Token, AuthUserResponse User);

/// <summary>
/// Body of <c>POST /api/auth/register</c> (201). Carries the resolved plan alongside the user
/// so the signup screen can route straight to the right onboarding step.
/// </summary>
public sealed record RegisterResponse(
    string Token,
    AuthUserResponse User,
    string Plan,
    string PlanStatus);

/// <summary>
/// Body of <c>GET /api/auth/me</c>. Deliberately flat, not nested under <c>user</c> — the Node
/// route returns the fields at the top level and the client reads them there.
///
/// <c>organizationId</c> here is the user's CURRENT organization
/// (<c>users.current_organization_id</c>), which is not necessarily the one embedded in the
/// token: the token holds the primary membership at issue time.
/// </summary>
public sealed record CurrentUserResponse(
    string Id,
    string? Email,
    string Name,
    string DisplayName,
    string? Phone,
    [property: JsonPropertyName("profilePictureUrl")] string? ProfilePictureUrl,
    string Role,
    string UserType,
    string? OrganizationId);
