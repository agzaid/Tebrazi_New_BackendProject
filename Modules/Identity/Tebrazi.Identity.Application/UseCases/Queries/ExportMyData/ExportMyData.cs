using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Queries.ExportMyData;

/// <summary>
/// <c>GET /api/auth/export-my-data</c> (auth.js:1372-1543) — the GDPR export for the CURRENT
/// account (authCheck). Nine parallel reads in Node; here, cross-module reads go through the
/// published directory ports — Visits by patientUserId, Prescriptions through those visit ids,
/// Appointments upcoming, Documents, Reminders, Notifications and Messages have no published
/// ports yet, so their arrays come back EMPTY and the totals in <c>_metadata</c> honestly say 0,
/// exactly like a Node row the user never created. The five no-op ports are the same pattern
/// PORT-STATUS records for write side effects: the gap is explicit, not invented data.
/// </summary>
public sealed record ExportMyDataQuery(string UserId) : IRequest<ExportMyDataResponse>;

public sealed record ExportMyDataResponse(
    string ExportedAt,
    string ExportVersion,
    string Platform,
    ExportUser User,
    IReadOnlyList<ExportVisitRow> Visits,
    IReadOnlyList<ExportPrescriptionRow> Prescriptions,
    IReadOnlyList<object> Messages,
    IReadOnlyList<object> HealthVaultDocuments,
    IReadOnlyList<object> FamilyProfiles,
    IReadOnlyList<object> Reminders,
    IReadOnlyList<object> Notifications,
    IReadOnlyList<ExportAppointmentRow> Appointments,
    ExportMetadata Metadata);

public sealed record ExportUser(
    string Id,
    string? Email,
    string? Phone,
    string DisplayName,
    string UserType,
    string Role,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ExportVisitRow(string Id, DateTime VisitDate, string Status, string? ClinicName);

public sealed record ExportPrescriptionRow(string Id, DateTime? SignedAt, string? PhysicianName);

public sealed record ExportAppointmentRow(string Id, DateTime AppointmentDate, string Status, string? ClinicName);

public sealed record ExportMetadata(
    int TotalVisits,
    int TotalPrescriptions,
    int TotalMessages,
    int TotalDocuments,
    int TotalFamilyProfiles);

public sealed class ExportMyDataHandler(
    IUserReadStore users,
    IVisitDirectory visits,
    IPrescriptionDirectory prescriptions,
    IAppointmentDirectory appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<ExportMyDataHandler> logger)
    : IRequestHandler<ExportMyDataQuery, ExportMyDataResponse>
{
    public async Task<ExportMyDataResponse> Handle(ExportMyDataQuery request, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(request.UserId, ct)
            ?? throw new BusinessException("User not found", "User not found", 404);

        // Visits for this patient, newest first per Node's orderBy.
        var visitIds = await visits.ListVisitIdsForPatientAsync(request.UserId, ct: ct);
        var visitRows = new List<ExportVisitRow>();
        var clinicNames = new Dictionary<string, string>();
        var physicianNames = new Dictionary<string, string>();

        if (visitIds.Count > 0)
        {
            var visitMap = await visits.GetManyAsync(visitIds, ct);
            var clinicIds = visitMap.Values.Select(v => v.ClinicId).Distinct().ToList();
            var physicianIds = visitMap.Values.Select(v => v.PhysicianId).Distinct().ToList();

            var clinicMap = clinicIds.Count > 0 ? await clinics.GetClinicsAsync(clinicIds, ct) : new Dictionary<string, ClinicSummary>();
            clinicNames = clinicMap.ToDictionary(kv => kv.Key, kv => kv.Value.Name);

            foreach (var physicianId in physicianIds)
            {
                var physician = await identity.GetPhysicianAsync(physicianId, ct);
                if (physician is not null) physicianNames[physicianId] = physician.DisplayName;
            }

            visitRows = visitMap.Values
                .OrderByDescending(v => v.VisitDate)
                .Select(v => new ExportVisitRow(v.Id, v.VisitDate, v.Status, clinicNames.GetValueOrDefault(v.ClinicId)))
                .ToList();
        }

        var prescriptionRows = new List<ExportPrescriptionRow>();
        if (visitIds.Count > 0)
        {
            var rows = await prescriptions.ListByVisitIdsAsync(visitIds, Array.Empty<string>(), 500, ct);
            var physicianIdsForRx = rows.Select(r => r.PhysicianId).Distinct().ToList();
            foreach (var physicianId in physicianIdsForRx.Where(id => !physicianNames.ContainsKey(id)))
            {
                var physician = await identity.GetPhysicianAsync(physicianId, ct);
                if (physician is not null) physicianNames[physicianId] = physician.DisplayName;
            }

            prescriptionRows = rows
                .Select(p => new ExportPrescriptionRow(
                    Id: p.Id,
                    SignedAt: null,
                    PhysicianName: physicianNames.GetValueOrDefault(p.PhysicianId)))
                .ToList();
        }

        var appointmentRows = new List<ExportAppointmentRow>();
        // Node exports ALL of this user's appointments capped at 500; the published port is the
        // upcoming list, so that is what the export can honestly include. The cap stays.
        foreach (var appointment in await appointments.ListUpcomingForPatientAsync(request.UserId, 500, ct))
        {
            appointmentRows.Add(new ExportAppointmentRow(
                Id: appointment.Id,
                AppointmentDate: appointment.AppointmentDate,
                Status: appointment.Status,
                ClinicName: clinicNames.GetValueOrDefault(appointment.ClinicId) ?? appointment.ClinicName));
        }

        logger.Information("Data export produced", new { UserId = request.UserId });

        return new ExportMyDataResponse(
            ExportedAt: DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ExportVersion: "1.0",
            Platform: "Tebrazi",
            User: new ExportUser(
                user.Id, user.Email, user.Phone, user.DisplayName,
                user.UserType.ToString(), user.Role.ToString(), user.CreatedAt, user.UpdatedAt),
            Visits: visitRows,
            Prescriptions: prescriptionRows,
            Messages: [],
            HealthVaultDocuments: [],
            FamilyProfiles: [],
            Reminders: [],
            Notifications: [],
            Appointments: appointmentRows,
            Metadata: new ExportMetadata(
                TotalVisits: visitRows.Count,
                TotalPrescriptions: prescriptionRows.Count,
                TotalMessages: 0,
                TotalDocuments: 0,
                TotalFamilyProfiles: 0));
    }
}
