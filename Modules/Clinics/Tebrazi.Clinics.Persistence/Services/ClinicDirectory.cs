using Microsoft.EntityFrameworkCore;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Abstractions.Directory;

namespace Tebrazi.Clinics.Persistence.Services;

/// <summary>
/// Clinics' implementation of <see cref="IClinicDirectory"/> and
/// <see cref="IClinicWorkingHoursWriter"/>.
/// </summary>
public sealed class ClinicDirectory(ClinicsDbContext context, IClinicsDbContext unitOfWork)
    : IClinicDirectory, IClinicWorkingHoursWriter
{
    public Task<ClinicSummary?> GetClinicAsync(string clinicId, CancellationToken ct = default)
        => context.Clinics
            .AsNoTracking()
            .Where(c => c.Id == clinicId)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, ClinicSummary>> GetClinicsAsync(
        IReadOnlyCollection<string> clinicIds, CancellationToken ct = default)
    {
        if (clinicIds.Count == 0) return new Dictionary<string, ClinicSummary>(0);

        var ids = clinicIds.Distinct().ToArray();

        var rows = await context.Clinics
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(Projection)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id);
    }

    /// <summary>Ownership, not membership — see the port's note on IClinicAccessEvaluator.</summary>
    public Task<bool> IsOwningPhysicianAsync(
        string clinicId, string physicianProfileId, CancellationToken ct = default)
        => context.Clinics
            .AsNoTracking()
            .AnyAsync(c => c.Id == clinicId && c.PhysicianId == physicianProfileId, ct);

    public Task<bool> IsActiveStaffAsync(string userId, string clinicId, CancellationToken ct = default)
        => context.ClinicStaff
            .AsNoTracking()
            .AnyAsync(s => s.ClinicId == clinicId && s.UserId == userId && s.IsActive, ct);

    public async Task<IReadOnlyList<string>> ListActiveStaffUserIdsAsync(
        string clinicId, CancellationToken ct = default)
        => await context.ClinicStaff
            .AsNoTracking()
            .Where(s => s.ClinicId == clinicId && s.IsActive)
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync(ct);

    /// <summary>
    /// Tracked read, then commit. Returns false rather than throwing when the clinic is gone, so
    /// the caller can answer its own 404 instead of turning a missing row into a 500.
    /// </summary>
    public async Task<bool> SetWorkingHoursAsync(
        string clinicId, string? workingHoursJson, CancellationToken ct = default)
    {
        var clinic = await context.Clinics.FirstOrDefaultAsync(c => c.Id == clinicId, ct);
        if (clinic is null) return false;

        clinic.SetWorkingHours(workingHoursJson);
        await unitOfWork.SaveChangesAsync(ct);

        return true;
    }

    // ── Added for the Connections module ─────────────────────────────────────

    public Task<string?> GetFirstClinicIdForPhysicianAsync(
        string physicianProfileId, CancellationToken ct = default)
        => context.Clinics
            .AsNoTracking()
            // No isActive filter: the Node relation load has none (connections.js:762).
            .Where(c => c.PhysicianId == physicianProfileId)
            // Deterministic where Node takes Postgres' physical order. See the port's doc comment.
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => (string?)c.Id)
            .FirstOrDefaultAsync(ct);

    private static readonly System.Linq.Expressions.Expression<
        Func<Domain.Entities.Clinic, ClinicSummary>> Projection =
        c => new ClinicSummary(
            c.Id,
            c.OrganizationId,
            c.PhysicianId,
            c.Name,
            c.Address,
            c.City,
            c.Country,
            c.Phone,
            c.Email,
            c.Specialty,
            c.Logo,
            c.WorkingHours,
            c.IsActive,
            c.AllowPatientBooking,
            c.ConsultationFee,
            c.FollowUpFee);
}
