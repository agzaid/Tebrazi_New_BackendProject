using System.Text.Json.Nodes;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Application.Services;
using Tebrazi.Clinics.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Authorization;

namespace Tebrazi.Clinics.Application.Mapping;

/// <summary>
/// Turns entities into the response shapes the Node routes produced. Kept in one place so the
/// several endpoints that return a clinic cannot drift apart.
/// </summary>
public static class ClinicMapper
{
    public static ClinicResponse ToResponse(
        Clinic clinic,
        ClinicOrganizationResponse? organization = null,
        PhysicianSummary? physician = null,
        int? staffCount = null,
        int? visitCount = null,
        string? staffRole = null,
        IReadOnlyDictionary<string, bool>? staffPermissions = null)
        => new(
            Id: clinic.Id,
            OrganizationId: clinic.OrganizationId,
            PhysicianId: clinic.PhysicianId,
            Name: clinic.Name,
            Address: clinic.Address,
            City: clinic.City,
            Country: clinic.Country,
            Phone: clinic.Phone,
            Email: clinic.Email,
            WorkingHours: ParseJson(clinic.WorkingHours),
            Specialty: clinic.Specialty,
            Logo: clinic.Logo,
            IsActive: clinic.IsActive,
            AllowPatientBooking: clinic.AllowPatientBooking,
            ConsultationFee: clinic.ConsultationFee,
            FollowUpFee: clinic.FollowUpFee,
            CreatedAt: clinic.CreatedAt,
            UpdatedAt: clinic.UpdatedAt,
            Organization: organization,
            Physician: physician is null
                ? null
                : new ClinicPhysicianResponse(
                    physician.Id, physician.UserId, physician.Specialty, physician.Verified,
                    new ClinicPhysicianUserResponse(physician.DisplayName)),
            Count: staffCount is null && visitCount is null
                ? null
                : new ClinicCountsResponse(staffCount ?? 0, visitCount ?? 0),
            StaffRole: staffRole,
            StaffPermissions: staffPermissions);

    public static ClinicSummaryResponse ToSummary(Clinic clinic)
        => new(clinic.Id, clinic.Name, clinic.Specialty);

    public static ClinicStaffResponse ToResponse(ClinicStaff staff, UserSummary? user)
        => new(
            Id: staff.Id,
            ClinicId: staff.ClinicId,
            UserId: staff.UserId,
            Role: staff.Role,
            Permissions: ClinicPermissionMatrix.Resolve(
                staff.Role, ClinicAccessEvaluator.ParseOverrides(staff.Permissions)),
            IsActive: staff.IsActive,
            CreatedAt: staff.CreatedAt,
            User: user is null
                ? null
                : new ClinicStaffUserResponse(
                    user.Id, user.Email, user.DisplayName, user.Phone, user.ProfilePictureUrl));

    public static StaffInvitationResponse ToResponse(StaffInvitation invitation, string? clientUrl = null)
        => new(
            Id: invitation.Id,
            ClinicId: invitation.ClinicId,
            Email: invitation.Email,
            Role: invitation.Role,
            Status: invitation.Status.ToString(),
            ExpiresAt: invitation.ExpiresAt,
            AcceptedAt: invitation.AcceptedAt,
            CreatedAt: invitation.CreatedAt,
            InviteUrl: string.IsNullOrEmpty(clientUrl)
                ? null
                : $"{clientUrl.TrimEnd('/')}/staff-invite/{invitation.Token}");

    /// <summary>
    /// Reads a stored JSON column back into a node so it serializes as an object. Invalid JSON
    /// yields null rather than throwing — one malformed row must not fail the whole list.
    /// </summary>
    private static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
