using Microsoft.EntityFrameworkCore;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.Visits.Application.Abstractions.Persistence;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Persistence;

/// <summary>
/// The Visits module's context: <c>visits</c> and <c>investigations</c>.
/// </summary>
public sealed class VisitsDbContext(DbContextOptions<VisitsDbContext> options, ICurrentUser currentUser)
    : BaseDbContext(options, currentUser), IVisitsDbContext
{
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Investigation> Investigations => Set<Investigation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VisitsDbContext).Assembly);

        // ── There is deliberately NO soft-delete query filter here ──────────────
        //
        // The Patients module puts `HasQueryFilter(x => x.DeletedAt == null)` on its context,
        // and copying that here would be wrong. Across all 1,442 lines of
        // server/src/routes/visits.js, `softDeleteFilter` is applied in exactly ONE place —
        // the GET /api/visits list (visits.js:89). Every other route reads by id with a bare
        // `findUnique({ where: { id } })`, which returns a soft-deleted visit.
        //
        // A blanket filter would therefore make GET /api/visits/{id} answer 404 for a visit the
        // Node backend still serves, and would silently change the relation counts that
        // Appointments and Prescriptions read through this module's ports.
        //
        // So the filter is applied per query, in VisitStore.PageAsync alone. See
        // docs/PORT-STATUS.md.
        //
        // Note also that DELETE /api/visits/{id} does NOT write DeletedAt at all: it sets
        // Status = ARCHIVED, and the patient keeps access to the archived record. DeletedAt
        // exists on the table but nothing in visits.js ever writes it.
    }
}
