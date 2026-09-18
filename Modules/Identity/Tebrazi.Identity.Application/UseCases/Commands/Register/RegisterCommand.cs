using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.Register;

/// <summary>
/// <c>POST /api/auth/register</c> — self-service signup. Creates, in one transaction:
/// organization, user, an OWNER membership, a subscription, and for physicians a
/// PhysicianProfile.
/// </summary>
public sealed record RegisterCommand(
    string? DisplayName,
    string? Email,
    string? Password,
    string? OrganizationName,
    string? Plan,
    string? CompanySize,
    string? Phone,
    string? UserType,
    string? LicenseNumber,
    string? Specialty) : IRequest<RegisterResponse>;
