using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.Documents.Application.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.UploadProfilePicture;

/// <summary>
/// <c>POST /api/auth/profile-picture</c> (auth.js:1550-1588).
///
/// Node stores under <c>/uploads/profile-pictures/</c>, deletes the OLD file when the URL it is
/// replacing still points into that directory, and returns <c>{"profilePictureUrl": ...}</c>.
/// The old-file deletion goes through the storage port's Delete, which treats a missing file as
/// done — the same semantics as Node's <c>fs.existsSync</c> guard.
/// </summary>
public sealed record UploadProfilePictureCommand(
    string UserId,
    string FileName,
    string ContentType,
    Stream Content) : IRequest<UploadProfilePictureResponse>;

public sealed record UploadProfilePictureResponse(string ProfilePictureUrl);

public sealed class UploadProfilePictureHandler(
    IIdentityDbContext dbContext,
    IUserWriteStore userWriter,
    IFileStorageService storage,
    IAppLogger<UploadProfilePictureHandler> logger)
    : IRequestHandler<UploadProfilePictureCommand, UploadProfilePictureResponse>
{
    public async Task<UploadProfilePictureResponse> Handle(UploadProfilePictureCommand request, CancellationToken ct = default)
    {
        var user = await userWriter.GetForUpdateAsync(request.UserId, ct)
            ?? throw new BusinessException("User not found", "User not found", 404);

        // A collision in storage resolves the name itself; the returned path is what the
        // database column stores and what the client sees.
        var storedPath = await storage.SaveAsync(
            request.Content,
            $"profile-pictures/{request.UserId}/{request.FileName}",
            request.ContentType,
            ct);

        // Node deletes the previous file when it lived in the profile-pictures tree (auth.js:1572-1576).
        if (user.ProfilePictureUrl is not null && user.ProfilePictureUrl.StartsWith("/uploads/profile-pictures/"))
        {
            var oldRelative = user.ProfilePictureUrl["/uploads/".Length..];
            if (await storage.ExistsAsync(oldRelative, ct))
                await storage.DeleteAsync(oldRelative, ct);
        }

        user.SetProfilePicture(storage.GetPublicUrl(storedPath));
        await dbContext.SaveChangesAsync(ct);

        logger.Information("Profile picture uploaded", new { UserId = request.UserId, Path = storedPath });

        return new UploadProfilePictureResponse(storage.GetPublicUrl(storedPath));
    }
}
