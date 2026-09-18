using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tebrazi.Common.Api.Controllers;
using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.Identity.Application.UseCases.Commands.Login;
using Tebrazi.Identity.Application.UseCases.Commands.AcceptInvite;
using Tebrazi.Identity.Application.UseCases.Commands.CheckPhone;
using Tebrazi.Identity.Application.UseCases.Commands.ClaimAccount;
using Tebrazi.Identity.Application.UseCases.Commands.DeleteAccount;
using Tebrazi.Identity.Application.UseCases.Commands.ForgotPassword;
using Tebrazi.Identity.Application.UseCases.Commands.Register;
using Tebrazi.Identity.Application.UseCases.Commands.RequestOtp;
using Tebrazi.Identity.Application.UseCases.Commands.ResetPassword;
using Tebrazi.Identity.Application.UseCases.Commands.RevokeOtherSessions;
using Tebrazi.Identity.Application.UseCases.Commands.RevokeSession;
using Tebrazi.Identity.Application.UseCases.Commands.DeleteProfilePicture;
using Tebrazi.Identity.Application.UseCases.Commands.UploadProfilePicture;
using Tebrazi.Identity.Application.UseCases.Queries.ExportData;
using Tebrazi.Identity.Application.UseCases.Queries.ExportMyData;
using Tebrazi.Identity.Application.UseCases.Queries.GetCurrentUser;
using Tebrazi.Identity.Application.UseCases.Queries.InviteByToken;
using Tebrazi.Identity.Application.UseCases.Queries.ListSessions;
using Tebrazi.Identity.Application.UseCases.Queries.ValidateResetToken;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Api.Controllers;

/// <summary>
/// <c>/api/auth</c> — the port of <c>server/src/routes/auth.js</c>.
/// Routes, verbs, status codes and body shapes match the Node originals exactly, so the React
/// client reaches these by changing VITE_API_URL and nothing else.
/// </summary>
[Route("api/auth")]
public sealed class AuthController(IMediator mediator, ICurrentUser currentUser)
    : BaseApiController(mediator)
{
    /// <summary>
    /// <c>POST /api/auth/login</c>. Accepts an email or a phone number as <c>username</c>.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await Send(new LoginCommand(
            Username: request.Username,
            Email: request.Email,
            Password: request.Password,
            LoginAs: request.LoginAs,
            LoginContext: request.LoginContext,
            DeviceInfo: Request.Headers.UserAgent.ToString(),
            IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString()));

        return Payload(result);
    }

    /// <summary>
    /// <c>POST /api/auth/logout</c>. Stateless, as in the Node route: the client discards the
    /// token. Kept so the client's logout call does not 404.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout() => Acknowledge("Logout successful");

    /// <summary><c>POST /api/auth/register</c> — returns 201.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await Send(new RegisterCommand(
            DisplayName: request.DisplayName,
            Email: request.Email,
            Password: request.Password,
            OrganizationName: request.OrganizationName,
            Plan: request.Plan,
            CompanySize: request.CompanySize,
            Phone: request.Phone,
            UserType: request.UserType,
            LicenseNumber: request.LicenseNumber,
            Specialty: request.Specialty));

        return CreatedPayload(result);
    }

    /// <summary>
    /// <c>GET /api/auth/me</c>. Returns the user flat, not nested under a <c>user</c> key.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CurrentUserResponse>> Me()
    {
        var userId = currentUser.UserId
            ?? throw new UnauthorizedException("Invalid token");

        return Ok(await Send(new GetCurrentUserQuery(userId)));
    }
    /// <summary>
    /// <c>GET /api/auth/invite/{token}</c> — public. 404 unknown, 410 accepted or expired.
    /// </summary>
    [HttpGet("invite/{token}")]
    [AllowAnonymous]
    [ProducesResponseType<InviteByTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> InviteByToken(string token)
        => Payload(await Send(new InviteByTokenQuery(token)));

    /// <summary>
    /// <c>POST /api/auth/accept-invite</c> — public. 201 like register; the invitee leaves the
    /// endpoint signed in.
    /// </summary>
    [HttpPost("accept-invite")]
    [AllowAnonymous]
    [ProducesResponseType<AcceptInviteResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> AcceptInvite([FromBody] AcceptInviteRequest request)
        => CreatedPayload(await Send(new AcceptInviteCommand(
            Token: request.Token,
            DisplayName: request.DisplayName,
            Password: request.Password)));

    /// <summary><c>POST /api/auth/forgot-password</c> — public; always answers 200.</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [ProducesResponseType<ForgotPasswordResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        => Payload(await Send(new ForgotPasswordCommand(request.Email)));

    /// <summary><c>POST /api/auth/reset-password</c> — public.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [ProducesResponseType<ResetPasswordResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        => Payload(await Send(new ResetPasswordCommand(request.Token, request.Password)));

    /// <summary><c>GET /api/auth/reset-password/{token}</c> — the pre-form validity check.</summary>
    [HttpGet("reset-password/{token}")]
    [AllowAnonymous]
    [ProducesResponseType<ValidateResetTokenResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateResetToken(string token)
        => Payload(await Send(new ValidateResetTokenQuery(token)));

    /// <summary><c>POST /api/auth/check-phone</c> — public, the claim wizard's first step.</summary>
    [HttpPost("check-phone")]
    [AllowAnonymous]
    [ProducesResponseType<CheckPhoneResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckPhone([FromBody] CheckPhoneRequest request)
        => Payload(await Send(new CheckPhoneCommand(request.Phone)));

    /// <summary><c>POST /api/auth/request-otp</c> — public; 429 after three codes in ten minutes.</summary>
    [HttpPost("request-otp")]
    [AllowAnonymous]
    [ProducesResponseType<RequestOtpResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestOtp([FromBody] RequestOtpRequest request)
        => Payload(await Send(new RequestOtpCommand(request.Phone)));

    /// <summary><c>POST /api/auth/claim-account</c> — public; upgrades a stub account.</summary>
    [HttpPost("claim-account")]
    [AllowAnonymous]
    [ProducesResponseType<ClaimAccountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClaimAccount([FromBody] ClaimAccountRequest request)
        => Payload(await Send(new ClaimAccountCommand(
            Phone: request.Phone,
            OtpCode: request.OtpCode,
            Email: request.Email,
            Password: request.Password,
            DisplayName: request.DisplayName)));

    /// <summary>
    /// <c>DELETE /api/auth/delete-account</c>. Node reads the token by hand and answers its own
    /// 401 <c>{"error":"No token"}</c>; this port lets the JWT middleware emit the standard 401
    /// body instead, which the client already handles. SOFT-delete only.
    /// </summary>
    [HttpDelete("delete-account")]
    [Authorize]
    [ProducesResponseType<DeleteAccountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest request)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("No token");
        return Payload(await Send(new DeleteAccountCommand(userId, request.Confirmation)));
    }

    /// <summary><c>GET /api/auth/sessions</c> — the active-sessions screen.</summary>
    [HttpGet("sessions")]
    [Authorize]
    [ProducesResponseType<SessionResponse[]>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Sessions()
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Invalid token");
        var currentToken = bearerToken();
        return Ok(await Send(new ListSessionsQuery(userId, currentToken)));
    }

    /// <summary><c>DELETE /api/auth/sessions/{id}</c> — revoke one device.</summary>
    [HttpDelete("sessions/{id}")]
    [Authorize]
    [ProducesResponseType<RevokeSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSession(string id)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Invalid token");
        return Payload(await Send(new RevokeSessionCommand(userId, id)));
    }

    /// <summary><c>DELETE /api/auth/sessions</c> — revoke every OTHER device.</summary>
    [HttpDelete("sessions")]
    [Authorize]
    [ProducesResponseType<RevokeOtherSessionsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeAllSessions()
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Invalid token");
        return Payload(await Send(new RevokeOtherSessionsCommand(userId, bearerToken())));
    }

    /// <summary>
    /// <c>GET /api/auth/export-data</c> — the account-switcher export. Node's auth is a hand
    ///rolled check with <c>{"error":"No token"}</c>; the middleware 401 here carries the
    /// standard body.
    /// </summary>
    [HttpGet("export-data")]
    [Authorize]
    [ProducesResponseType<ExportDataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportData()
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("No token");
        var data = await Send(new ExportDataQuery(userId));
        var filename = $"tebrazi-data-export-{DateTime.UtcNow:yyyy-MM-dd}.json";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        return Ok(data);
    }

    /// <summary><c>GET /api/auth/export-my-data</c> — the GDPR export (authCheck).</summary>
    [HttpGet("export-my-data")]
    [Authorize]
    [ProducesResponseType<ExportMyDataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportMyData()
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Invalid token");
        var data = await Send(new ExportMyDataQuery(userId));
        var filename = $"tebrazi-data-export-{userId[..Math.Min(8, userId.Length)]}-{DateTime.UtcNow:yyyy-MM-dd}.json";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        return Ok(data);
    }

    /// <summary>
    /// <c>POST /api/auth/profile-picture</c> — multipart form, field <c>profilePicture</c>.
    /// Multer's 5MB cap becomes the shared FileStorageOptions limit; the failure messages match
    /// Node's multer text.
    /// </summary>
    [HttpPost("profile-picture")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadProfilePicture(IFormFile profilePicture)
    {
        if (profilePicture is null || profilePicture.Length == 0)
            return BadRequest(new { error = "No image provided" });

        var userId = currentUser.UserId ?? throw new UnauthorizedException("Invalid token");
        var result = await Send(new UploadProfilePictureCommand(
            UserId: userId,
            FileName: profilePicture.FileName,
            ContentType: profilePicture.ContentType,
            Content: profilePicture.OpenReadStream()));

        return Payload(result);
    }

    /// <summary><c>DELETE /api/auth/profile-picture</c>.</summary>
    [HttpDelete("profile-picture")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteProfilePicture()
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Invalid token");
        return Payload(await Send(new DeleteProfilePictureCommand(userId)));
    }

    private string bearerToken() =>
        Request.Headers.Authorization.ToString().StartsWith("Bearer ")
            ? Request.Headers.Authorization.ToString()["Bearer ".Length..]
            : throw new UnauthorizedException("Invalid token");
}

/// <summary>
/// Login body. Every field is optional at the binding layer so a missing password produces the
/// handler's own 400 <c>{ error: "Validation failed" }</c> rather than an ASP.NET
/// ProblemDetails body the client does not understand.
/// </summary>
public sealed record LoginRequest(
    string? Username,
    string? Email,
    string? Password,
    string? LoginAs,
    string? LoginContext);

public sealed record RegisterRequest(
    string? DisplayName,
    string? Email,
    string? Password,
    string? OrganizationName,
    string? Plan,
    string? CompanySize,
    string? Phone,
    string? UserType,
    string? LicenseNumber,
    string? Specialty);

public sealed record AcceptInviteRequest(string? Token, string? DisplayName, string? Password);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? Password);

public sealed record CheckPhoneRequest(string? Phone);

public sealed record RequestOtpRequest(string? Phone);

public sealed record ClaimAccountRequest(string? Phone, string? OtpCode, string? Email, string? Password, string? DisplayName);

public sealed record DeleteAccountRequest(string? Confirmation);
