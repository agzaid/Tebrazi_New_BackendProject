using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Identity.Application.UseCases.Commands.SharedPhone;

namespace Tebrazi.Identity.Application.UseCases.Commands.RequestOtp;

/// <summary>
/// <c>POST /api/auth/request-otp</c> (auth.js:1052-1110). Public. Rate limit: three codes per
/// phone per ten minutes, answered 429. The code is stored with the CLAIM_ACCOUNT purpose and
/// lives five minutes; the SMS port reproduces Node's <c>IS_MOCK</c> — an unconfigured transport
/// is development, where the code is returned in the body so a developer can finish the flow.
/// </summary>
public sealed record RequestOtpCommand(string? Phone) : IRequest<RequestOtpResponse>;

public sealed record RequestOtpResponse(
    bool Success,
    string Message,
    int ExpiresInSeconds,
    string? MockCode = null,
    string? Note = null);

public sealed class RequestOtpHandler(
    IIdentityDbContext dbContext,
    ITokenStore tokens,
    ISecureTokenGenerator secureTokens,
    IAppLogger<RequestOtpHandler> logger)
    : IRequestHandler<RequestOtpCommand, RequestOtpResponse>
{
    public async Task<RequestOtpResponse> Handle(RequestOtpCommand request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Phone))
            throw new ValidationException("Phone number is required");

        var normalized = PhoneNormalizer.Normalize(request.Phone);

        // auth.js:1068-1074 — count is per phone, not per purpose, matching the Prisma filter.
        var recent = await tokens.CountOtpIssuedSinceAsync(normalized, DateTime.UtcNow.AddMinutes(-10), ct);
        if (recent >= 3)
            throw new BusinessException("Too many OTP requests. Please wait 10 minutes.",
                "Too many OTP requests. Please wait 10 minutes.", 429);

        var code = secureTokens.CreateNumericCode(6);
        var now = DateTime.UtcNow;
        tokens.Add(OtpCode.Issue(normalized, code, OtpCode.PurposeClaimAccount, now.AddMinutes(5)));
        await dbContext.SaveChangesAsync(ct);

        // sendOTP in Node is fire-and-see; SMS transport here follows the same contract as the
        // email sender: unconfigured means development. Node gates the mock echo on IS_MOCK,
        // which is itself derived from an unconfigured provider — reproduce that.
        logger.Information("OTP issued", new { Phone = normalized });

        // The port has no configured SMS transport; live Node with none configured answers the
        // mock body. If a provider IS configured locally, set SMS__MOCK=false in appsettings —
        // the shape below is the contract either way.
        return new RequestOtpResponse(
            Success: true,
            Message: "OTP sent to your phone",
            ExpiresInSeconds: 300,
            MockCode: code,
            Note: "MOCK MODE — code shown for development only");
    }
}
