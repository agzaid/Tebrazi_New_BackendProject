using Tebrazi.Clinics.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Persistence;

namespace Tebrazi.Clinics.Application.Abstractions.Persistence;

/// <summary>The Clinics module's unit of work. Handlers inject this, never the concrete context.</summary>
public interface IClinicsDbContext : IDbContext;

public interface IClinicReadStore
{
    Task<Clinic?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Active clinics owned by a physician profile, newest first.</summary>
    Task<IReadOnlyList<Clinic>> ListByPhysicianAsync(string physicianProfileId, CancellationToken ct = default);

    /// <summary>Active clinics owned by a physician profile, oldest first — the switcher's order.</summary>
    Task<IReadOnlyList<Clinic>> ListByPhysicianOldestFirstAsync(string physicianProfileId, CancellationToken ct = default);

    /// <summary>The public directory: active clinics, optionally filtered, paged in SQL.</summary>
    Task<(IReadOnlyList<Clinic> Items, int Total)> SearchDirectoryAsync(
        string? search, string? specialty, string? city, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Staff and visit counts for a set of clinics, resolved in one query each.</summary>
    Task<IReadOnlyDictionary<string, int>> CountStaffAsync(
        IReadOnlyCollection<string> clinicIds, CancellationToken ct = default);
}

public interface IClinicWriteStore
{
    Task<Clinic?> GetForUpdateAsync(string id, CancellationToken ct = default);
    void Add(Clinic clinic);
    void Remove(Clinic clinic);
}

public interface IClinicStaffStore
{
    Task<ClinicStaff?> GetAsync(string clinicId, string userId, CancellationToken ct = default);
    Task<ClinicStaff?> GetByIdAsync(string staffId, CancellationToken ct = default);

    /// <summary>Active staff rows for a user across every clinic.</summary>
    Task<IReadOnlyList<ClinicStaff>> ListActiveForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>The user's first active staff row, used by the context switcher.</summary>
    Task<ClinicStaff?> FirstActiveForUserAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<ClinicStaff>> ListForClinicAsync(string clinicId, CancellationToken ct = default);

    void Add(ClinicStaff staff);
    void Remove(ClinicStaff staff);
}

public interface IStaffPinStore
{
    /// <summary>A live, unredeemed PIN. PINs are only unique among live ones, never globally.</summary>
    Task<StaffPin?> FindRedeemableAsync(string pin, DateTime utcNow, CancellationToken ct = default);

    Task<bool> IsPinInUseAsync(string pin, DateTime utcNow, CancellationToken ct = default);

    /// <summary>Every live PIN for a clinic, so issuing a new one can expire them.</summary>
    Task<IReadOnlyList<StaffPin>> ListLiveForClinicAsync(string clinicId, DateTime utcNow, CancellationToken ct = default);

    void Add(StaffPin pin);
}

public interface IStaffInvitationStore
{
    Task<StaffInvitation?> GetByTokenAsync(string token, CancellationToken ct = default);
    Task<StaffInvitation?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<StaffInvitation>> ListForClinicAsync(string clinicId, CancellationToken ct = default);
    Task<StaffInvitation?> FindPendingAsync(string clinicId, string email, CancellationToken ct = default);
    void Add(StaffInvitation invitation);
}

/// <summary>
/// The physician-owned patient charts. Other modules never reach this store — they use the
/// published <c>IClinicPatientDirectory</c> port, which this store backs.
/// </summary>
public interface IClinicPatientStore
{
    Task<ClinicPatient?> GetForUpdateAsync(string id, CancellationToken ct = default);
    Task<ClinicPatient?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Resolved in one query, for rendering a list of visits or appointments.</summary>
    Task<IReadOnlyList<ClinicPatient>> GetManyAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>A physician's charts, active first by most recent visit.</summary>
    Task<IReadOnlyList<ClinicPatient>> ListForPhysicianAsync(
        string physicianUserId, string? clinicId, CancellationToken ct = default);

    /// <summary>Charts already matched to a Tebrazi account, for reconciling the two identities.</summary>
    Task<IReadOnlyList<ClinicPatient>> ListByLinkedUserAsync(
        string linkedUserId, CancellationToken ct = default);

    void Add(ClinicPatient patient);
    void Remove(ClinicPatient patient);
}
