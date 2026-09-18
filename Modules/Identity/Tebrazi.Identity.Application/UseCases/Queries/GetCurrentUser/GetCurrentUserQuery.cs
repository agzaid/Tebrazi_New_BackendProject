using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Queries.GetCurrentUser;

/// <summary><c>GET /api/auth/me</c>.</summary>
public sealed record GetCurrentUserQuery(string UserId) : IRequest<CurrentUserResponse>;

/// <summary>
/// Reads the user fresh from the database rather than echoing the token's claims, so a
/// deactivation or a rename takes effect without waiting for the 7-day token to expire.
/// </summary>
public sealed class GetCurrentUserHandler(IUserReadStore users)
    : IRequestHandler<GetCurrentUserQuery, CurrentUserResponse>
{
    public async Task<CurrentUserResponse> Handle(
        GetCurrentUserQuery request,
        CancellationToken cancellationToken = default)
    {
        var user = await users.GetByIdAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException("User not found");

        return new CurrentUserResponse(
            Id: user.Id,
            Email: user.Email,
            Name: user.DisplayName,
            DisplayName: user.DisplayName,
            Phone: user.Phone,
            ProfilePictureUrl: user.ProfilePictureUrl,
            Role: user.Role.ToString(),
            UserType: user.UserType.ToString(),
            // The CURRENT organization, which may differ from the one baked into the token.
            OrganizationId: user.CurrentOrganizationId);
    }
}
