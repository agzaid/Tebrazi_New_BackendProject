using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Application.Mapping;
using Tebrazi.Clinics.Application.Services;
using Tebrazi.Clinics.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Authorization;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Clinics.Application.UseCases.Queries.ListClinics;

/// <summary><c>GET /api/clinics</c> — every clinic the caller can reach, however they reach it.</summary>
public sealed record ListClinicsQuery(string UserId, string? UserType)
    : IRequest<IReadOnlyList<ClinicResponse>>;

/// <summary>
/// Port of <c>GET /api/clinics</c>. Two sources are merged: clinics the caller OWNS as a
/// physician, and clinics they are STAFF at — the second applies to any user type, including
/// patients. A physician who is also staff at their own clinic must appear once, not twice.
/// </summary>
public sealed class ListClinicsHandler(
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IIdentityDirectory identity) : IRequestHandler<ListClinicsQuery, IReadOnlyList<ClinicResponse>>
{
    public async Task<IReadOnlyList<ClinicResponse>> Handle(
        ListClinicsQuery request,
        CancellationToken cancellationToken = default)
    {
        var owned = new List<Clinic>();

        if (request.UserType == "PHYSICIAN")
        {
            var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);
            if (physician is not null)
                owned = [.. await clinics.ListByPhysicianAsync(physician.Id, cancellationToken)];
        }

        var memberships = await staff.ListActiveForUserAsync(request.UserId, cancellationToken);

        // De-duplicate before building responses: a physician who also holds a staff row at their
        // own clinic would otherwise see it twice in the switcher.
        var ownedIds = owned.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var staffOnly = memberships.Where(m => !ownedIds.Contains(m.ClinicId)).ToList();

        var all = owned.Concat(staffOnly.Select(m => m.Clinic)).ToList();
        if (all.Count == 0) return [];

        // Batch the two cross-cutting lookups rather than resolving per clinic.
        var physicianIds = all.Select(c => c.PhysicianId).Distinct().ToList();
        var physicians = await identity.GetPhysiciansAsync(physicianIds, cancellationToken);
        var staffCounts = await clinics.CountStaffAsync([.. all.Select(c => c.Id)], cancellationToken);

        var result = new List<ClinicResponse>(all.Count);

        foreach (var clinic in owned)
        {
            result.Add(ClinicMapper.ToResponse(
                clinic,
                physician: physicians.GetValueOrDefault(clinic.PhysicianId),
                staffCount: staffCounts.GetValueOrDefault(clinic.Id)));
        }

        foreach (var membership in staffOnly)
        {
            result.Add(ClinicMapper.ToResponse(
                membership.Clinic,
                physician: physicians.GetValueOrDefault(membership.Clinic.PhysicianId),
                staffCount: staffCounts.GetValueOrDefault(membership.ClinicId),
                staffRole: membership.Role,
                staffPermissions: ClinicPermissionMatrix.Resolve(
                    membership.Role, ClinicAccessEvaluator.ParseOverrides(membership.Permissions))));
        }

        return result;
    }
}
