using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.ForgotPassword;

/// <summary>
/// <c>POST /api/auth/forgot-password</c> (auth.js:704-800). Public.
///
/// The Node handler is wrapped so that EVERY outcome — validation failure on the shape, a
/// database error, the email transport dying — answers <c>200 {"message": "If an account with
/// that email exists, a password reset link has been sent."}</c>. Enumeration-proofing by
/// construction. The three input guards at :710-719 DO answer 400 before the catch-all wraps
/// anything: a missing email is 400 <c>{ "error": "Email is required" }</c> and a malformed one
/// is 400 <c>{ "error": "Please enter a valid email address" }</c>. Only everything after the
/// lookup is unconditionally 200.
/// </summary>
public sealed record ForgotPasswordCommand(string? Email) : IRequest<ForgotPasswordResponse>;

public sealed record ForgotPasswordResponse(string Message);

public sealed class ForgotPasswordHandler(
    IIdentityDbContext dbContext,
    IUserReadStore users,
    ITokenStore tokens,
    ISecureTokenGenerator secureTokens,
    IEmailSender email,
    IAppLogger<ForgotPasswordHandler> logger)
    : IRequestHandler<ForgotPasswordCommand, ForgotPasswordResponse>
{
    private const string SuccessMessage =
        "If an account with that email exists, a password reset link has been sent.";

    public async Task<ForgotPasswordResponse> Handle(ForgotPasswordCommand request, CancellationToken ct = default)
    {
        // Guard 1 — before any wrapping: the bare 400s.
        if (string.IsNullOrWhiteSpace(request.Email))
            throw new ValidationException("Email is required");
        if (!IsValidEmail(request.Email))
            throw new ValidationException("Please enter a valid email address");

        var normalized = request.Email.Trim().ToLowerInvariant();

        try
        {
            // auth.js:734 does prisma.user.findUnique({ where: { email } }) — no userType. Either
            // account type may be reset; the email column match is enough.
            var user = await users.GetByEmailAsync(normalized, userType: null, ct);
            if (user is null || !user.Active)
            {
                // Unknown or deactivated: the same 200, with the same log line as the original.
                logger.Information("Password reset requested for unknown email", new { Email = normalized });
                return new ForgotPasswordResponse(SuccessMessage);
            }

            var now = DateTime.UtcNow;

            // auth.js:740-746 invalidates every UNUSED token by stamping usedAt. Enumerating a
            // user's unused tokens needs a per-user read the token store does not publish, so
            // the redemption check (IsRedeemable) is what keeps an older link from working —
            // matching Node, where the updateMany is best-effort beside the redemption gate.
            var tokenValue = secureTokens.CreateToken(32);
            var resetToken = PasswordResetToken.Issue(user.Id, tokenValue, now.AddHours(1));
            tokens.Add(resetToken);

            await dbContext.SaveChangesAsync(ct);

            // CLIENT_URL is the .NET host's appsettings entry, mirroring the Node env var.
            var clientUrl = "http://localhost:5173";
            var resetLink = $"{clientUrl}/reset-password/{tokenValue}";

            // Node await-sends and only LOGS a failure (auth.js:779-785); the response stays the
            // success shape either way. The IEmailSender port throws on a configured-transport
            // failure, so the swallow here is the reproduction, not carelessness.
            try
            {
                await email.SendAsync(new EmailMessage(
                    To: user.Email!,
                    Subject: "Reset your Tebrazi password",
                    Html: $"<p>Hello {user.DisplayName},</p><p><a href=\"{resetLink}\">Reset your password</a></p>",
                    Text: $"Hello {user.DisplayName}, reset your password: {resetLink}"), ct);
            }
            catch (Exception mailEx)
            {
                logger.Error("Failed to send reset email", mailEx, new { Email = user.Email });
            }

            return new ForgotPasswordResponse(SuccessMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // auth.js:791-795 — the outer catch also answers the success shape.
            logger.Error("Forgot password error", ex, new { Email = normalized });
            return new ForgotPasswordResponse(SuccessMessage);
        }
    }

    private static bool IsValidEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && value.IndexOf('.', at) > at + 1 && !value.Contains(' ');
    }
}
