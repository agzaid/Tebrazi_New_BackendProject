using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Application.Services;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Authorization;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Clinics.Application.UseCases.Queries.MyContext;

/// <summary><c>GET /api/clinics/my-context</c> — what the context switcher offers this user.</summary>
public sealed record GetMyContextQuery(string UserId, string? UserType) : IRequest<MyContextResponse>;

/// <summary>
/// Port of <c>GET /api/clinics/my-context</c>, including its ordering rules:
/// physician contexts first (one per clinic, or a single "My Practice" placeholder when they
/// own none), then at most ONE staff context, and a "Personal Health" context prepended only
/// for non-physicians.
///
/// <c>hasClinicAccess</c> is <c>contexts.Length &gt; 1</c> — that is what the Node route computes,
/// so a physician with exactly one clinic reports false. Kept as-is on purpose.
/// </summary>
public sealed class GetMyContextHandler(
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IIdentityDirectory identity) : IRequestHandler<GetMyContextQuery, MyContextResponse>
{
    public async Task<MyContextResponse> Handle(
        GetMyContextQuery request,
        CancellationToken cancellationToken = default)
    {
        var contexts = new List<ContextResponse>();

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);

        if (physician is not null)
        {
            var owned = await clinics.ListByPhysicianOldestFirstAsync(physician.Id, cancellationToken);

            if (owned.Count > 0)
            {
                contexts.AddRange(owned.Select(clinic => new ContextResponse(
                    Mode: "physician",
                    ClinicId: clinic.Id,
                    Label: clinic.Name,
                    Specialty: clinic.Specialty,
                    Icon: "🏥",
                    Role: "PHYSICIAN",
                    Permissions: null,
                    PhysicianName: null,
                    Active: null)));
            }
            else
            {
                // A physician who has not created a clinic yet still needs physician mode.
                contexts.Add(new ContextResponse(
                    Mode: "physician",
                    ClinicId: null,
                    Label: "My Practice",
                    Specialty: physician.Specialty,
                    Icon: "🩺",
                    Role: "PHYSICIAN",
                    Permissions: null,
                    PhysicianName: null,
                    Active: null));
            }
        }

        // Only the FIRST staff role, as in the Node route — someone on staff at two clinics gets
        // one context, not two.
        var membership = await staff.FirstActiveForUserAsync(request.UserId, cancellationToken);

        if (membership is not null)
        {
            var owner = await identity.GetPhysiciansAsync([membership.Clinic.PhysicianId], cancellationToken);

            contexts.Add(new ContextResponse(
                Mode: "clinic",
                ClinicId: membership.ClinicId,
                Label: membership.Clinic.Name,
                Specialty: membership.Clinic.Specialty,
                Icon: "🏥",
                Role: membership.Role,
                Permissions: ClinicPermissionMatrix.Resolve(
                    membership.Role, ClinicAccessEvaluator.ParseOverrides(membership.Permissions)),
                PhysicianName: owner.GetValueOrDefault(membership.Clinic.PhysicianId)?.DisplayName,
                Active: null));
        }

        if (physician is null)
        {
            contexts.Insert(0, new ContextResponse(
                Mode: "personal",
                ClinicId: null,
                Label: "Personal Health",
                Specialty: null,
                Icon: "🏠",
                Role: null,
                Permissions: null,
                PhysicianName: null,
                Active: true));
        }

        return new MyContextResponse(
            UserId: request.UserId,
            UserType: request.UserType ?? string.Empty,
            Contexts: contexts,
            HasClinicAccess: contexts.Count > 1);
    }
}
