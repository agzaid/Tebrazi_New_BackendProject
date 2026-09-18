using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Application.ApiModels.Responses;
using Tebrazi.Patients.Application.Services;
using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Patients.Application.UseCases;

// ── GET /api/patients/family ─────────────────────────────────────────────────

public sealed record ListFamilyQuery(string UserId) : IRequest<IReadOnlyList<SubprofileResponse>>;

/// <summary>
/// Port of <c>GET /api/patients/family</c>. A caller with no profile yet gets an empty list
/// rather than a created profile — reading the family list should not write.
/// </summary>
public sealed class ListFamilyHandler(PatientProfileResolver resolver, IFamilySubprofileStore subprofiles)
    : IRequestHandler<ListFamilyQuery, IReadOnlyList<SubprofileResponse>>
{
    public async Task<IReadOnlyList<SubprofileResponse>> Handle(
        ListFamilyQuery request, CancellationToken cancellationToken = default)
    {
        var profile = await resolver.FindAsync(request.UserId, cancellationToken);
        if (profile is null) return [];

        var family = await subprofiles.ListActiveAsync(profile.Id, cancellationToken);
        return [.. family.Select(PatientMapper.ToResponse)];
    }
}

// ── GET /api/patients/{userId}/family-members ────────────────────────────────

public sealed record ListFamilyMembersQuery(string TargetUserId, string CallerUserId)
    : IRequest<IReadOnlyList<SubprofileResponse>>;

/// <summary>
/// Port of <c>GET /api/patients/:userId/family-members</c> — used by a physician viewing a
/// patient's file, so the target is someone else's account.
/// </summary>
public sealed class ListFamilyMembersHandler(
    IPatientProfileStore profiles, IFamilySubprofileStore subprofiles)
    : IRequestHandler<ListFamilyMembersQuery, IReadOnlyList<SubprofileResponse>>
{
    public async Task<IReadOnlyList<SubprofileResponse>> Handle(
        ListFamilyMembersQuery request, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByUserIdAsync(request.TargetUserId, cancellationToken);
        if (profile is null) return [];

        var family = await subprofiles.ListActiveAsync(profile.Id, cancellationToken);
        return [.. family.Select(PatientMapper.ToResponse)];
    }
}

// ── POST /api/patients/family ────────────────────────────────────────────────

public sealed record AddFamilyMemberCommand(
    string UserId, string? Name, string? Relation, DateTime? DateOfBirth,
    string? Gender, string? BloodType) : IRequest<SubprofileResponse>;

public sealed class AddFamilyMemberHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IFamilySubprofileStore subprofiles) : IRequestHandler<AddFamilyMemberCommand, SubprofileResponse>
{
    public async Task<SubprofileResponse> Handle(
        AddFamilyMemberCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationException("Name is required");

        if (!Enum.TryParse<SubprofileRelation>(request.Relation, ignoreCase: true, out var relation))
            throw new ValidationException(
                $"Relation must be one of: {string.Join(", ", Enum.GetNames<SubprofileRelation>())}");

        var profile = await resolver.GetOrCreateAsync(request.UserId, cancellationToken);

        var subprofile = FamilySubprofile.Create(
            profile.Id,
            request.Name,
            relation,
            request.DateOfBirth,
            Enum.TryParse<Gender>(request.Gender, ignoreCase: true, out var gender) ? gender : null,
            request.BloodType);

        subprofiles.Add(subprofile);
        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(subprofile);
    }
}

// ── PUT /api/patients/family/{id} ────────────────────────────────────────────

public sealed record UpdateFamilyMemberCommand(
    string SubprofileId, string UserId, string? Name, string? Relation,
    DateTime? DateOfBirth, string? Gender, string? BloodType) : IRequest<SubprofileResponse>;

public sealed class UpdateFamilyMemberHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IFamilySubprofileStore subprofiles) : IRequestHandler<UpdateFamilyMemberCommand, SubprofileResponse>
{
    public async Task<SubprofileResponse> Handle(
        UpdateFamilyMemberCommand request, CancellationToken cancellationToken = default)
    {
        var profile = await resolver.FindAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException("Family member not found");

        var subprofile = await subprofiles.GetByIdAsync(request.SubprofileId, cancellationToken);

        // Ownership is checked on the loaded row, not by trusting the id: a subprofile belonging
        // to another account must read as "not found", not as someone else's data.
        if (subprofile is null || subprofile.PatientProfileId != profile.Id)
            throw new NotFoundException("Family member not found");

        subprofile.Update(
            request.Name,
            request.DateOfBirth,
            Enum.TryParse<Gender>(request.Gender, ignoreCase: true, out var gender) ? gender : null,
            Enum.TryParse<SubprofileRelation>(request.Relation, ignoreCase: true, out var relation)
                ? relation : null,
            request.BloodType);

        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(subprofile);
    }
}

// ── DELETE /api/patients/family/{id} ─────────────────────────────────────────

public sealed record RemoveFamilyMemberCommand(string SubprofileId, string UserId)
    : IRequest<MessageResponse>;

/// <summary>
/// Deactivates rather than deletes. The dependant's visits, prescriptions and documents still
/// reference them, and a hard delete would orphan or cascade away real clinical history.
/// </summary>
public sealed class RemoveFamilyMemberHandler(
    IPatientsDbContext dbContext,
    PatientProfileResolver resolver,
    IFamilySubprofileStore subprofiles) : IRequestHandler<RemoveFamilyMemberCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        RemoveFamilyMemberCommand request, CancellationToken cancellationToken = default)
    {
        var profile = await resolver.FindAsync(request.UserId, cancellationToken)
            ?? throw new NotFoundException("Family member not found");

        var subprofile = await subprofiles.GetByIdAsync(request.SubprofileId, cancellationToken);

        if (subprofile is null || subprofile.PatientProfileId != profile.Id)
            throw new NotFoundException("Family member not found");

        subprofile.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Family member removed");
    }
}
