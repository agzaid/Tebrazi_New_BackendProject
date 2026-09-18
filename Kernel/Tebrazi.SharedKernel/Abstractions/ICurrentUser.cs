namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The authenticated caller, read from the JWT. Claim names mirror the Node backend exactly
/// (<c>id</c>, <c>email</c>, <c>role</c>, <c>userType</c>, <c>displayName</c>, <c>organizationId</c>)
/// so tokens issued by either backend are interchangeable.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string? UserId { get; }
    string? Email { get; }
    string? DisplayName { get; }
    string? Role { get; }
    string? UserType { get; }
    string? OrganizationId { get; }
}
