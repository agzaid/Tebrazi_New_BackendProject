using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Application.Mapping;
using Tebrazi.Clinics.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Clinics.Application.UseCases.Commands;

// ── GET /api/clinics/:id/invites ─────────────────────────────────────────────

public sealed record ListStaffInvitationsQuery(string ClinicId, string UserId)
    : IRequest<IReadOnlyList<StaffInvitationResponse>>;

public sealed class ListStaffInvitationsHandler(
    IClinicReadStore clinics,
    IStaffInvitationStore invitations,
    IIdentityDirectory identity)
    : IRequestHandler<ListStaffInvitationsQuery, IReadOnlyList<StaffInvitationResponse>>
{
    public async Task<IReadOnlyList<StaffInvitationResponse>> Handle(
        ListStaffInvitationsQuery request, CancellationToken cancellationToken = default)
    {
        await UpdateClinicStaffHandler.EnsureOwnerAsync(clinics, identity, request.ClinicId, request.UserId,
            "Only the clinic owner can view invitations", cancellationToken);

        var items = await invitations.ListForClinicAsync(request.ClinicId, cancellationToken);
        return [.. items.Select(i => ClinicMapper.ToResponse(i))];
    }
}

// ── DELETE /api/clinics/:id/invites/:inviteId ────────────────────────────────

public sealed record RevokeStaffInvitationCommand(string ClinicId, string InvitationId, string UserId)
    : IRequest<MessageResponse>;

/// <summary>
/// Revokes rather than deletes, so the audit trail of who was invited survives. The token stops
/// working because acceptance requires PENDING status.
/// </summary>
public sealed class RevokeStaffInvitationHandler(
    IClinicsDbContext dbContext,
    IClinicReadStore clinics,
    IStaffInvitationStore invitations,
    IIdentityDirectory identity) : IRequestHandler<RevokeStaffInvitationCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        RevokeStaffInvitationCommand request, CancellationToken cancellationToken = default)
    {
        await UpdateClinicStaffHandler.EnsureOwnerAsync(clinics, identity, request.ClinicId, request.UserId,
            "Only the clinic owner can revoke invitations", cancellationToken);

        var invitation = await invitations.GetByIdAsync(request.InvitationId, cancellationToken);

        if (invitation is null || invitation.ClinicId != request.ClinicId)
            throw new NotFoundException("Invitation not found");

        invitation.Revoke();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Invitation revoked");
    }
}

// ── GET /api/clinics/invite/:token ───────────────────────────────────────────

public sealed record GetInvitationPreviewQuery(string Token) : IRequest<InvitationPreviewResponse>;

/// <summary>
/// Unauthenticated preview shown on the invitation landing page. It reveals only the clinic name,
/// the invited email and the role — enough to decide whether to accept, and no more.
/// </summary>
public sealed class GetInvitationPreviewHandler(
    IStaffInvitationStore invitations,
    IClinicReadStore clinics,
    IIdentityDirectory identity) : IRequestHandler<GetInvitationPreviewQuery, InvitationPreviewResponse>
{
    public async Task<InvitationPreviewResponse> Handle(
        GetInvitationPreviewQuery request, CancellationToken cancellationToken = default)
    {
        var invitation = await invitations.GetByTokenAsync(request.Token, cancellationToken)
            ?? throw new NotFoundException("Invitation not found");

        // 410 rather than 404: the link WAS valid, and the client shows "this invite expired"
        // instead of "no such invite".
        if (!invitation.IsAcceptable(DateTime.UtcNow))
            throw new GoneException("This invitation has expired or has already been used");

        var clinic = await clinics.GetByIdAsync(invitation.ClinicId, cancellationToken)
            ?? throw new NotFoundException("Clinic not found");

        var owner = await identity.GetPhysiciansAsync([clinic.PhysicianId], cancellationToken);

        return new InvitationPreviewResponse(
            ClinicId: clinic.Id,
            ClinicName: clinic.Name,
            Email: invitation.Email,
            Role: invitation.Role,
            PhysicianName: owner.GetValueOrDefault(clinic.PhysicianId)?.DisplayName,
            ExpiresAt: invitation.ExpiresAt);
    }
}

// ── POST /api/clinics/invite/:token/accept ───────────────────────────────────

public sealed record AcceptStaffInvitationCommand(string Token, string UserId)
    : IRequest<JoinClinicResponse>;

/// <summary>
/// Accepts an invitation for the AUTHENTICATED caller: they must have signed up or logged in
/// first, so the staff row is attached to a real account.
/// </summary>
public sealed class AcceptStaffInvitationHandler(
    IClinicsDbContext dbContext,
    IStaffInvitationStore invitations,
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IIdentityDirectory identity,
    IOrganizationMembershipWriter memberships) : IRequestHandler<AcceptStaffInvitationCommand, JoinClinicResponse>
{
    public async Task<JoinClinicResponse> Handle(
        AcceptStaffInvitationCommand request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var invitation = await invitations.GetByTokenAsync(request.Token, cancellationToken)
            ?? throw new NotFoundException("Invitation not found");

        if (!invitation.IsAcceptable(now))
            throw new GoneException("This invitation has expired or has already been used");

        var clinic = await clinics.GetByIdAsync(invitation.ClinicId, cancellationToken)
            ?? throw new NotFoundException("Clinic not found");

        var existing = await staff.GetAsync(clinic.Id, request.UserId, cancellationToken);

        if (existing is not null && existing.IsActive)
        {
            invitation.Accept(request.UserId, now);
            await dbContext.SaveChangesAsync(cancellationToken);

            return new JoinClinicResponse(
                "Already a member of this clinic", clinic.Id, clinic.Name, existing.Role, null, true);
        }

        if (existing is not null) existing.Reactivate(invitation.Role, invitation.Permissions);
        else staff.Add(ClinicStaff.Create(clinic.Id, request.UserId, invitation.Role, invitation.Permissions));

        invitation.Accept(request.UserId, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        await memberships.EnsureMemberAsync(clinic.OrganizationId, request.UserId, cancellationToken);

        var owner = await identity.GetPhysiciansAsync([clinic.PhysicianId], cancellationToken);

        return new JoinClinicResponse(
            $"Joined {clinic.Name}!",
            clinic.Id,
            clinic.Name,
            invitation.Role,
            owner.GetValueOrDefault(clinic.PhysicianId)?.DisplayName,
            null);
    }
}
