using System.Collections.Frozen;

namespace Tebrazi.SharedKernel.Authorization;

/// <summary>
/// Direct port of <c>server/src/services/clinicPermissions.js</c>. The presets below are
/// value-for-value identical to that file's <c>ROLE_PERMISSIONS</c>; changing one without
/// changing the other silently splits the two backends' authorization behaviour.
/// Physicians are NOT in this table — they bypass it entirely and hold every permission.
/// </summary>
public static class ClinicPermissionMatrix
{
    private static readonly FrozenDictionary<string, FrozenDictionary<string, bool>> Presets =
        new Dictionary<string, FrozenDictionary<string, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            [ClinicStaffRole.Assistant] = Preset(
                viewPatientFiles: true,  manageAppointments: true,  checkInPatients: true,
                draftNotes: true,        uploadDocuments: true,     sendMessages: false,
                viewAnalytics: true,     manageFollowUps: true,     startVisits: false,
                prescribe: false,        writeSoap: false,          manageSettings: false,
                manageStaff: false,      orderInvestigations: false),

            [ClinicStaffRole.Receptionist] = Preset(
                viewPatientFiles: false, manageAppointments: true,  checkInPatients: true,
                draftNotes: false,       uploadDocuments: false,    sendMessages: false,
                viewAnalytics: false,    manageFollowUps: false,    startVisits: false,
                prescribe: false,        writeSoap: false,          manageSettings: false,
                manageStaff: false,      orderInvestigations: false),

            [ClinicStaffRole.Nurse] = Preset(
                viewPatientFiles: true,  manageAppointments: true,  checkInPatients: true,
                draftNotes: true,        uploadDocuments: true,     sendMessages: false,
                viewAnalytics: false,    manageFollowUps: true,     startVisits: false,
                prescribe: false,        writeSoap: false,          manageSettings: false,
                manageStaff: false,      orderInvestigations: false),

            [ClinicStaffRole.Admin] = Preset(
                viewPatientFiles: true,  manageAppointments: true,  checkInPatients: true,
                draftNotes: true,        uploadDocuments: true,     sendMessages: false,
                viewAnalytics: true,     manageFollowUps: true,     startVisits: false,
                prescribe: false,        writeSoap: false,          manageSettings: true,
                manageStaff: true,       orderInvestigations: false),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the effective permissions for a role, with any stored per-staff overrides
    /// merged over the preset. An unknown role falls back to RECEPTIONIST — the least
    /// privileged preset — exactly as the Node service does.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> Resolve(
        string? role,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        var preset = role is not null && Presets.TryGetValue(role, out var found)
            ? found
            : Presets[ClinicStaffRole.Receptionist];

        if (overrides is null || overrides.Count == 0)
            return preset;

        var merged = new Dictionary<string, bool>(preset, StringComparer.Ordinal);
        foreach (var (key, value) in overrides)
            merged[key] = value;

        return merged;
    }

    /// <summary>True when the role, after overrides, grants <paramref name="permission"/>.</summary>
    public static bool Grants(
        string? role,
        string permission,
        IReadOnlyDictionary<string, bool>? overrides = null)
        => Resolve(role, overrides).TryGetValue(permission, out var granted) && granted;

    /// <summary>Every preset, unmerged. Backs the staff-permission-matrix endpoint.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> AllPresets()
        => Presets.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, bool>)kv.Value,
            StringComparer.Ordinal);

    private static FrozenDictionary<string, bool> Preset(
        bool viewPatientFiles, bool manageAppointments, bool checkInPatients,
        bool draftNotes, bool uploadDocuments, bool sendMessages,
        bool viewAnalytics, bool manageFollowUps, bool startVisits,
        bool prescribe, bool writeSoap, bool manageSettings,
        bool manageStaff, bool orderInvestigations)
        => new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [ClinicPermission.ViewPatientFiles]    = viewPatientFiles,
            [ClinicPermission.ManageAppointments]  = manageAppointments,
            [ClinicPermission.CheckInPatients]     = checkInPatients,
            [ClinicPermission.DraftNotes]          = draftNotes,
            [ClinicPermission.UploadDocuments]     = uploadDocuments,
            [ClinicPermission.SendMessages]        = sendMessages,
            [ClinicPermission.ViewAnalytics]       = viewAnalytics,
            [ClinicPermission.ManageFollowUps]     = manageFollowUps,
            [ClinicPermission.StartVisits]         = startVisits,
            [ClinicPermission.Prescribe]           = prescribe,
            [ClinicPermission.WriteSoap]           = writeSoap,
            [ClinicPermission.ManageSettings]      = manageSettings,
            [ClinicPermission.ManageStaff]         = manageStaff,
            [ClinicPermission.OrderInvestigations] = orderInvestigations,
        }.ToFrozenDictionary(StringComparer.Ordinal);
}
