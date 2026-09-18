namespace Tebrazi.Common.Api.Constants;

/// <summary>
/// The JWT payload keys the Node backend signs, verbatim:
/// <c>{ id, email, role, userType, displayName, organizationId }</c>.
/// Tokens must stay interchangeable between the two backends during the migration, so these
/// names cannot be changed on one side alone.
/// </summary>
public static class TebraziClaims
{
    public const string UserId         = "id";
    public const string Email          = "email";
    public const string Role           = "role";
    public const string UserType       = "userType";
    public const string DisplayName    = "displayName";
    public const string OrganizationId = "organizationId";
}
