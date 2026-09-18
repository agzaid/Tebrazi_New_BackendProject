using Tebrazi.SharedKernel.Abstractions.Persistence;

namespace Tebrazi.Identity.Application.Abstractions.Persistence;

/// <summary>
/// The Identity module's unit of work. Handlers inject THIS, never the concrete
/// <c>IdentityDbContext</c> and never the bare <c>IDbContext</c>.
///
/// This is the boundary that was breached in KACCC, where five of six modules injected the
/// concrete IdentityDbContext and reached into Identity's tables directly. Anything outside
/// this module that needs identity data goes through a published service port instead.
/// </summary>
public interface IIdentityDbContext : IDbContext;
