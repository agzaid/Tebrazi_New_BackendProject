using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tebrazi.Clinics.Application.ApiModels.Responses;
using Tebrazi.Clinics.Application.UseCases.Commands;
using Tebrazi.Clinics.Application.UseCases.Queries;
using Tebrazi.Clinics.Application.UseCases.Queries.ListClinics;
using Tebrazi.Clinics.Application.UseCases.Queries.MyContext;
using Tebrazi.Common.Api.Controllers;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Api.Controllers;

/// <summary>
/// <c>/api/clinics</c> — the port of <c>server/src/routes/clinics.js</c>.
///
/// Route ORDER matters and mirrors the Node file: the literal segments (<c>mine</c>,
/// <c>my-context</c>, <c>directory</c>, <c>roles</c>, <c>invite/...</c>, <c>join-by-pin</c>)
/// are declared before <c>{id}</c>, or ASP.NET would bind "mine" as a clinic id.
/// </summary>
[Route("api/clinics")]
[Authorize]
public sealed class ClinicsController(IMediator mediator, ICurrentUser currentUser)
    : BaseApiController(mediator)
{
    private string UserId => currentUser.UserId ?? throw new UnauthorizedException("Invalid token");

    /// <summary>Every clinic the caller can reach — owned as a physician, or joined as staff.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ClinicResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
        => Payload(await Send(new ListClinicsQuery(UserId, currentUser.UserType)));

    /// <summary>Lightweight list for the sidebar clinic switcher. Empty for non-physicians.</summary>
    [HttpGet("mine")]
    [ProducesResponseType<IReadOnlyList<ClinicSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine()
        => Payload(await Send(new ListMyClinicsQuery(UserId, currentUser.UserType)));

    /// <summary>The contexts available to this user: personal, physician, and clinic staff.</summary>
    [HttpGet("my-context")]
    [ProducesResponseType<MyContextResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MyContext()
        => Payload(await Send(new GetMyContextQuery(UserId, currentUser.UserType)));

    /// <summary>The public clinic directory, paged.</summary>
    [HttpGet("directory")]
    [ProducesResponseType<ClinicDirectoryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Directory(
        [FromQuery] string? search,
        [FromQuery] string? specialty,
        [FromQuery] string? city,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
        => Payload(await Send(new ClinicDirectoryQuery(search, specialty, city, page, pageSize)));

    /// <summary>Clinic roles and their permission presets.</summary>
    [HttpGet("roles")]
    [ProducesResponseType<ClinicRolesResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Roles()
        => Payload(await Send(new GetClinicRolesQuery()));

    /// <summary>Previews a staff invitation. Anonymous — the recipient may not have an account yet.</summary>
    [HttpGet("invite/{token}")]
    [AllowAnonymous]
    [ProducesResponseType<InvitationPreviewResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> InvitePreview(string token)
        => Payload(await Send(new GetInvitationPreviewQuery(token)));

    /// <summary>Accepts a staff invitation for the signed-in caller.</summary>
    [HttpPost("invite/{token}/accept")]
    [ProducesResponseType<JoinClinicResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AcceptInvite(string token)
        => Payload(await Send(new AcceptStaffInvitationCommand(token, UserId)));

    /// <summary>Joins a clinic using a 4-digit PIN read out by the physician.</summary>
    [HttpPost("join-by-pin")]
    [ProducesResponseType<JoinClinicResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> JoinByPin([FromBody] JoinByPinRequest request)
        => Payload(await Send(new JoinClinicByPinCommand(UserId, request.Pin)));

    /// <summary>Creates a clinic, provisioning its organization and subscription. Physicians only.</summary>
    [HttpPost]
    [ProducesResponseType<ClinicResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateClinicRequest request)
        => CreatedPayload(await Send(new CreateClinicCommand(
            UserId, currentUser.UserType, request.Name, request.Address, request.City,
            request.Country, request.Phone, request.Email, request.Specialty, request.WorkingHours)));

    /// <summary>One clinic. Readable by the owning physician or its active staff.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ClinicResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id)
        => Payload(await Send(new GetClinicQuery(id, UserId)));

    /// <summary>Updates a clinic. Owner only; a partial body updates only what it carries.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType<ClinicResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateClinicRequest request)
        => Payload(await Send(new UpdateClinicCommand(
            id, UserId, request.Name, request.Address, request.City, request.Country,
            request.Phone, request.Email, request.Specialty, request.WorkingHours,
            request.AllowPatientBooking, request.ConsultationFee, request.FollowUpFee)));

    /// <summary>Deletes a clinic and its organization. Owner only, and irreversible.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(string id)
        => Payload(await Send(new DeleteClinicCommand(id, UserId)));

    /// <summary>Staff at a clinic, with the user details the staff screen renders.</summary>
    [HttpGet("{id}/staff")]
    [ProducesResponseType<IReadOnlyList<ClinicStaffResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Staff(string id)
        => Payload(await Send(new ListClinicStaffQuery(id, UserId)));

    /// <summary>Changes a staff member's role or permissions. Owner only.</summary>
    [HttpPut("{id}/staff/{staffId}")]
    [ProducesResponseType<ClinicStaffResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateStaff(
        string id, string staffId, [FromBody] UpdateStaffRequest request)
        => Payload(await Send(new UpdateClinicStaffCommand(
            id, staffId, UserId, request.Role, request.Permissions, request.IsActive)));

    /// <summary>Removes a staff member. Owner only.</summary>
    [HttpDelete("{id}/staff/{staffId}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveStaff(string id, string staffId)
        => Payload(await Send(new RemoveClinicStaffCommand(id, staffId, UserId)));

    /// <summary>Invites someone by email. Registered users are added straight away.</summary>
    [HttpPost("{id}/invite")]
    [ProducesResponseType<InviteStaffResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Invite(string id, [FromBody] InviteStaffRequest request)
        => Payload(await Send(new InviteStaffCommand(
            id, UserId, request.Email, request.Role, request.Message)));

    /// <summary>Outstanding invitations for a clinic. Owner only.</summary>
    [HttpGet("{id}/invites")]
    [ProducesResponseType<IReadOnlyList<StaffInvitationResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Invites(string id)
        => Payload(await Send(new ListStaffInvitationsQuery(id, UserId)));

    /// <summary>Revokes an invitation. Owner only.</summary>
    [HttpDelete("{id}/invites/{inviteId}")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeInvite(string id, string inviteId)
        => Payload(await Send(new RevokeStaffInvitationCommand(id, inviteId, UserId)));

    /// <summary>Issues a 4-digit staff PIN, valid five minutes. Owner only.</summary>
    [HttpPost("{id}/generate-staff-pin")]
    [ProducesResponseType<StaffPinResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GenerateStaffPin(string id, [FromBody] GenerateStaffPinRequest request)
        => Payload(await Send(new GenerateStaffPinCommand(id, UserId, request.Role)));
}

public sealed record JoinByPinRequest(string? Pin);

public sealed record GenerateStaffPinRequest(string? Role);

public sealed record CreateClinicRequest(
    string? Name, string? Address, string? City, string? Country,
    string? Phone, string? Email, string? Specialty, string? WorkingHours);

/// <summary>
/// Update body. Every field is nullable and null means "leave unchanged" — the Node route only
/// writes keys present in the request, and a PUT from a partial form must not blank the rest.
/// </summary>
public sealed record UpdateClinicRequest(
    string? Name, string? Address, string? City, string? Country,
    string? Phone, string? Email, string? Specialty, string? WorkingHours,
    bool? AllowPatientBooking, double? ConsultationFee, double? FollowUpFee);

public sealed record UpdateStaffRequest(
    string? Role, Dictionary<string, bool>? Permissions, bool? IsActive);

public sealed record InviteStaffRequest(string? Email, string? Role, string? Message);
