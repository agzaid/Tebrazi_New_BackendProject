using Tebrazi.Identity.Application.Abstractions.Persistence;
using Tebrazi.Identity.Application.ApiModels.Responses;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Identity.Application.UseCases.Commands.AcceptInvite;

/// <summary>
/// <c>POST /api/auth/accept-invite</c> — creates the user from an invitation and joins the
/// organization, all in one transaction (auth.js:601-626). 201 on success, like register.
///
/// The new user is <c>role = USER</c> and <c>userType = PATIENT</c> — Node passes neither to
/// <c>tx.user.create</c> (auth.js:604-612), so the schema default PATIENT applies and the
/// invitation's role lives on the MEMBERSHIP, not on the user.
/// </summary>
public sealed record AcceptInviteCommand(
    string? Token,
    string? DisplayName,
    string? Password) : IRequest<AcceptInviteResponse>;

public sealed record AcceptInviteResponse(string Token, AuthUserResponse User);
