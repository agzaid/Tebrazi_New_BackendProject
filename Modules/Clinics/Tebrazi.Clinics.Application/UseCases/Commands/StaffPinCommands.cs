using System.Security.Cryptography;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Authorization;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Clinics.Application.UseCases.Commands;

// ── POST /api/clinics/:id/generate-staff-pin ─────────────────────────────────

public sealed record GenerateStaffPinCommand(string ClinicId, string UserId, string? Role)
    : IRequest<StaffPinResponse>;

/// <summary>
/// Port of <c>POST /api/clinics/:id/generate-staff-pin</c>. Issues a 4-digit code valid for five
/// minutes, after expiring the clinic's outstanding ones so only one is ever live.
///
/// The PIN is drawn from a cryptographic RNG rather than <c>Math.random()</c>. A 4-digit code is
/// only 10,000 possibilities, and its safety rests entirely on the five-minute window and on
/// there being at most one live PIN per clinic — a predictable generator would remove even that.
/// </summary>
public sealed class GenerateStaffPinHandler(
    IClinicsDbContext dbContext,
    IClinicReadStore clinics,
    IStaffPinStore pins,
    IIdentityDirectory identity,
    IAppLogger<GenerateStaffPinHandler> logger) : IRequestHandler<GenerateStaffPinCommand, StaffPinResponse>
{
    private const int MaxAttempts = 20;

    public async Task<StaffPinResponse> Handle(
        GenerateStaffPinCommand request, CancellationToken cancellationToken = default)
    {
        var clinic = await clinics.GetByIdAsync(request.ClinicId, cancellationToken)
            ?? throw new NotFoundException("Clinic not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);

        if (physician is null || physician.Id != clinic.PhysicianId)
            throw new ForbiddenException("Only the clinic owner can generate staff PINs");

        var role = ClinicStaffRole.All.Contains(request.Role, StringComparer.Ordinal)
            ? request.Role!
            : ClinicStaffRole.Receptionist;

        var now = DateTime.UtcNow;

        // Expire the clinic's live PINs so the new one is the only way in.
        foreach (var live in await pins.ListLiveForClinicAsync(request.ClinicId, now, cancellationToken))
            live.Expire(now);

        var pin = await GenerateUniquePinAsync(now, cancellationToken);
        var issued = StaffPin.Issue(request.ClinicId, request.UserId, role, pin, now);

        pins.Add(issued);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.Information("Staff PIN issued", new { request.ClinicId, role });

        return new StaffPinResponse(
            Pin: issued.Pin,
            Role: role,
            ClinicName: clinic.Name,
            ExpiresAt: issued.ExpiresAt,
            ExpiresInSeconds: (int)StaffPin.Lifetime.TotalSeconds);
    }

    private async Task<string> GenerateUniquePinAsync(DateTime now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = RandomNumberGenerator.GetInt32(1000, 10000).ToString();

            if (!await pins.IsPinInUseAsync(candidate, now, ct))
                return candidate;
        }

        throw new BusinessException(
            "Could not generate unique PIN, try again",
            "Could not generate unique PIN, try again",
            statusCode: 500);
    }
}

// ── POST /api/clinics/join-by-pin ────────────────────────────────────────────

public sealed record JoinClinicByPinCommand(string UserId, string? Pin) : IRequest<JoinClinicResponse>;

/// <summary>
/// Port of <c>POST /api/clinics/join-by-pin</c>. Three outcomes, each with its own body:
/// a fresh join, a re-join of a deactivated membership, and "already a member".
/// </summary>
public sealed class JoinClinicByPinHandler(
    IClinicsDbContext dbContext,
    IStaffPinStore pins,
    IClinicStaffStore staff,
    IIdentityDirectory identity,
    IOrganizationMembershipWriter memberships,
    IAppLogger<JoinClinicByPinHandler> logger) : IRequestHandler<JoinClinicByPinCommand, JoinClinicResponse>
{
    public async Task<JoinClinicResponse> Handle(
        JoinClinicByPinCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Pin) || request.Pin.Length != 4)
            throw new ValidationException("Please enter a 4-digit PIN");

        var now = DateTime.UtcNow;

        var pin = await pins.FindRedeemableAsync(request.Pin, now, cancellationToken)
            ?? throw new NotFoundException("Invalid or expired PIN. Ask the physician for a new code.");

        var clinic = pin.Clinic;
        var owner = await identity.GetPhysiciansAsync([clinic.PhysicianId], cancellationToken);
        var ownerProfile = owner.GetValueOrDefault(clinic.PhysicianId);

        if (ownerProfile is not null && ownerProfile.UserId == request.UserId)
            throw new ValidationException("You are the owner of this clinic");

        var existing = await staff.GetAsync(clinic.Id, request.UserId, cancellationToken);

        if (existing is not null)
        {
            // The PIN is consumed either way, so it cannot be reused by someone else.
            pin.Redeem(request.UserId, now);

            if (!existing.IsActive)
            {
                existing.Reactivate(pin.Role, null);
                await dbContext.SaveChangesAsync(cancellationToken);

                return new JoinClinicResponse(
                    $"Re-joined {clinic.Name}!", clinic.Id, clinic.Name, pin.Role, null, null);
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            return new JoinClinicResponse(
                "Already a member of this clinic", clinic.Id, clinic.Name, null, null, AlreadyMember: true);
        }

        staff.Add(ClinicStaff.Create(clinic.Id, request.UserId, pin.Role));
        pin.Redeem(request.UserId, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Separate commit, in Identity's context. Done after the clinic role so a failure here
        // leaves someone on staff without organization membership — visible and repairable —
        // rather than a membership with no role.
        await memberships.EnsureMemberAsync(clinic.OrganizationId, request.UserId, cancellationToken);

        logger.Information("Staff joined by PIN", new { ClinicId = clinic.Id, request.UserId, pin.Role });

        return new JoinClinicResponse(
            $"Joined {clinic.Name}!", clinic.Id, clinic.Name, pin.Role, ownerProfile?.DisplayName, null);
    }
}
