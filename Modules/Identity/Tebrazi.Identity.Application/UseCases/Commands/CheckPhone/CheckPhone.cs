using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Identity.Application.UseCases.Commands.SharedPhone;
namespace Tebrazi.Identity.Application.UseCases.Commands.CheckPhone;

/// <summary>
/// <c>POST /api/auth/check-phone</c> (auth.js:1012-1050). Public — the claim-account wizard's
/// first step: does a live PATIENT account hold this phone, and is it a stub?
///
/// A stub is an account whose email is a synthetic <c>patient-&lt;digits&gt;@tebrazi.local</c>
/// created by the physician's create-patient flow; the claim flow upgrades it. The response
/// carries the display name but deliberately never the email — the comment in the Node source
/// says "Don't expose real email for security" and the client relies on its absence.
/// </summary>
public sealed record CheckPhoneCommand(string? Phone) : IRequest<CheckPhoneResponse>;

public sealed record CheckPhoneResponse(bool Exists, bool? StubAccount = null, string? DisplayName = null);

public sealed class CheckPhoneHandler(IUserReadStore users, IAppLogger<CheckPhoneHandler> logger)
    : IRequestHandler<CheckPhoneCommand, CheckPhoneResponse>
{
    public async Task<CheckPhoneResponse> Handle(CheckPhoneCommand request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Phone))
            throw new ValidationException("Phone number is required");

        var normalized = PhoneNormalizer.Normalize(request.Phone);

        // auth.js:1021-1028 — PATIENT-only and active-only, by exact normalized phone.
        var user = await users.GetByPhoneAsync(normalized, ct);
        if (user is null || user.UserType != UserType.PATIENT || !user.Active)
            return new CheckPhoneResponse(Exists: false);

        try
        {
            var isStub = user.Email?.EndsWith("@tebrazi.local", StringComparison.Ordinal) ?? false;
            return new CheckPhoneResponse(Exists: true, StubAccount: isStub, DisplayName: user.DisplayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error("Check phone error", ex);
            throw new BusinessException("Failed to check phone", "Failed to check phone", 500);
        }
    }
}
