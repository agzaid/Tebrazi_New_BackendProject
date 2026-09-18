namespace Tebrazi.SharedKernel.Authorization;

/// <summary>
/// The published port for "may this user do X at this clinic?". The API layer depends on THIS,
/// never on a module's DbContext — the boundary that keeps authorization out of the modules'
/// internals. The Clinics module supplies the implementation.
/// </summary>
public interface IClinicAccessEvaluator
{
    /// <summary>
    /// Resolves the caller's standing at a clinic. Mirrors the precedence in
    /// <c>server/src/middleware/clinicPermission.js</c>:
    /// owning physician first, then any physician profile, then a ClinicStaff row.
    /// </summary>
    Task<ClinicAccess> EvaluateAsync(
        string userId,
        string? clinicId,
        CancellationToken cancellationToken = default);
}

/// <summary>The caller's standing at one clinic.</summary>
/// <param name="IsGranted">False when the user has no relationship with the clinic at all.</param>
/// <param name="IsPhysician">Physicians bypass the permission matrix and hold everything.</param>
/// <param name="ClinicId">The clinic actually resolved, which may differ from the one requested.</param>
/// <param name="Role">The ClinicStaff role, or null for a physician.</param>
/// <param name="Permissions">Effective permissions after overrides. Empty for a physician.</param>
public readonly record struct ClinicAccess(
    bool IsGranted,
    bool IsPhysician,
    string? ClinicId,
    string? Role,
    IReadOnlyDictionary<string, bool> Permissions)
{
    public static ClinicAccess Denied => new(false, false, null, null, EmptyPermissions);

    public static ClinicAccess Physician(string? clinicId) => new(true, true, clinicId, null, EmptyPermissions);

    public static ClinicAccess Staff(string clinicId, string role, IReadOnlyDictionary<string, bool> permissions)
        => new(true, false, clinicId, role, permissions);

    /// <summary>True when the caller holds <paramref name="permission"/>. Physicians always do.</summary>
    public bool Grants(string permission)
        => IsGranted && (IsPhysician || (Permissions.TryGetValue(permission, out var v) && v));

    private static readonly IReadOnlyDictionary<string, bool> EmptyPermissions =
        new Dictionary<string, bool>(0);
}
