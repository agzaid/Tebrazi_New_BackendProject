using Tebrazi.SharedKernel.Exceptions;

namespace Tebrazi.Identity.Application.UseCases.Commands.Login;

/// <summary>
/// 401 with the Node route's distinctive body:
/// <c>{ error: "WrongPortal", message: "This is a X account.", redirectRole: "x" }</c>.
///
/// The login screen branches on <c>error === "WrongPortal"</c> and redirects using
/// <c>redirectRole</c>, so this needs its own shape rather than a plain 401.
/// </summary>
public sealed class WrongPortalException(string role)
    : AppException("WrongPortal", $"This is a {role} account.")
{
    public override int StatusCode => 401;

    /// <summary>Lowercased role, the value the client redirects on.</summary>
    public string RedirectRole { get; } = role.ToLowerInvariant();

    public override IReadOnlyDictionary<string, object?> AdditionalFields
        => new Dictionary<string, object?> { ["redirectRole"] = RedirectRole };
}
