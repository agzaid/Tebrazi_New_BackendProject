namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The narrow write port onto organization membership, published by Identity because Identity
/// owns the table. Clinics needs it because joining a clinic also makes someone a member of the
/// clinic's organization.
///
/// It is deliberately tiny. A broad "identity write" port would recreate the cross-module
/// coupling these ports exist to prevent.
///
/// Note this writes through the IDENTITY unit of work, so a Clinics handler that calls it and
/// then saves its own context has made two commits, not one. Where that matters, the clinic row
/// is written first so a failure leaves a membership without a clinic role rather than the
/// reverse.
/// </summary>
public interface IOrganizationMembershipWriter
{
    Task<bool> IsMemberAsync(string organizationId, string userId, CancellationToken ct = default);

    /// <summary>Adds a MEMBER-role membership if absent, and commits. Idempotent.</summary>
    Task EnsureMemberAsync(string organizationId, string userId, CancellationToken ct = default);
}
