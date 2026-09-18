namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// Provisions and retires the organization that backs a clinic. In Tebrazi a clinic IS a tenant:
/// creating one creates an organization, an OWNER membership and a FREE subscription together.
///
/// Published by Identity because Identity owns all three tables. Clinics calls this instead of
/// writing them, which is what keeps the modules separable.
/// </summary>
public interface IOrganizationProvisioner
{
    /// <summary>
    /// Creates an organization with a unique slug derived from <paramref name="name"/>, makes
    /// <paramref name="ownerUserId"/> its OWNER, and opens a FREE, ACTIVE subscription.
    /// Commits before returning.
    /// </summary>
    /// <returns>The new organization's id.</returns>
    Task<string> ProvisionAsync(string name, string ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Deletes an organization and everything cascading from it. Absent is not an error — the
    /// clinic delete path calls this after removing the clinic and must not fail if a cascade
    /// already took it.
    /// </summary>
    Task DeleteAsync(string organizationId, CancellationToken ct = default);
}
