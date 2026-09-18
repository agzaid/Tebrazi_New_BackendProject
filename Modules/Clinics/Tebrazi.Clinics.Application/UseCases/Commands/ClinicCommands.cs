using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Application.Mapping;
using Tebrazi.Clinics.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Clinics.Application.UseCases.Commands;

// ── POST /api/clinics ────────────────────────────────────────────────────────

public sealed record CreateClinicCommand(
    string UserId,
    string? UserType,
    string? Name,
    string? Address,
    string? City,
    string? Country,
    string? Phone,
    string? Email,
    string? Specialty,
    string? WorkingHours) : IRequest<ClinicResponse>;

/// <summary>
/// Port of <c>POST /api/clinics</c>. A clinic is a tenant: the organization, OWNER membership
/// and FREE subscription are provisioned alongside it.
///
/// The provisioning commits separately from the clinic row (they live in different modules'
/// contexts). Order matters: the organization is created FIRST, so a failure leaves an empty
/// organization rather than a clinic pointing at one that does not exist.
/// </summary>
public sealed class CreateClinicHandler(
    IClinicsDbContext dbContext,
    IClinicWriteStore clinics,
    IIdentityDirectory identity,
    IOrganizationProvisioner organizations,
    IAppLogger<CreateClinicHandler> logger) : IRequestHandler<CreateClinicCommand, ClinicResponse>
{
    public async Task<ClinicResponse> Handle(
        CreateClinicCommand request, CancellationToken cancellationToken = default)
    {
        if (request.UserType != "PHYSICIAN")
            throw new ForbiddenException("Only physicians can create clinics");

        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationException("Clinic name is required");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken)
            ?? throw new NoPhysicianProfileException();

        var organizationId = await organizations.ProvisionAsync(
            request.Name.Trim(), request.UserId, cancellationToken);

        var clinic = Clinic.Create(
            organizationId: organizationId,
            physicianId: physician.Id,
            name: request.Name,
            // Falls back to the physician's own specialty when the form leaves it blank.
            specialty: string.IsNullOrWhiteSpace(request.Specialty) ? physician.Specialty : request.Specialty,
            address: request.Address,
            city: request.City,
            country: request.Country,
            phone: request.Phone,
            email: request.Email,
            workingHours: request.WorkingHours);

        clinics.Add(clinic);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.Information("Clinic created", new
        {
            ClinicId = clinic.Id,
            OrganizationId = organizationId,
            PhysicianId = physician.Id
        });

        return ClinicMapper.ToResponse(clinic, physician: physician);
    }
}

/// <summary>
/// 400 with the Node route's <c>code: "NO_PROFILE"</c> field, which the client uses to route the
/// physician to profile completion rather than showing a generic error.
/// </summary>
public sealed class NoPhysicianProfileException()
    : AppException("Please complete your physician profile first", "Please complete your physician profile first")
{
    public override int StatusCode => 400;

    public override IReadOnlyDictionary<string, object?> AdditionalFields
        => new Dictionary<string, object?> { ["code"] = "NO_PROFILE" };
}

// ── PUT /api/clinics/:id ─────────────────────────────────────────────────────

public sealed record UpdateClinicCommand(
    string ClinicId,
    string UserId,
    string? Name,
    string? Address,
    string? City,
    string? Country,
    string? Phone,
    string? Email,
    string? Specialty,
    string? WorkingHours,
    bool? AllowPatientBooking,
    double? ConsultationFee,
    double? FollowUpFee) : IRequest<ClinicResponse>;

/// <summary>Port of <c>PUT /api/clinics/:id</c>. Owner only; a partial body updates only what it carries.</summary>
public sealed class UpdateClinicHandler(
    IClinicsDbContext dbContext,
    IClinicWriteStore clinics,
    IIdentityDirectory identity) : IRequestHandler<UpdateClinicCommand, ClinicResponse>
{
    public async Task<ClinicResponse> Handle(
        UpdateClinicCommand request, CancellationToken cancellationToken = default)
    {
        var clinic = await clinics.GetForUpdateAsync(request.ClinicId, cancellationToken)
            ?? throw new NotFoundException("Clinic not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);

        if (physician is null || physician.Id != clinic.PhysicianId)
            throw new ForbiddenException("Only the clinic owner can update this clinic");

        clinic.Update(
            request.Name, request.Address, request.City, request.Country,
            request.Phone, request.Email, request.Specialty, request.WorkingHours,
            request.AllowPatientBooking, request.ConsultationFee, request.FollowUpFee);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ClinicMapper.ToResponse(clinic, physician: physician);
    }
}

// ── DELETE /api/clinics/:id ──────────────────────────────────────────────────

public sealed record DeleteClinicCommand(string ClinicId, string UserId) : IRequest<MessageResponse>;

public sealed record MessageResponse(string Message);

/// <summary>
/// Port of <c>DELETE /api/clinics/:id</c>. A HARD delete, as in the Node route — the clinic row
/// goes, then its organization, cascading to visits, staff, prescriptions and inventory.
///
/// The two deletes are in different modules' contexts and so are two commits. The clinic is
/// removed first: a failure between them leaves an orphaned organization, which is recoverable,
/// rather than a clinic whose organization has gone, which is not.
/// </summary>
public sealed class DeleteClinicHandler(
    IClinicsDbContext dbContext,
    IClinicWriteStore clinics,
    IIdentityDirectory identity,
    IOrganizationProvisioner organizations,
    IAppLogger<DeleteClinicHandler> logger) : IRequestHandler<DeleteClinicCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        DeleteClinicCommand request, CancellationToken cancellationToken = default)
    {
        var clinic = await clinics.GetForUpdateAsync(request.ClinicId, cancellationToken)
            ?? throw new NotFoundException("Clinic not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);

        if (physician is null || physician.Id != clinic.PhysicianId)
            throw new ForbiddenException("Only the clinic owner can delete a clinic");

        var organizationId = clinic.OrganizationId;

        clinics.Remove(clinic);
        await dbContext.SaveChangesAsync(cancellationToken);

        await organizations.DeleteAsync(organizationId, cancellationToken);

        logger.Information("Clinic deleted", new { clinic.Id, clinic.Name, organizationId });

        return new MessageResponse("Clinic deleted successfully");
    }
}
