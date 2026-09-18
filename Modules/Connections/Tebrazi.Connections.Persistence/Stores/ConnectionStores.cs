using Microsoft.EntityFrameworkCore;
using Tebrazi.Connections.Application.Abstractions.Persistence;
using Tebrazi.Connections.Domain.Entities;

namespace Tebrazi.Connections.Persistence.Stores;

/// <summary>
/// Reads and writes over <c>doctor_patient_connections</c>. No method filters a soft-delete
/// column because the entity has none — see the comment on <see cref="ConnectionsDbContext"/>.
/// </summary>
public sealed class DoctorPatientConnectionStore(ConnectionsDbContext context)
    : IDoctorPatientConnectionStore
{
    public Task<DoctorPatientConnection?> GetForUpdateAsync(string id, CancellationToken ct = default)
        => context.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<DoctorPatientConnection>> ListAsync(
        ConnectionFilter filter, CancellationToken ct = default)
        => await Apply(context.Connections.AsNoTracking(), filter)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

    public Task<DoctorPatientConnection?> FindPairAsync(
        string physicianUserId,
        string patientUserId,
        string? subprofileId,
        ConnectionStatus? status = null,
        bool tracked = false,
        CancellationToken ct = default)
    {
        var query = tracked ? context.Connections : context.Connections.AsNoTracking();

        query = query.Where(c =>
            c.PhysicianUserId == physicianUserId
            && c.PatientUserId == patientUserId);

        // A VALUE comparison, not "any subprofile": `subprofileId: null` in Prisma means IS NULL.
        // Written as two branches because `c.SubprofileId == subprofileId` with a null parameter
        // translates to `= NULL`, which is never true in SQL.
        query = subprofileId is null
            ? query.Where(c => c.SubprofileId == null)
            : query.Where(c => c.SubprofileId == subprofileId);

        if (status.HasValue) query = query.Where(c => c.Status == status.Value);

        return query.FirstOrDefaultAsync(ct);
    }

    public Task<DoctorPatientConnection?> FindAnyPairAsync(
        string physicianUserId, string patientUserId, CancellationToken ct = default)
        => context.Connections
            .AsNoTracking()
            .Where(c => c.PhysicianUserId == physicianUserId && c.PatientUserId == patientUserId)
            // Node's findFirst carries no orderBy and takes Postgres' physical order. Ordering by
            // created_at makes the choice deterministic where the row actually matters — the
            // caller reads Status out of it and reports "already connected".
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<DoctorPatientConnection?> FindForUpdateByEitherSideAsync(
        string callerUserId, string otherUserId, CancellationToken ct = default)
        => context.Connections
            .Where(c =>
                (c.PhysicianUserId == callerUserId && c.PatientUserId == otherUserId)
                || (c.PatientUserId == callerUserId && c.PhysicianUserId == otherUserId))
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ConnectionEdge>> ListEdgesWithAsync(
        string lookupUserId,
        IReadOnlyCollection<string> counterpartUserIds,
        CancellationToken ct = default)
    {
        if (counterpartUserIds.Count == 0) return [];

        var ids = counterpartUserIds.Distinct().ToArray();

        return await context.Connections
            .AsNoTracking()
            .Where(c =>
                (c.PhysicianUserId == lookupUserId && ids.Contains(c.PatientUserId))
                || (c.PatientUserId == lookupUserId && ids.Contains(c.PhysicianUserId)))
            .Select(c => new ConnectionEdge(c.PhysicianUserId, c.PatientUserId, c.Status))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        ListAcceptedSubprofileIdsAsync(
            string physicianUserId,
            IReadOnlyCollection<string> patientUserIds,
            CancellationToken ct = default)
    {
        if (patientUserIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<string>>(0);

        var ids = patientUserIds.Distinct().ToArray();

        var rows = await context.Connections
            .AsNoTracking()
            .Where(c =>
                c.PhysicianUserId == physicianUserId
                && ids.Contains(c.PatientUserId)
                && c.SubprofileId != null
                && c.Status == ConnectionStatus.ACCEPTED)
            .Select(c => new { c.PatientUserId, c.SubprofileId })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.PatientUserId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<string> (g) => g.Select(r => r.SubprofileId!).Distinct().ToList());
    }

    public void Add(DoctorPatientConnection connection) => context.Connections.Add(connection);

    public void Remove(DoctorPatientConnection connection) => context.Connections.Remove(connection);

    private static IQueryable<DoctorPatientConnection> Apply(
        IQueryable<DoctorPatientConnection> query, ConnectionFilter f)
    {
        if (f.PhysicianUserId is not null)
            query = query.Where(c => c.PhysicianUserId == f.PhysicianUserId);

        if (f.PatientUserId is not null)
            query = query.Where(c => c.PatientUserId == f.PatientUserId);

        if (f.Status.HasValue)
            query = query.Where(c => c.Status == f.Status.Value);

        // Only the `true` case adds a predicate: no Node query on this path asks for
        // "subprofile is NOT null", so `false` is treated the same as absent.
        if (f.SubprofileIdIsNull == true)
            query = query.Where(c => c.SubprofileId == null);

        // An EMPTY collection is a real filter meaning "no user matched the search" and must
        // return nothing. Treating it as unfiltered would list the physician's whole patient book.
        if (f.PatientUserIdIn is { } patientIds)
            query = query.Where(c => patientIds.Contains(c.PatientUserId));

        return query;
    }
}

/// <summary>
/// Reads and writes over <c>connection_pins</c>. "Redeemable" is
/// <c>used_at IS NULL AND expires_at &gt; now</c> throughout, with a STRICT comparison — which is
/// what makes <see cref="ConnectionPin.ForceExpire"/> take effect immediately.
/// </summary>
public sealed class ConnectionPinStore(ConnectionsDbContext context) : IConnectionPinStore
{
    public async Task<IReadOnlyList<ConnectionPin>> ListRedeemableForUpdateAsync(
        string physicianUserId, DateTime now, CancellationToken ct = default)
        => await context.ConnectionPins
            .Where(p => p.PhysicianUserId == physicianUserId
                        && p.UsedAt == null
                        && p.ExpiresAt > now)
            .ToListAsync(ct);

    public Task<bool> AnyRedeemableAsync(string pin, DateTime now, CancellationToken ct = default)
        => context.ConnectionPins
            .AsNoTracking()
            // Global, not per physician: two doctors must never hold the same live code, because
            // connect-by-pin resolves a bare four-digit string to exactly one physician.
            .AnyAsync(p => p.Pin == pin && p.UsedAt == null && p.ExpiresAt > now, ct);

    public Task<ConnectionPin?> FindRedeemableForUpdateAsync(
        string pin, DateTime now, CancellationToken ct = default)
        => context.ConnectionPins
            .Where(p => p.Pin == pin && p.UsedAt == null && p.ExpiresAt > now)
            // Newest first, so a collision the generation loop somehow let through resolves to the
            // pin the physician just read out rather than to a stale one.
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public void Add(ConnectionPin pin) => context.ConnectionPins.Add(pin);
}
