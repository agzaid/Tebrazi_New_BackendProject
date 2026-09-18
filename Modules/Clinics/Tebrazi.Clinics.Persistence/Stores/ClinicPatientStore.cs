using Microsoft.EntityFrameworkCore;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Domain.Entities;

namespace Tebrazi.Clinics.Persistence.Stores;

public sealed class ClinicPatientStore(ClinicsDbContext context) : IClinicPatientStore
{
    public Task<ClinicPatient?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.ClinicPatients.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<ClinicPatient?> GetAsync(string id, CancellationToken ct = default)
        => context.ClinicPatients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<ClinicPatient>> GetManyAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];

        return await context.ClinicPatients
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ClinicPatient>> ListForPhysicianAsync(
        string physicianUserId, string? clinicId, CancellationToken ct = default)
    {
        var query = context.ClinicPatients
            .AsNoTracking()
            .Where(p => p.PhysicianUserId == physicianUserId);

        if (clinicId is not null)
            query = query.Where(p => p.ClinicId == clinicId);

        // Most recently seen first, with charts that have never had a visit last rather than
        // first — a null last_visit_date sorts before everything under a plain descending sort.
        return await query
            .OrderByDescending(p => p.IsActive)
            .ThenByDescending(p => p.LastVisitDate != null)
            .ThenByDescending(p => p.LastVisitDate)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ClinicPatient>> ListByLinkedUserAsync(
        string linkedUserId, CancellationToken ct = default)
        => await context.ClinicPatients
            .AsNoTracking()
            .Where(p => p.LinkedUserId == linkedUserId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

    public void Add(ClinicPatient patient) => context.ClinicPatients.Add(patient);

    public void Remove(ClinicPatient patient) => context.ClinicPatients.Remove(patient);
}
