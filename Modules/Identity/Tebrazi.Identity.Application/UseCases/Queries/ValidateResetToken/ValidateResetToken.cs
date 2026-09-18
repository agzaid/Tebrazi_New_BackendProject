using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Queries.ValidateResetToken;

/// <summary>
/// <c>GET /api/auth/reset-password/{token}</c> (auth.js:911-960) — the pre-form check.
///
/// Every failure keeps status 200 false out of the way of the SPA: the response is
/// <c>{"valid":true,"email":masked}</c> or <c>{"valid":false,"error":...}</c> with a status
/// (404 / 410 / 403) that still signals the failure class. A 500 keeps <c>valid:false</c> too.
/// </summary>
public sealed record ValidateResetTokenQuery(string Token) : IRequest<ValidateResetTokenResponse>;

public sealed record ValidateResetTokenResponse(bool Valid, string? Email = null, string? Error = null);

public sealed class ValidateResetTokenHandler(
    ITokenStore tokens,
    IUserReadStore users,
    IAppLogger<ValidateResetTokenHandler> logger)
    : IRequestHandler<ValidateResetTokenQuery, ValidateResetTokenResponse>
{
    public async Task<ValidateResetTokenResponse> Handle(ValidateResetTokenQuery request, CancellationToken ct = default)
    {
        try
        {
            var resetToken = await tokens.GetResetTokenAsync(request.Token, ct);
            if (resetToken is null)
                return new ValidateResetTokenResponse(false, Error: "Invalid reset link");

            if (resetToken.UsedAt is not null)
                return new ValidateResetTokenResponse(false, Email: null, "This reset link has already been used");

            if (resetToken.ExpiresAt <= DateTime.UtcNow)
                return new ValidateResetTokenResponse(false, Email: null, "This reset link has expired");
            var user = await users.GetByIdAsync(resetToken.UserId, ct);
            if (user is null || !user.Active)
                return new ValidateResetTokenResponse(false, Email: null, "Account deactivated");

            // auth.js:945 masks to first 2 chars + *** + domain.
            return new ValidateResetTokenResponse(true, Email: MaskEmail(user.Email!));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error("Validate reset token error", ex);
            return new ValidateResetTokenResponse(false, Email: null, "Failed to validate token");
        }
    }

    internal static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return email;
        var local = email[..at];
        var prefix = local.Length <= 2 ? local : local[..2];
        return $"{prefix}***{email[at..]}";
    }
}
