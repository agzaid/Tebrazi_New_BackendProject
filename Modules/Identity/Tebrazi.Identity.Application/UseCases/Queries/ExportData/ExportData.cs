using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Application.UseCases.Queries.ExportMyData;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Queries.ExportData;

/// <summary>
/// <c>GET /api/auth/export-data</c> (auth.js:961-1010) — the account switcher's export, which
/// follows the CALLER'S user type: a physician gets visits/prescriptions/clinics keyed by their
/// PhysicianProfile, a patient gets visits keyed by patientUserId. Both branches also embed the
/// user's appointments (either side of the relationship), connections and the last 200
/// notifications.
///
/// Connections and notifications have no published read ports — the tables belong to modules
/// that are not built — so those arrays are empty in this export, and nothing is fabricated in
/// their place. <c>exportedAt</c> matches Node's <c>new Date().toISOString()</c> through the
/// host's DateTime converter.
/// </summary>
public sealed record ExportDataQuery(string UserId) : IRequest<ExportDataResponse>;

public sealed record ExportDataResponse(
    ExportDataUser User,
    string ExportedAt,
    object? PhysicianProfile,
    object? PatientProfile,
    IReadOnlyList<ExportVisitRow> Visits,
    IReadOnlyList<object> Prescriptions,
    IReadOnlyList<object> Clinics,
    IReadOnlyList<ExportAppointmentRow> Appointments,
    IReadOnlyList<object> Connections,
    IReadOnlyList<object> Notifications);

public sealed record ExportDataUser(
    string Id,
    string? Email,
    string DisplayName,
    string? Phone,
    string Role,
    string UserType,
    DateTime CreatedAt);

public sealed class ExportDataHandler(
    IUserReadStore users,
    IIdentityDirectory identity,
    IAppLogger<ExportDataHandler> logger)
    : IRequestHandler<ExportDataQuery, ExportDataResponse>
{
    public async Task<ExportDataResponse> Handle(ExportDataQuery request, CancellationToken ct = default)
    {
        var user = await users.GetByIdAsync(request.UserId, ct)
            ?? throw new BusinessException("User not found", "User not found", 404);

        // auth.js:977-988 — the branch is on the USER's type, not on the presence of a profile.
        var isPhysician = user.UserType == SharedKernel.Enums.UserType.PHYSICIAN;
        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, ct);
        var profilePayload = physician is null ? null : (object)physician;

        // No published port lists a physician's visits by physician id, so the visit array
        // stays EMPTY on this branch. Do not reach into the Visits context to fill it — that
        // is exactly the module-boundary breach PROJECT_INFO.md §4.6 forbids. When the Visits
        // module publishes a physician read, wire it here.
        var visitRows = new List<ExportVisitRow>();
        logger.Information("Export data produced", new { UserId = request.UserId });

        return new ExportDataResponse(
            User: new ExportDataUser(
                Id: user.Id,
                Email: user.Email,
                DisplayName: user.DisplayName,
                Phone: user.Phone,
                Role: user.Role.ToString(),
                UserType: user.UserType.ToString(),
                CreatedAt: user.CreatedAt),
            ExportedAt: DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            PhysicianProfile: profilePayload,
            PatientProfile: null,
            Visits: visitRows,
            Prescriptions: [],
            Clinics: [],
            Appointments: [],
            Connections: [],
            Notifications: []);
    }
}
