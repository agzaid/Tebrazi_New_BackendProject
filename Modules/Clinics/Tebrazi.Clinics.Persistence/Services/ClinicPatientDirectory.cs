using Microsoft.EntityFrameworkCore;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Enums;
using Tebrazi.SharedKernel.Exceptions;

namespace Tebrazi.Clinics.Persistence.Services;

/// <summary>
/// Clinics' implementation of the published <see cref="IClinicPatientDirectory"/> port.
///
/// It lives in Persistence, next to <c>IdentityDirectory</c>'s equivalent, because it projects
/// straight out of the context: mapping in SQL rather than materialising entities keeps a
/// hundred-row visit list to the eleven columns the summary actually needs.
/// </summary>
public sealed class ClinicPatientDirectory(
    ClinicsDbContext context,
    IClinicsDbContext unitOfWork) : IClinicPatientDirectory
{
    public Task<ClinicPatientSummary?> GetAsync(string clinicPatientId, CancellationToken ct = default)
        => context.ClinicPatients
            .AsNoTracking()
            .Where(p => p.Id == clinicPatientId)
            .Select(Projection)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<string, ClinicPatientSummary>> GetManyAsync(
        IReadOnlyCollection<string> clinicPatientIds, CancellationToken ct = default)
    {
        if (clinicPatientIds.Count == 0)
            return new Dictionary<string, ClinicPatientSummary>(0);

        // Distinct first: a day of appointments for one chart would otherwise send the same id
        // many times in the IN list.
        var ids = clinicPatientIds.Distinct().ToArray();

        var rows = await context.ClinicPatients
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(Projection)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id);
    }

    public async Task TouchLastVisitAsync(
        string clinicPatientId, DateTime visitedAtUtc, CancellationToken ct = default)
    {
        var chart = await context.ClinicPatients
            .FirstOrDefaultAsync(p => p.Id == clinicPatientId, ct);

        // Silent on a missing chart: the caller has already written the visit, and a stale
        // clinic_patient_id must not turn that into a failure.
        if (chart is null) return;

        chart.RecordVisit(visitedAtUtc);
        await unitOfWork.SaveChangesAsync(ct);
    }

    // ── Added for the Connections module ─────────────────────────────────────

    public Task<ClinicPatientRecord?> GetOwnedRecordAsync(
        string clinicPatientId, string physicianUserId, CancellationToken ct = default)
        => context.ClinicPatients
            .AsNoTracking()
            // physician_user_id and nothing else: the delete route has no staff fallback, so a
            // receptionist never matches and always sees the 404 (connections.js:866-868).
            .Where(p => p.Id == clinicPatientId && p.PhysicianUserId == physicianUserId)
            .Select(RecordProjection)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ClinicPatientRecord>> ListActiveForPhysicianAsync(
        string physicianUserId, CancellationToken ct = default)
        => await context.ClinicPatients
            .AsNoTracking()
            .Where(p => p.PhysicianUserId == physicianUserId && p.IsActive)
            // `orderBy: { name: 'asc' }` — deliberately not ClinicPatientStore's
            // active/last-visit/name ordering, which belongs to a different screen.
            .OrderBy(p => p.Name)
            .Select(RecordProjection)
            .ToListAsync(ct);

    public async Task<ClinicPatientRecord> CreateAsync(
        ClinicPatientDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // Node writes `gender || null` straight into a Prisma enum column, so an unrecognised
        // string is a PrismaClientValidationError and the route answers
        // `500 {"error":"Failed to create patient file"}`. Throwing keeps that; silently dropping
        // it to null would store a chart Node refuses to store.
        // ⚠ `Enum.IsDefined(Type, string)` FIRST, and it is not redundant. `Enum.TryParse` also
        // accepts a NUMERIC string ("1" → FEMALE) and a comma-separated list ("MALE,OTHER"), and
        // it SUCCEEDS for a number with no matching member ("7" → (Gender)7, stored and echoed
        // back verbatim because the column is HasConversion<string>()). Prisma accepts none of
        // those: the Gender enum is exactly MALE|FEMALE|OTHER (schema.prisma:1113-1117), so every
        // one of them is a PrismaClientValidationError inside the route's own try and therefore
        // `500 {"error":"Failed to create patient file"}`. IsDefined against the STRING does exact,
        // case-sensitive member-name matching and rejects all three shapes.
        Gender? gender = null;
        if (!string.IsNullOrEmpty(draft.Gender))
        {
            if (!Enum.IsDefined(typeof(Gender), draft.Gender)
                || !Enum.TryParse<Gender>(draft.Gender, ignoreCase: false, out var parsed))
            {
                throw new BusinessException(
                    "Invalid gender", $"'{draft.Gender}' is not a valid Gender value.", 500);
            }

            gender = parsed;
        }

        var chart = Domain.Entities.ClinicPatient.Create(
            draft.PhysicianUserId,
            draft.Name,
            draft.ClinicId,
            draft.Phone,
            draft.Email,
            draft.DateOfBirth,
            gender,
            // nationalId: no Connections route writes it (connections.js:786-799).
            nationalId: null,
            draft.BloodType,
            draft.Notes,
            draft.Allergies,
            draft.ChronicConditions);

        context.ClinicPatients.Add(chart);
        await unitOfWork.SaveChangesAsync(ct);

        return ToRecord(chart);
    }

    public async Task<bool> DeleteAsync(string clinicPatientId, CancellationToken ct = default)
    {
        var chart = await context.ClinicPatients
            .FirstOrDefaultAsync(p => p.Id == clinicPatientId, ct);

        if (chart is null) return false;

        // A HARD delete, matching `tx.clinicPatient.delete` (connections.js:885). The chart's
        // visits, appointments, notes and tags are removed by the caller through their own ports
        // first — this port owns clinic_patients and nothing else.
        context.ClinicPatients.Remove(chart);
        await unitOfWork.SaveChangesAsync(ct);

        return true;
    }

    /// <summary>The in-memory twin of <see cref="RecordProjection"/>, for a just-created row.</summary>
    private static ClinicPatientRecord ToRecord(Domain.Entities.ClinicPatient p) => new(
        p.Id, p.PhysicianUserId, p.ClinicId, p.Name, p.Phone, p.Email, p.DateOfBirth,
        p.Gender?.ToString(), p.NationalId, p.BloodType, p.Notes,
        p.Allergies, p.ChronicConditions,
        p.LinkedUserId, p.LinkedAt, p.IsActive, p.LastVisitDate, p.CreatedAt, p.UpdatedAt);

    /// <summary>
    /// Every scalar, for the three Connections routes whose responses spread the whole Prisma row.
    /// The audit columns this table carries and Prisma's does not (<c>created_by</c>,
    /// <c>updated_by</c>) are deliberately absent.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<
        Func<Domain.Entities.ClinicPatient, ClinicPatientRecord>> RecordProjection =
        p => new ClinicPatientRecord(
            p.Id,
            p.PhysicianUserId,
            p.ClinicId,
            p.Name,
            p.Phone,
            p.Email,
            p.DateOfBirth,
            p.Gender == null ? null : p.Gender.ToString(),
            p.NationalId,
            p.BloodType,
            p.Notes,
            p.Allergies,
            p.ChronicConditions,
            p.LinkedUserId,
            p.LinkedAt,
            p.IsActive,
            p.LastVisitDate,
            p.CreatedAt,
            p.UpdatedAt);

    /// <summary>
    /// Shared by both reads so the two can never drift. Gender is stringified here because the
    /// enum is stored and serialized as its name everywhere else.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<
        Func<Domain.Entities.ClinicPatient, ClinicPatientSummary>> Projection =
        p => new ClinicPatientSummary(
            p.Id,
            p.PhysicianUserId,
            p.ClinicId,
            p.Name,
            p.Phone,
            p.Email,
            p.DateOfBirth,
            p.Gender == null ? null : p.Gender.ToString(),
            p.BloodType,
            p.LinkedUserId,
            p.IsActive);
}
