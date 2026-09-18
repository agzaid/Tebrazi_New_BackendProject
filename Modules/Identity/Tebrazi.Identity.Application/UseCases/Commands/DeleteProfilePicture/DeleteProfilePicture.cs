using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.Documents.Application.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.DeleteProfilePicture;

/// <summary>
/// <c>DELETE /api/auth/profile-picture</c> (auth.js:1590-1605) — clears the column and removes
/// the file when it lived under the profile-pictures tree. 200 whether or not there was one.
/// </summary>
public sealed record DeleteProfilePictureCommand(string UserId) : IRequest<DeleteProfilePictureResponse>;

public sealed record DeleteProfilePictureResponse(string Message);

public sealed class DeleteProfilePictureHandler(
    IIdentityDbContext dbContext,
    IUserWriteStore userWriter,
    IFileStorageService storage,
    IAppLogger<DeleteProfilePictureHandler> logger)
    : IRequestHandler<DeleteProfilePictureCommand, DeleteProfilePictureResponse>
{
    public async Task<DeleteProfilePictureResponse> Handle(DeleteProfilePictureCommand request, CancellationToken ct = default)
    {
        var user = await userWriter.GetForUpdateAsync(request.UserId, ct)
            ?? throw new BusinessException("User not found", "User not found", 404);

        if (user.ProfilePictureUrl is not null && user.ProfilePictureUrl.StartsWith("/uploads/profile-pictures/"))
        {
            var relative = user.ProfilePictureUrl["/uploads/".Length..];
            if (await storage.ExistsAsync(relative, ct))
                await storage.DeleteAsync(relative, ct);
        }

        user.SetProfilePicture(null);
        await dbContext.SaveChangesAsync(ct);

        logger.Information("Profile picture removed", new { UserId = request.UserId });

        return new DeleteProfilePictureResponse("Profile picture removed");
    }
}
