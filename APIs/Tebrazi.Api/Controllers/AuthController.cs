using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tebrazi.Common.Api.Controllers;
using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.Identity.Application.UseCases.Commands.Login;
using Tebrazi.Identity.Application.UseCases.Commands.Register;
using Tebrazi.Identity.Application.UseCases.Queries.GetCurrentUser;
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
