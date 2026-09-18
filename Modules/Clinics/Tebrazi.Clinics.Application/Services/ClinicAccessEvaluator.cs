using System.Text.Json;
using Tebrazi.Clinics.Application.Abstractions.Persistence;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Authorization;

namespace Tebrazi.Clinics.Application.Services;

/// <summary>
/// Port of <c>requireClinicAccess</c> from <c>server/src/middleware/clinicPermission.js</c>.
/// The precedence below is the Node middleware's, in the same order:
///
/// <list type="number">
///   <item>Owner of the named clinic → full access, permission check skipped.</item>
///   <item>Holds a physician profile at all → full access, clinic defaulted to their first.</item>
///   <item>Active ClinicStaff row → permissions from the role preset plus stored overrides.</item>
///   <item>Otherwise denied.</item>
/// </list>
///
/// Step 2 is broader than it looks: ANY physician gets physician-level access even to a clinic
/// they do not own. That is the Node behaviour, kept deliberately so the two backends authorize
/// identically — it is flagged in the README as a candidate to tighten, in both at once.
/// </summary>
public sealed class ClinicAccessEvaluator(
    IClinicReadStore clinics,
    IClinicStaffStore staff,
    IIdentityDirectory identity) : IClinicAccessEvaluator
{
    public async Task<ClinicAccess> EvaluateAsync(
        string userId,
        string? clinicId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId)) return ClinicAccess.Denied;

        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

        // 1 — owner of the clinic named on the request.
        if (!string.IsNullOrEmpty(clinicId) && physician is not null)
        {
            var clinic = await clinics.GetByIdAsync(clinicId, cancellationToken);
            if (clinic is not null && clinic.PhysicianId == physician.Id)
                return ClinicAccess.Physician(clinicId);
        }

        // 2 — a physician with no clinic named, or named someone else's.
        if (physician is not null)
        {
            if (!string.IsNullOrEmpty(clinicId))
                return ClinicAccess.Physician(clinicId);

            var owned = await clinics.ListByPhysicianOldestFirstAsync(physician.Id, cancellationToken);
            return ClinicAccess.Physician(owned.Count > 0 ? owned[0].Id : null);
        }

        // 3 — staff. With no clinic named, fall back to their first active membership.
        var membership = string.IsNullOrEmpty(clinicId)
            ? await staff.FirstActiveForUserAsync(userId, cancellationToken)
            : await staff.GetAsync(clinicId, userId, cancellationToken);

        if (membership is null || !membership.IsActive)
            return ClinicAccess.Denied;

        var permissions = ClinicPermissionMatrix.Resolve(
            membership.Role,
            ParseOverrides(membership.Permissions));

        return ClinicAccess.Staff(membership.ClinicId, membership.Role, permissions);
    }

    /// <summary>
    /// Reads the stored overrides document. Non-boolean values are skipped rather than coerced:
    /// a malformed row must not silently GRANT a permission.
    /// </summary>
    internal static IReadOnlyDictionary<string, bool>? ParseOverrides(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var overrides = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    overrides[property.Name] = property.Value.GetBoolean();
            }

            return overrides.Count > 0 ? overrides : null;
        }
        catch (JsonException)
        {
            // Unparseable overrides fall back to the role preset — never to "allow".
            return null;
        }
    }
}
