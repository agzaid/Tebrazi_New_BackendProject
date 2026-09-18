using Microsoft.EntityFrameworkCore;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.Prescriptions.Application.Abstractions.Persistence;
using Tebrazi.Prescriptions.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Prescriptions.Persistence;

/// <summary>
/// The Prescriptions module's context: <c>prescriptions</c>.
/// </summary>
public sealed class PrescriptionsDbContext(
    DbContextOptions<PrescriptionsDbContext> options,
    ICurrentUser currentUser)
    : BaseDbContext(options, currentUser), IPrescriptionsDbContext
{
    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<InteractionAlert> InteractionAlerts => Set<InteractionAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PrescriptionsDbContext).Assembly);

        // ── There is deliberately NO soft-delete query filter here ──────────────
        //
        // server/src/routes/prescriptions.js never filters on deletedAt. A blanket filter would
        // change GET /api/prescriptions, the summary counts, and the prescription rows that the
        // Visits module reads back through this module's port.
    }
}
