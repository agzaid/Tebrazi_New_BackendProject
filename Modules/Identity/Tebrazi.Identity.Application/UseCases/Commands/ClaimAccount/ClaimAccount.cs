using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Domain.Entities;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Identity.Application.UseCases.Commands.SharedPhone;

namespace Tebrazi.Identity.Application.UseCases.Commands.ClaimAccount;

/// <summary>
/// <c>POST /api/auth/claim-account</c> (auth.js:1112-1250). Public — upgrades a physician-created
/// stub PATIENT account with real credentials, after a phone OTP.
///
/// ORDER, carried from Node because the client tests it: missing-inputs 400 → no-valid-OTP 400 →
/// attempts-exhausted 400 → increment attempts → mismatch 400 with remaining count → mark
/// verified → stub lookup 404 → email-taken 409 → upgrade → token → session row (swallowed on
/// failure) → 200.
/// </summary>
public sealed record ClaimAccountCommand(
    string? Phone,
    string? OtpCode,
    string? Email,
    string? Password,
    string? DisplayName) : IRequest<ClaimAccountResponse>;

public sealed record ClaimAccountResponse(
    bool Success,
    string Message,
    string Token,
    ClaimedUser User);

public sealed record ClaimedUser(
    string Id,
    string DisplayName,
    string? Email,
    string? Phone,
    string UserType,
    string Role);

public sealed class ClaimAccountHandler(
    IIdentityDbContext dbContext,
    ITokenStore tokens,
    ISessionStore sessions,
    IUserReadStore users,
    IPasswordHasher hasher,
    IJwtTokenGenerator jwt,
    IAppLogger<ClaimAccountHandler> logger)
    : IRequestHandler<ClaimAccountCommand, ClaimAccountResponse>
{
    public async Task<ClaimAccountResponse> Handle(ClaimAccountCommand request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.OtpCode) ||
            string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ValidationException("Phone, OTP code, email, and password are required");
        }

        var normalizedPhone = PhoneNormalizer.Normalize(request.Phone);

        // auth.js:1130-1140 — latest CLAIM_ACCOUNT code, unverified, not expired.
        var otp = await tokens.GetLatestOtpAsync(normalizedPhone, OtpCode.PurposeClaimAccount, ct);
        if (otp is null || otp.Verified || otp.ExpiresAt <= DateTime.UtcNow)
            throw new ValidationException("No valid OTP found. Please request a new code.");

        if (otp.Attempts >= 5)
            throw new ValidationException("Too many attempts. Please request a new code.");

        // auth.js:1146-1151 — the increment commits BEFORE the comparison, so a wrong guess
        // counts even though the request ends in a 400.
        otp.RecordFailedAttempt();
        await dbContext.SaveChangesAsync(ct);

        if (otp.Code != request.OtpCode)
        {
            var remaining = 4 - otp.Attempts;
            throw new ValidationException(
                $"Invalid code. {remaining} attempt{(remaining != 1 ? "s" : "")} remaining.");
        }

        otp.MarkVerified();
        await dbContext.SaveChangesAsync(ct);

        // auth.js:1176-1183 — the stub is the PATIENT row on this phone, active. GetByPhoneAsync
        // is untyped, so the type gate stays here, exactly as the Prisma filter had it.
        var stubUser = await users.GetByPhoneAsync(normalizedPhone, ct);
        if (stubUser is null || stubUser.UserType != UserType.PATIENT || !stubUser.Active)
            throw new BusinessException("Not Found", "No account found for this phone number", 404);

        var claimedEmail = request.Email!.Trim().ToLowerInvariant();

        // auth.js:1182-1188 — same PATIENT type, excluding the stub itself.
        var emailTaken = await users.GetByEmailAsync(claimedEmail, UserType.PATIENT, ct);
        if (emailTaken is not null && emailTaken.Id != stubUser.Id)
            throw new ConflictException("This email is already registered");

        stubUser.ChangePassword(hasher.Hash(request.Password!));
        stubUser.Rename(request.DisplayName ?? stubUser.DisplayName);
        // User has no SetEmail; the stub's synthetic address becomes the claimed one through the
        // same field Node updates. A dedicated mutator keeps the invariant honest.
        stubUser.ReplaceEmail(claimedEmail);
        await dbContext.SaveChangesAsync(ct);

        var (token, expiresAt) = jwt.Generate(stubUser, stubUser.CurrentOrganizationId);

        // Non-critical in Node (a trailing .catch(() => {})) — the session row is bookkeeping.
        try
        {
            sessions.Add(Session.Issue(stubUser.Id, token, DateTime.UtcNow.AddDays(7)));
            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception sessionEx) when (sessionEx is not OperationCanceledException)
        {
            logger.Warning("Session write failed on claim", sessionEx);
        }

        logger.Information("Account claimed successfully", new { UserId = stubUser.Id, Phone = normalizedPhone });

        return new ClaimAccountResponse(
            Success: true,
            Message: "Account claimed successfully! Welcome to Tebrazi.",
            Token: token,
            User: new ClaimedUser(
                Id: stubUser.Id,
                DisplayName: stubUser.DisplayName,
                Email: stubUser.Email,
                Phone: stubUser.Phone,
                UserType: stubUser.UserType.ToString(),
                Role: stubUser.Role.ToString()));
    }

}
