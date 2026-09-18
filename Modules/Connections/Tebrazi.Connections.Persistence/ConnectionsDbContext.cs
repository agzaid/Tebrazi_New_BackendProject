using Microsoft.EntityFrameworkCore;
using Tebrazi.Connections.Application.Abstractions.Persistence;
using Tebrazi.Connections.Domain.Entities;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Connections.Persistence;

/// <summary>
/// The Connections module's context: <c>doctor_patient_connections</c> and
/// <c>connection_pins</c>.
///
/// <para>It deliberately does NOT map <c>users</c>, <c>physician_profiles</c>,
/// <c>family_subprofiles</c>, <c>clinics</c> or <c>clinic_patients</c>, even though every row here
/// carries foreign keys into the first three and three endpoints operate on the last. Those tables
/// belong to Identity, Patients and Clinics, and mapping one here would put a single entity in two
/// EF models. They are reached through <c>IIdentityDirectory</c>, <c>IPatientDirectory</c>,
/// <c>IClinicDirectory</c> and <c>IClinicPatientDirectory</c>.</para>
/// </summary>
public sealed class ConnectionsDbContext(
    DbContextOptions<ConnectionsDbContext> options,
    ICurrentUser currentUser)
    : BaseDbContext(options, currentUser), IConnectionsDbContext
{
    public DbSet<DoctorPatientConnection> Connections => Set<DoctorPatientConnection>();
    public DbSet<ConnectionPin> ConnectionPins => Set<ConnectionPin>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ConnectionsDbContext).Assembly);

        // ── There is deliberately NO soft-delete query filter here, and in this module the
        //    reason is stronger than in Visits, Appointments and Prescriptions ────────────────
        //
        // Those three each HAVE a `deletedAt` column that their routes happen not to filter on.
        // These two entities do not have one at all. Measured against server/prisma/schema.prisma:
        // `model DoctorPatientConnection` (507-530) and `model ConnectionPin` (549-563) declare no
        // such field, and the string "deletedAt" does not occur anywhere in the 1,818 lines of
        // server/src/routes/connections.js.
        //
        // Disconnection is a HARD delete in both routes that perform one —
        // `prisma.doctorPatientConnection.delete` at connections.js:385 (unassign-subprofile) and
        // :427 (DELETE /{id}) — so there is no soft-deleted state for a filter to hide. A filter
        // here would have nothing to filter and would be a claim about the data model that the
        // data model does not make.
        //
        // The one place this module touches a soft-deletable row is through IPatientDirectory,
        // whose PatientsDbContext DOES carry query filters on Allergy, ChronicCondition and
        // CurrentMedication. GET /patient-summaries' conditionsCount and medsCount come back
        // through that port and inherit those filters, which is correct: Node hard-deletes those
        // rows, so a live Node row never has deletedAt set. See docs/PORT-STATUS.md.
    }
}
