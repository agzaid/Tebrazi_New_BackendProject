using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Application.Mapping;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Authorization;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Clinics.Application.UseCases.Queries;

// ── GET /api/clinics/mine ────────────────────────────────────────────────────

/// <summary>Lightweight clinic list for the sidebar switcher. Physicians only.</summary>
public sealed record ListMyClinicsQuery(string UserId, string? UserType)
    : IRequest<IReadOnlyList<ClinicSummaryResponse>>;

/// <summary>
/// Port of <c>GET /api/clinics/mine</c>. A non-physician, or a physician with no profile,
/// gets an empty array rather than a 403 — the sidebar renders that as "no clinics".
/// </summary>
public sealed class ListMyClinicsHandler(IClinicReadStore clinics, IIdentityDirectory identity)
    : IRequestHandler<ListMyClinicsQuery, IReadOnlyList<ClinicSummaryResponse>>
{
    public async Task<IReadOnlyList<ClinicSummaryResponse>> Handle(
        ListMyClinicsQuery request, CancellationToken cancellationToken = default)
    {
        if (request.UserType != "PHYSICIAN") return [];

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);
        if (physician is null) return [];

        var owned = await clinics.ListByPhysicianOldestFirstAsync(physician.Id, cancellationToken);
        return [.. owned.Select(ClinicMapper.ToSummary)];
    }
}

// ── GET /api/clinics/:id ─────────────────────────────────────────────────────

public sealed record GetClinicQuery(string ClinicId, string UserId) : IRequest<ClinicResponse>;

/// <summary>
/// Port of <c>GET /api/clinics/:id</c>. Readable by the owning physician or by active staff;
/// anyone else gets 403 rather than 404, matching the Node route.
/// </summary>
public sealed class GetClinicHandler(
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IIdentityDirectory identity) : IRequestHandler<GetClinicQuery, ClinicResponse>
{
    public async Task<ClinicResponse> Handle(GetClinicQuery request, CancellationToken cancellationToken = default)
    {
        var clinic = await clinics.GetByIdAsync(request.ClinicId, cancellationToken)
            ?? throw new NotFoundException("Clinic not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);
        var isOwner = physician is not null && physician.Id == clinic.PhysicianId;

        var membership = isOwner
            ? null
            : await staff.GetAsync(request.ClinicId, request.UserId, cancellationToken);

        if (!isOwner && (membership is null || !membership.IsActive))
            throw new ForbiddenException("You do not have access to this clinic");

        var owner = await identity.GetPhysiciansAsync([clinic.PhysicianId], cancellationToken);
        var staffCounts = await clinics.CountStaffAsync([clinic.Id], cancellationToken);

        return ClinicMapper.ToResponse(
            clinic,
            physician: owner.GetValueOrDefault(clinic.PhysicianId),
            staffCount: staffCounts.GetValueOrDefault(clinic.Id),
            staffRole: membership?.Role,
            staffPermissions: membership is null
                ? null
                : ClinicPermissionMatrix.Resolve(
                    membership.Role, Services.ClinicAccessEvaluator.ParseOverrides(membership.Permissions)));
    }
}

// ── GET /api/clinics/directory ───────────────────────────────────────────────

/// <summary>The public clinic directory patients browse when looking for a physician.</summary>
public sealed record ClinicDirectoryQuery(
    string? Search, string? Specialty, string? City, int Page, int PageSize)
    : IRequest<ClinicDirectoryResponse>;

public sealed record ClinicDirectoryResponse(
    IReadOnlyList<ClinicResponse> Clinics, int Total, int Page, int PageSize);

/// <summary>
/// Port of <c>GET /api/clinics/directory</c>. Paged in SQL, not in memory: the directory grows
/// with every clinic on the platform.
/// </summary>
public sealed class ClinicDirectoryHandler(IClinicReadStore clinics, IIdentityDirectory identity)
    : IRequestHandler<ClinicDirectoryQuery, ClinicDirectoryResponse>
{
    public async Task<ClinicDirectoryResponse> Handle(
        ClinicDirectoryQuery request, CancellationToken cancellationToken = default)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 100 ? 20 : request.PageSize;

        var (items, total) = await clinics.SearchDirectoryAsync(
            request.Search, request.Specialty, request.City, page, pageSize, cancellationToken);

        if (items.Count == 0)
            return new ClinicDirectoryResponse([], total, page, pageSize);

        var physicians = await identity.GetPhysiciansAsync(
            [.. items.Select(c => c.PhysicianId).Distinct()], cancellationToken);

        var mapped = items
            .Select(c => ClinicMapper.ToResponse(c, physician: physicians.GetValueOrDefault(c.PhysicianId)))
            .ToList();

        return new ClinicDirectoryResponse(mapped, total, page, pageSize);
    }
}

// ── GET /api/clinics/roles ───────────────────────────────────────────────────

public sealed record GetClinicRolesQuery : IRequest<ClinicRolesResponse>;

/// <summary>
/// Port of <c>GET /api/clinics/roles</c> — the role list and their permission presets, which the
/// staff permission matrix screen renders directly.
/// </summary>
public sealed class GetClinicRolesHandler : IRequestHandler<GetClinicRolesQuery, ClinicRolesResponse>
{
    public Task<ClinicRolesResponse> Handle(
        GetClinicRolesQuery request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ClinicRolesResponse(
            ClinicStaffRole.All,
            ClinicPermissionMatrix.AllPresets()));
}

// ── GET /api/clinics/:id/staff ───────────────────────────────────────────────

public sealed record ListClinicStaffQuery(string ClinicId, string UserId)
    : IRequest<IReadOnlyList<ClinicStaffResponse>>;

/// <summary>Port of <c>GET /api/clinics/:id/staff</c>.</summary>
public sealed class ListClinicStaffHandler(
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IIdentityDirectory identity) : IRequestHandler<ListClinicStaffQuery, IReadOnlyList<ClinicStaffResponse>>
{
    public async Task<IReadOnlyList<ClinicStaffResponse>> Handle(
        ListClinicStaffQuery request, CancellationToken cancellationToken = default)
    {
        var clinic = await clinics.GetByIdAsync(request.ClinicId, cancellationToken)
            ?? throw new NotFoundException("Clinic not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);
        var isOwner = physician is not null && physician.Id == clinic.PhysicianId;

        if (!isOwner)
        {
            var membership = await staff.GetAsync(request.ClinicId, request.UserId, cancellationToken);
            if (membership is null || !membership.IsActive)
                throw new ForbiddenException("You do not have access to this clinic");
        }

        var members = await staff.ListForClinicAsync(request.ClinicId, cancellationToken);
        if (members.Count == 0) return [];

        // One lookup for every staff user, not one per row.
        var users = await identity.GetUsersAsync([.. members.Select(m => m.UserId).Distinct()], cancellationToken);

        return [.. members.Select(m => ClinicMapper.ToResponse(m, users.GetValueOrDefault(m.UserId)))];
    }
}
