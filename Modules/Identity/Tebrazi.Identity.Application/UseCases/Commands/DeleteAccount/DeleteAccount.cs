using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.DeleteAccount;

/// <summary>
/// <c>DELETE /api/auth/delete-account</c> (auth.js:1252-1293). SOFT-delete: the row stays,
/// PII is anonymized, active goes false, every session row goes. Clinical records keyed by
/// user id keep working — physicians see "Deleted User", which is the point.
///
/// The confirmation gate is literal: Node compares the raw body string to
/// <c>"DELETE MY ACCOUNT"</c>. The body this port receives is bound by MVC, so an absent
/// <c>confirmation</c> arrives as null and fails the same check.
/// </summary>
public sealed record DeleteAccountCommand(string UserId, string? Confirmation) : IRequest<DeleteAccountResponse>;

public sealed record DeleteAccountResponse(string Message);

public sealed class DeleteAccountHandler(
    IIdentityDbContext dbContext,
    IUserWriteStore userWriter,
    ISessionStore sessions,
    Tebrazi.SharedKernel.Logging.IAppLogger<DeleteAccountHandler> logger)
    : IRequestHandler<DeleteAccountCommand, DeleteAccountResponse>
{
    public async Task<DeleteAccountResponse> Handle(DeleteAccountCommand request, CancellationToken ct = default)
    {
        // auth.js:1261 — Node compares the body value literally; the exact string IS the gate.
        if (request.Confirmation != "DELETE MY ACCOUNT")
            throw new BusinessException("Please type \"DELETE MY ACCOUNT\" to confirm",
                "Please type \"DELETE MY ACCOUNT\" to confirm", 400);

        var user = await userWriter.GetForUpdateAsync(request.UserId, ct);
        if (user is null)
            throw new BusinessException("Not Found", "User not found", 404);

        user.Deactivate();
        user.Rename("Deleted User");
        user.ReplaceEmail($"deleted_{request.UserId}@tebrazi.local");
        user.SetPhone(null);
        user.SetProfilePicture(null);

        // Node issues the session purge as a separate deleteMany with a .catch — non-fatal.
        var allSessions = await sessions.ListForUserIncludingExpiredAsync(request.UserId, ct);
        sessions.RemoveRange(allSessions);
        await dbContext.SaveChangesAsync(ct);

        logger.Information("Account soft-deleted — clinical records preserved", new { UserId = request.UserId });

        return new DeleteAccountResponse("Account deleted successfully");
    }
}
