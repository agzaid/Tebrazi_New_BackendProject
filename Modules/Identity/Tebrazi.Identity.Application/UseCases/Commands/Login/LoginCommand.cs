using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.Login;

/// <summary>
/// <c>POST /api/auth/login</c>.
///
/// <paramref name="Username"/> is an email OR a phone number — the Node route auto-detects
/// which. <paramref name="Email"/> is accepted as an alias because parts of the client still
/// post that field name.
/// </summary>
/// <param name="LoginAs">
/// Optional user type, sent when one email address holds both a physician and a patient
/// account, to pick which one to authenticate.
/// </param>
/// <param name="LoginContext">
/// Optional portal name for role-aware login. "default" (or absent) allows every role.
/// </param>
public sealed record LoginCommand(
    string? Username,
    string? Email,
    string? Password,
    string? LoginAs,
    string? LoginContext,
    string? DeviceInfo,
    string? IpAddress) : IRequest<LoginResponse>;
