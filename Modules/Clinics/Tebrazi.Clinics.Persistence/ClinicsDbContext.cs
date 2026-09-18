using Microsoft.EntityFrameworkCore;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Domain.Entities;
using Tebrazi.Infrastructure.Shared.Persistence;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Clinics.Persistence;

/// <summary>
/// The Clinics module's context. It maps only clinic-owned tables: <c>clinics</c>,
/// <c>clinic_staff</c>, <c>staff_pins</c>, <c>staff_invitations</c>.
///
/// It deliberately does NOT map <c>users</c>, <c>physician_profiles</c> or
/// <c>organization_members</c>, even though clinic rows carry foreign keys to all three. Mapping
/// another module's table here would put one entity in two EF models — the exact leak that made
/// KACCC's module boundaries fictional. Those are reached through IIdentityDirectory.
/// </summary>
public sealed class ClinicsDbContext(DbContextOptions<ClinicsDbContext> options, ICurrentUser currentUser)
    : BaseDbContext(options, currentUser), IClinicsDbContext
{
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<ClinicStaff> ClinicStaff => Set<ClinicStaff>();
    public DbSet<StaffPin> StaffPins => Set<StaffPin>();
    public DbSet<StaffInvitation> StaffInvitations => Set<StaffInvitation>();
    public DbSet<ClinicPatient> ClinicPatients => Set<ClinicPatient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClinicsDbContext).Assembly);
    }
}
