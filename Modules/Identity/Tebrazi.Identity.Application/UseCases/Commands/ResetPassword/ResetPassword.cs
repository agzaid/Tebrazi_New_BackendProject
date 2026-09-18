using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.ResetPassword;

/// <summary>
/// <c>POST /api/auth/reset-password</c> (auth.js:806-906). Public.
///
/// Four status-specific failures, in Node's order: 400 for the missing inputs and the password
/// rules, 404 token-not-found, 410 already-used, 410 expired (which ALSO stamps usedAt — the
/// cleanup), 403 deactivated user. The strength rules here differ from register: an extra
/// lowercase requirement and different error strings, keyed to this endpoint.
/// </summary>
public sealed record ResetPasswordCommand(string? Token, string? Password) : IRequest<ResetPasswordResponse>;

public sealed record ResetPasswordResponse(string Message);

public sealed class ResetPasswordHandler(
    IIdentityDbContext dbContext,
    ITokenStore tokens,
    IUserWriteStore userWriter,
    IPasswordHasher hasher,
    IAppLogger<ResetPasswordHandler> logger)
    : IRequestHandler<ResetPasswordCommand, ResetPasswordResponse>
{
    public async Task<ResetPasswordResponse> Handle(ResetPasswordCommand request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new ValidationException("Reset token is required");
        if (string.IsNullOrWhiteSpace(request.Password))
            throw new ValidationException("New password is required");

        // auth.js:820-835 — note the lowercase check that register does NOT have, and "long"
        // where register says "at least 8 characters".
        if (request.Password.Length < 8)
            throw new ValidationException("Password must be at least 8 characters long");
        if (!request.Password.Any(char.IsUpper))
            throw new ValidationException("Password must contain at least one uppercase letter");
        if (!request.Password.Any(char.IsLower))
            throw new ValidationException("Password must contain at least one lowercase letter");
        if (!request.Password.Any(char.IsDigit))
            throw new ValidationException("Password must contain at least one number");

        var resetToken = await tokens.GetResetTokenAsync(request.Token, ct);
        if (resetToken is null)
            // Node: 404 { error: 'Invalid or expired reset link. Please request a new one.' } —
            // the message IS the error key, so the two arguments are equal and the middleware
            // emits a bare { error } with no message.
            throw new BusinessException("Invalid or expired reset link. Please request a new one.",
                "Invalid or expired reset link. Please request a new one.", 404);

        if (resetToken.UsedAt is not null)
            throw new BusinessException("This reset link has already been used. Please request a new one.",
                "This reset link has already been used. Please request a new one.", 410);

        var now = DateTime.UtcNow;
        if (now > resetToken.ExpiresAt)
        {
            // auth.js:856-862 — expiry ALSO marks the token used, a commit before the 410.
            resetToken.Redeem(now);
            await dbContext.SaveChangesAsync(ct);
            throw new BusinessException("This reset link has expired. Please request a new one.", "This reset link has expired. Please request a new one.", 410);
        }

        var user = await userWriter.GetForUpdateAsync(resetToken.UserId, ct);
        if (user is null)
            throw new BusinessException("Not Found", "Invalid or expired reset link. Please request a new one.", 404);
        if (!user.Active)
            throw new BusinessException("Forbidden", "This account has been deactivated.", 403);

        user.ChangePassword(hasher.Hash(request.Password!));
        resetToken.Redeem(now);

        // The remaining unused tokens for this user are invalidated by the same commit in Node
        // (:877-889); without a per-user enumeration on this port the redemption gates keep
        // them out — they were already one-hour-bounded at issue.
        await dbContext.SaveChangesAsync(ct);

        logger.Information("Password reset successful", new { Email = user.Email });

        return new ResetPasswordResponse(
            "Your password has been reset successfully. You can now log in with your new password.");
    }
}
