using Microsoft.EntityFrameworkCore;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.Clinics.Domain.Entities;

namespace Tebrazi.Clinics.Persistence.Stores;

public sealed class ClinicReadStore(ClinicsDbContext context) : IClinicReadStore
{
    private IQueryable<Clinic> Query => context.Clinics.AsNoTracking();

    public Task<Clinic?> GetByIdAsync(string id, CancellationToken ct = default)
        => Query.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<Clinic>> ListByPhysicianAsync(
        string physicianProfileId, CancellationToken ct = default)
        => await Query
            .Where(c => c.PhysicianId == physicianProfileId && c.IsActive)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Clinic>> ListByPhysicianOldestFirstAsync(
        string physicianProfileId, CancellationToken ct = default)
        => await Query
            .Where(c => c.PhysicianId == physicianProfileId && c.IsActive)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

    public async Task<(IReadOnlyList<Clinic> Items, int Total)> SearchDirectoryAsync(
        string? search, string? specialty, string? city, int page, int pageSize, CancellationToken ct = default)
    {
        var query = Query.Where(c => c.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Contains translates to LIKE '%term%', which cannot seek. Acceptable at directory
            // sizes; if the directory grows, this is the query to move to full-text search.
            var term = search.Trim();
            query = query.Where(c =>
                c.Name.Contains(term) ||
                (c.Specialty != null && c.Specialty.Contains(term)) ||
                (c.City != null && c.City.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(specialty))
            query = query.Where(c => c.Specialty == specialty);

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(c => c.City == city);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountStaffAsync(
        IReadOnlyCollection<string> clinicIds, CancellationToken ct = default)
    {
        if (clinicIds.Count == 0) return new Dictionary<string, int>();

        // One grouped query for every clinic, rather than a count per clinic.
        var counts = await context.ClinicStaff
            .AsNoTracking()
            .Where(s => clinicIds.Contains(s.ClinicId) && s.IsActive)
            .GroupBy(s => s.ClinicId)
            .Select(g => new { ClinicId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(c => c.ClinicId, c => c.Count, StringComparer.Ordinal);
    }
}

public sealed class ClinicWriteStore(ClinicsDbContext context) : IClinicWriteStore
{
    public Task<Clinic?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Clinics.FirstOrDefaultAsync(c => c.Id == id, ct);

    public void Add(Clinic clinic) => context.Clinics.Add(clinic);

    public void Remove(Clinic clinic) => context.Clinics.Remove(clinic);
}

public sealed class ClinicStaffStore(ClinicsDbContext context) : IClinicStaffStore
{
    // Tracked: every caller either mutates the row or reads it inside a write flow.
    public Task<ClinicStaff?> GetAsync(string clinicId, string userId, CancellationToken ct = default)
        => context.ClinicStaff.FirstOrDefaultAsync(s => s.ClinicId == clinicId && s.UserId == userId, ct);

    public Task<ClinicStaff?> GetByIdAsync(string staffId, CancellationToken ct = default)
        => context.ClinicStaff.FirstOrDefaultAsync(s => s.Id == staffId, ct);

    public async Task<IReadOnlyList<ClinicStaff>> ListActiveForUserAsync(
        string userId, CancellationToken ct = default)
        => await context.ClinicStaff
            .AsNoTracking()
            .Include(s => s.Clinic)
            .Where(s => s.UserId == userId && s.IsActive)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

    public Task<ClinicStaff?> FirstActiveForUserAsync(string userId, CancellationToken ct = default)
        => context.ClinicStaff
            .AsNoTracking()
            .Include(s => s.Clinic)
            .Where(s => s.UserId == userId && s.IsActive)
            .OrderBy(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ClinicStaff>> ListForClinicAsync(
        string clinicId, CancellationToken ct = default)
        => await context.ClinicStaff
            .AsNoTracking()
            .Where(s => s.ClinicId == clinicId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

    public void Add(ClinicStaff staff) => context.ClinicStaff.Add(staff);

    public void Remove(ClinicStaff staff) => context.ClinicStaff.Remove(staff);
}

public sealed class StaffPinStore(ClinicsDbContext context) : IStaffPinStore
{
    public Task<StaffPin?> FindRedeemableAsync(string pin, DateTime utcNow, CancellationToken ct = default)
        => context.StaffPins
            .Include(p => p.Clinic)
            .FirstOrDefaultAsync(p => p.Pin == pin && p.UsedAt == null && p.ExpiresAt > utcNow, ct);

    public Task<bool> IsPinInUseAsync(string pin, DateTime utcNow, CancellationToken ct = default)
        => context.StaffPins
            .AsNoTracking()
            .AnyAsync(p => p.Pin == pin && p.UsedAt == null && p.ExpiresAt > utcNow, ct);

    public async Task<IReadOnlyList<StaffPin>> ListLiveForClinicAsync(
        string clinicId, DateTime utcNow, CancellationToken ct = default)
        => await context.StaffPins
            .Where(p => p.ClinicId == clinicId && p.UsedAt == null && p.ExpiresAt > utcNow)
            .ToListAsync(ct);

    public void Add(StaffPin pin) => context.StaffPins.Add(pin);
}

public sealed class StaffInvitationStore(ClinicsDbContext context) : IStaffInvitationStore
{
    public Task<StaffInvitation?> GetByTokenAsync(string token, CancellationToken ct = default)
        => context.StaffInvitations.FirstOrDefaultAsync(i => i.Token == token, ct);

    public Task<StaffInvitation?> GetByIdAsync(string id, CancellationToken ct = default)
        => context.StaffInvitations.FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<StaffInvitation>> ListForClinicAsync(
        string clinicId, CancellationToken ct = default)
        => await context.StaffInvitations
            .AsNoTracking()
            .Where(i => i.ClinicId == clinicId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

    public Task<StaffInvitation?> FindPendingAsync(
        string clinicId, string email, CancellationToken ct = default)
        => context.StaffInvitations
            .FirstOrDefaultAsync(i =>
                i.ClinicId == clinicId &&
                i.Email == email &&
                i.Status == StaffInvitationStatus.PENDING, ct);

    public void Add(StaffInvitation invitation) => context.StaffInvitations.Add(invitation);
}
