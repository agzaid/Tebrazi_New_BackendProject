using System.Text.Json;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Application.Mapping;
using Tebrazi.Clinics.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Authorization;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Clinics.Application.UseCases.Commands;

// ── PUT /api/clinics/:id/staff/:staffId ──────────────────────────────────────

public sealed record UpdateClinicStaffCommand(
    string ClinicId,
    string StaffId,
    string UserId,
    string? Role,
    IReadOnlyDictionary<string, bool>? Permissions,
    bool? IsActive) : IRequest<ClinicStaffResponse>;

/// <summary>
/// Port of <c>PUT /api/clinics/:id/staff/:staffId</c>. Owner only.
///
/// Permissions are sanitised against the known key list before storage, exactly as the Node
/// route does: an unrecognised key is dropped rather than persisted, so a client cannot invent a
/// permission name that later collides with a real one.
/// </summary>
public sealed class UpdateClinicStaffHandler(
    IClinicsDbContext dbContext,
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IIdentityDirectory identity) : IRequestHandler<UpdateClinicStaffCommand, ClinicStaffResponse>
{
    public async Task<ClinicStaffResponse> Handle(
        UpdateClinicStaffCommand request, CancellationToken cancellationToken = default)
    {
        await EnsureOwnerAsync(clinics, identity, request.ClinicId, request.UserId,
            "Only the clinic owner can modify staff", cancellationToken);

        var member = await staff.GetByIdAsync(request.StaffId, cancellationToken);

        // Confirm the row belongs to the clinic in the URL — otherwise one owner could edit
        // another clinic's staff by guessing a staff id.
        if (member is null || member.ClinicId != request.ClinicId)
            throw new NotFoundException("Staff member not found");

        var role = string.IsNullOrWhiteSpace(request.Role) ? member.Role : request.Role;
        var permissions = request.Permissions is null
            ? member.Permissions
            : Sanitize(request.Permissions);

        member.ChangeRole(role, permissions);

        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value) member.Reactivate(role, permissions);
            else member.Deactivate();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var user = await identity.GetUserAsync(member.UserId, cancellationToken);
        return ClinicMapper.ToResponse(member, user);
    }

    /// <summary>Keeps only known permission keys, coercing each value to a boolean.</summary>
    private static string Sanitize(IReadOnlyDictionary<string, bool> permissions)
    {
        var clean = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var key in ClinicPermission.All)
        {
            if (permissions.TryGetValue(key, out var value))
                clean[key] = value;
        }

        return JsonSerializer.Serialize(clean);
    }

    internal static async Task EnsureOwnerAsync(
        IClinicReadStore clinics,
        IIdentityDirectory identity,
        string clinicId,
        string userId,
        string message,
        CancellationToken ct)
    {
        var clinic = await clinics.GetByIdAsync(clinicId, ct);
        var physician = await identity.GetPhysicianByUserIdAsync(userId, ct);

        // The Node route answers 403 for a missing clinic too, rather than distinguishing it.
        if (clinic is null || physician is null || physician.Id != clinic.PhysicianId)
            throw new ForbiddenException(message);
    }
}

// ── DELETE /api/clinics/:id/staff/:staffId ───────────────────────────────────

public sealed record RemoveClinicStaffCommand(string ClinicId, string StaffId, string UserId)
    : IRequest<MessageResponse>;

/// <summary>Port of <c>DELETE /api/clinics/:id/staff/:staffId</c>. A hard delete, as in Node.</summary>
public sealed class RemoveClinicStaffHandler(
    IClinicsDbContext dbContext,
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IIdentityDirectory identity) : IRequestHandler<RemoveClinicStaffCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        RemoveClinicStaffCommand request, CancellationToken cancellationToken = default)
    {
        await UpdateClinicStaffHandler.EnsureOwnerAsync(clinics, identity, request.ClinicId, request.UserId,
            "Only the clinic owner can remove staff", cancellationToken);

        var member = await staff.GetByIdAsync(request.StaffId, cancellationToken);

        if (member is null || member.ClinicId != request.ClinicId)
            throw new NotFoundException("Staff member not found");

        staff.Remove(member);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Staff member removed");
    }
}

// ── POST /api/clinics/:id/invite ─────────────────────────────────────────────

public sealed record InviteStaffCommand(
    string ClinicId, string UserId, string? Email, string? Role, string? Message)
    : IRequest<InviteStaffResult>;

/// <summary>
/// Two outcomes in one response. A registered invitee becomes staff immediately
/// (<c>staff</c> populated); an unregistered one gets a tokenised invitation
/// (<c>invitation</c> populated).
/// </summary>
public sealed record InviteStaffResult(
    string Message,
    ClinicStaffResponse? Staff,
    StaffInvitationResponse? Invitation);

/// <summary>
/// Port of <c>POST /api/clinics/:id/invite</c>.
///
/// The registered-user lookup is by email ALONE, as in the Node route. That is worth knowing:
/// email is unique only per (email, userType), so where one address holds both a physician and a
/// patient account this resolves to an arbitrary one of them. Reproduced rather than corrected,
/// so the two backends behave the same; fixing it means deciding which account should be invited
/// and changing both.
/// </summary>
public sealed class InviteStaffHandler(
    IClinicsDbContext dbContext,
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IStaffInvitationStore invitations,
    IIdentityDirectory identity,
    IOrganizationMembershipWriter memberships) : IRequestHandler<InviteStaffCommand, InviteStaffResult>
{
    public async Task<InviteStaffResult> Handle(
        InviteStaffCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            throw new ValidationException("Email is required");

        var clinic = await clinics.GetByIdAsync(request.ClinicId, cancellationToken)
            ?? throw new NotFoundException("Clinic not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);
        if (physician is null || physician.Id != clinic.PhysicianId)
            throw new ForbiddenException("Only the clinic owner can invite staff");

        var email = request.Email.Trim().ToLowerInvariant();
        var role = ClinicStaffRole.All.Contains(request.Role, StringComparer.Ordinal)
            ? request.Role!
            : ClinicStaffRole.Receptionist;

        var existingUser = await identity.GetUserByEmailAsync(email, ct: cancellationToken);

        if (existingUser is not null)
        {
            var alreadyStaff = await staff.GetAsync(clinic.Id, existingUser.Id, cancellationToken);
            if (alreadyStaff is not null)
                throw new ConflictException("This user is already staff at this clinic");

            var member = ClinicStaff.Create(clinic.Id, existingUser.Id, role);
            staff.Add(member);
            await dbContext.SaveChangesAsync(cancellationToken);

            await memberships.EnsureMemberAsync(clinic.OrganizationId, existingUser.Id, cancellationToken);

            return new InviteStaffResult(
                $"{existingUser.DisplayName} was added to {clinic.Name}",
                ClinicMapper.ToResponse(member, existingUser),
                null);
        }

        // Not registered — re-use an outstanding invitation rather than stacking duplicates.
        var pending = await invitations.FindPendingAsync(clinic.Id, email, cancellationToken);

        if (pending is not null && pending.IsAcceptable(DateTime.UtcNow))
            return new InviteStaffResult("Invitation already sent", null, ClinicMapper.ToResponse(pending));

        var invitation = StaffInvitation.Create(
            clinic.Id, request.UserId, email, role, DateTime.UtcNow);

        invitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new InviteStaffResult(
            $"Invitation sent to {email}", null, ClinicMapper.ToResponse(invitation));
    }
}
