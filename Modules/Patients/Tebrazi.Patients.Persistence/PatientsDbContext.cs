using Microsoft.EntityFrameworkCore;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Patients.Persistence;

/// <summary>
/// The Patients module's context: <c>patient_profiles</c>, <c>family_subprofiles</c>,
/// <c>allergies</c>, <c>chronic_conditions</c>, <c>current_medications</c>,
/// <c>external_visits</c> and <c>external_doctors</c>.
/// </summary>
public sealed class PatientsDbContext(DbContextOptions<PatientsDbContext> options, ICurrentUser currentUser)
    : BaseDbContext(options, currentUser), IPatientsDbContext
{
    public DbSet<PatientProfile> PatientProfiles => Set<PatientProfile>();
    public DbSet<FamilySubprofile> FamilySubprofiles => Set<FamilySubprofile>();
    public DbSet<Allergy> Allergies => Set<Allergy>();
    public DbSet<ChronicCondition> ChronicConditions => Set<ChronicCondition>();
    public DbSet<CurrentMedication> CurrentMedications => Set<CurrentMedication>();
    public DbSet<ExternalVisit> ExternalVisits => Set<ExternalVisit>();
    public DbSet<ExternalDoctor> ExternalDoctors => Set<ExternalDoctor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PatientsDbContext).Assembly);

        // Soft-deleted health records are filtered out of EVERY query by default. Doing it here
        // rather than in each store means a new query cannot forget it and surface deleted rows.
        // Use IgnoreQueryFilters() where deleted rows are genuinely wanted.
        modelBuilder.Entity<Allergy>().HasQueryFilter(a => a.DeletedAt == null);
        modelBuilder.Entity<ChronicCondition>().HasQueryFilter(c => c.DeletedAt == null);
        modelBuilder.Entity<CurrentMedication>().HasQueryFilter(m => m.DeletedAt == null);
    }
}
