using System.Text.Json;
using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Application.ApiModels.Responses;
using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Patients.Application.UseCases;

// ── External visits ──────────────────────────────────────────────────────────

public sealed record ListExternalVisitsQuery(string UserId, string? SubprofileId)
    : IRequest<IReadOnlyList<ExternalVisitResponse>>;

public sealed class ListExternalVisitsHandler(IExternalCareStore store)
    : IRequestHandler<ListExternalVisitsQuery, IReadOnlyList<ExternalVisitResponse>>
{
    public async Task<IReadOnlyList<ExternalVisitResponse>> Handle(
        ListExternalVisitsQuery request, CancellationToken cancellationToken = default)
    {
        var visits = await store.ListVisitsAsync(request.UserId, request.SubprofileId, cancellationToken);
        return [.. visits.Select(PatientMapper.ToResponse)];
    }
}

public sealed record AddExternalVisitCommand(
    string UserId, string? SubprofileId, string? DoctorName, string? ClinicName, string? Specialty,
    DateTime? VisitDate, string? ChiefComplaint, string? Diagnosis, string? Notes,
    JsonElement? Medications, DateTime? FollowUpDate, string? FollowUpNotes)
    : IRequest<ExternalVisitResponse>;

public sealed class AddExternalVisitHandler(IPatientsDbContext dbContext, IExternalCareStore store)
    : IRequestHandler<AddExternalVisitCommand, ExternalVisitResponse>
{
    public async Task<ExternalVisitResponse> Handle(
        AddExternalVisitCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.DoctorName))
            throw new ValidationException("Doctor name is required");

        var visit = ExternalVisit.Create(
            request.UserId, request.SubprofileId, request.DoctorName, request.VisitDate,
            request.ClinicName, request.Specialty, request.ChiefComplaint, request.Diagnosis,
            request.Notes, SerializeMedications(request.Medications),
            request.FollowUpDate, request.FollowUpNotes);

        store.Add(visit);
        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(visit);
    }

    /// <summary>
    /// The medication list arrives as an arbitrary JSON array and is stored verbatim — the Node
    /// column was <c>Json?</c> and nothing on the server reads into it.
    /// </summary>
    internal static string? SerializeMedications(JsonElement? medications)
        => medications is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
            ? medications.Value.GetRawText()
            : null;
}

public sealed record UpdateExternalVisitCommand(
    string VisitId, string UserId, string? DoctorName, string? ClinicName, string? Specialty,
    DateTime? VisitDate, string? ChiefComplaint, string? Diagnosis, string? Notes,
    JsonElement? Medications, DateTime? FollowUpDate, string? FollowUpNotes)
    : IRequest<ExternalVisitResponse>;

public sealed class UpdateExternalVisitHandler(IPatientsDbContext dbContext, IExternalCareStore store)
    : IRequestHandler<UpdateExternalVisitCommand, ExternalVisitResponse>
{
    public async Task<ExternalVisitResponse> Handle(
        UpdateExternalVisitCommand request, CancellationToken cancellationToken = default)
    {
        var visit = await store.GetVisitAsync(request.VisitId, cancellationToken);

        // Scoped to the caller's own rows: an id belonging to another patient reads as missing.
        if (visit is null || visit.PatientUserId != request.UserId)
            throw new NotFoundException("External visit not found");

        visit.Update(
            request.DoctorName, request.ClinicName, request.Specialty, request.VisitDate,
            request.ChiefComplaint, request.Diagnosis, request.Notes,
            AddExternalVisitHandler.SerializeMedications(request.Medications),
            request.FollowUpDate, request.FollowUpNotes);

        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(visit);
    }
}

public sealed record RemoveExternalVisitCommand(string VisitId, string UserId) : IRequest<MessageResponse>;

public sealed class RemoveExternalVisitHandler(IPatientsDbContext dbContext, IExternalCareStore store)
    : IRequestHandler<RemoveExternalVisitCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        RemoveExternalVisitCommand request, CancellationToken cancellationToken = default)
    {
        var visit = await store.GetVisitAsync(request.VisitId, cancellationToken);

        if (visit is null || visit.PatientUserId != request.UserId)
            throw new NotFoundException("External visit not found");

        // A hard delete, matching the Node route: this is the patient's own note about care
        // elsewhere, not a clinical record other rows depend on.
        store.Remove(visit);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("External visit deleted");
    }
}

// ── External doctors ─────────────────────────────────────────────────────────

public sealed record ListExternalDoctorsQuery(string UserId, string? SubprofileId)
    : IRequest<IReadOnlyList<ExternalDoctorResponse>>;

public sealed class ListExternalDoctorsHandler(IExternalCareStore store)
    : IRequestHandler<ListExternalDoctorsQuery, IReadOnlyList<ExternalDoctorResponse>>
{
    public async Task<IReadOnlyList<ExternalDoctorResponse>> Handle(
        ListExternalDoctorsQuery request, CancellationToken cancellationToken = default)
    {
        var doctors = await store.ListDoctorsAsync(request.UserId, request.SubprofileId, cancellationToken);
        return [.. doctors.Select(PatientMapper.ToResponse)];
    }
}

public sealed record AddExternalDoctorCommand(
    string UserId, string? SubprofileId, string? Name, string? Specialty, string? ClinicName,
    string? Phone, string? Email, string? Address, string? Notes) : IRequest<ExternalDoctorResponse>;

public sealed class AddExternalDoctorHandler(IPatientsDbContext dbContext, IExternalCareStore store)
    : IRequestHandler<AddExternalDoctorCommand, ExternalDoctorResponse>
{
    public async Task<ExternalDoctorResponse> Handle(
        AddExternalDoctorCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationException("Doctor name is required");

        var doctor = ExternalDoctor.Create(
            request.UserId, request.SubprofileId, request.Name, request.Specialty,
            request.ClinicName, request.Phone, request.Email, request.Address, request.Notes);

        store.Add(doctor);
        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(doctor);
    }
}

public sealed record UpdateExternalDoctorCommand(
    string DoctorId, string UserId, string? Name, string? Specialty, string? ClinicName,
    string? Phone, string? Email, string? Address, string? Notes) : IRequest<ExternalDoctorResponse>;

public sealed class UpdateExternalDoctorHandler(IPatientsDbContext dbContext, IExternalCareStore store)
    : IRequestHandler<UpdateExternalDoctorCommand, ExternalDoctorResponse>
{
    public async Task<ExternalDoctorResponse> Handle(
        UpdateExternalDoctorCommand request, CancellationToken cancellationToken = default)
    {
        var doctor = await store.GetDoctorAsync(request.DoctorId, cancellationToken);

        if (doctor is null || doctor.PatientUserId != request.UserId)
            throw new NotFoundException("External doctor not found");

        doctor.Update(request.Name, request.Specialty, request.ClinicName,
                      request.Phone, request.Email, request.Address, request.Notes);

        await dbContext.SaveChangesAsync(cancellationToken);

        return PatientMapper.ToResponse(doctor);
    }
}

public sealed record RemoveExternalDoctorCommand(string DoctorId, string UserId) : IRequest<MessageResponse>;

public sealed class RemoveExternalDoctorHandler(IPatientsDbContext dbContext, IExternalCareStore store)
    : IRequestHandler<RemoveExternalDoctorCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        RemoveExternalDoctorCommand request, CancellationToken cancellationToken = default)
    {
        var doctor = await store.GetDoctorAsync(request.DoctorId, cancellationToken);

        if (doctor is null || doctor.PatientUserId != request.UserId)
            throw new NotFoundException("External doctor not found");

        store.Remove(doctor);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("External doctor deleted");
    }
}
