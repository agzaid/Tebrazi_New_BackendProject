using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tebrazi.Clinics.Application.ApiModels.Responses;

/// <summary>
/// A clinic as the list and detail endpoints return it.
///
/// <c>workingHours</c> is emitted as a JSON object, not a string: the Postgres column was
/// <c>Json?</c> and the client reads into it. Serializing the stored text verbatim would give
/// the client a quoted string and break the schedule screens.
///
/// <c>staffRole</c> and <c>staffPermissions</c> are populated only for clinics the caller
/// reaches as staff; they are null for a physician's own clinics.
/// </summary>
public sealed record ClinicResponse(
    string Id,
    string OrganizationId,
    string PhysicianId,
    string Name,
    string? Address,
    string? City,
    string? Country,
    string? Phone,
    string? Email,
    JsonNode? WorkingHours,
    string? Specialty,
    string? Logo,
    bool IsActive,
    bool AllowPatientBooking,
    double? ConsultationFee,
    double? FollowUpFee,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    ClinicOrganizationResponse? Organization,
    ClinicPhysicianResponse? Physician,
    [property: JsonPropertyName("_count")] ClinicCountsResponse? Count,
    string? StaffRole,
    IReadOnlyDictionary<string, bool>? StaffPermissions);

public sealed record ClinicOrganizationResponse(string Name, string Slug);

/// <summary>The owning physician, flattened the way the Node include produced it.</summary>
public sealed record ClinicPhysicianResponse(
    string Id,
    string UserId,
    string Specialty,
    bool Verified,
    ClinicPhysicianUserResponse User);

public sealed record ClinicPhysicianUserResponse(string DisplayName);

/// <summary>Prisma <c>_count</c> aggregate, preserved under its original key.</summary>
public sealed record ClinicCountsResponse(
    [property: JsonPropertyName("staffMembers")] int StaffMembers,
    [property: JsonPropertyName("visits")] int Visits);

/// <summary>Lightweight shape for the sidebar clinic switcher — <c>GET /api/clinics/mine</c>.</summary>
public sealed record ClinicSummaryResponse(string Id, string Name, string? Specialty);

/// <summary>One selectable context in the context switcher.</summary>
public sealed record ContextResponse(
    string Mode,
    string? ClinicId,
    string Label,
    string? Specialty,
    string Icon,
    string? Role,
    IReadOnlyDictionary<string, bool>? Permissions,
    string? PhysicianName,
    bool? Active);

/// <summary>Body of <c>GET /api/clinics/my-context</c>.</summary>
public sealed record MyContextResponse(
    string UserId,
    string UserType,
    IReadOnlyList<ContextResponse> Contexts,
    bool HasClinicAccess);

/// <summary>Body of <c>GET /api/clinics/roles</c>.</summary>
public sealed record ClinicRolesResponse(
    IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> Permissions);

/// <summary>A staff member of a clinic, with the user fields the staff screen shows.</summary>
public sealed record ClinicStaffResponse(
    string Id,
    string ClinicId,
    string UserId,
    string Role,
    IReadOnlyDictionary<string, bool> Permissions,
    bool IsActive,
    DateTime CreatedAt,
    ClinicStaffUserResponse? User);

public sealed record ClinicStaffUserResponse(
    string Id,
    string? Email,
    string DisplayName,
    string? Phone,
    [property: JsonPropertyName("profilePictureUrl")] string? ProfilePictureUrl);

/// <summary>A pending staff invitation.</summary>
public sealed record StaffInvitationResponse(
    string Id,
    string ClinicId,
    string Email,
    string Role,
    string Status,
    DateTime ExpiresAt,
    DateTime? AcceptedAt,
    DateTime CreatedAt,
    string? InviteUrl);

/// <summary>What <c>GET /api/clinics/invite/:token</c> returns to the acceptance screen.</summary>
public sealed record InvitationPreviewResponse(
    string ClinicId,
    string ClinicName,
    string Email,
    string Role,
    string? PhysicianName,
    DateTime ExpiresAt);

/// <summary>Body of <c>POST /api/clinics/:id/generate-staff-pin</c>.</summary>
public sealed record StaffPinResponse(
    string Pin,
    string Role,
    string ClinicName,
    DateTime ExpiresAt,
    int ExpiresInSeconds);

/// <summary>
/// Body of <c>POST /api/clinics/join-by-pin</c>. Fields beyond <c>message</c> are optional
/// because the Node route returns three different shapes depending on whether the caller
/// joined, re-joined, or was already a member.
/// </summary>
public sealed record JoinClinicResponse(
    string Message,
    string? ClinicId,
    string? ClinicName,
    string? Role,
    string? PhysicianName,
    bool? AlreadyMember);
