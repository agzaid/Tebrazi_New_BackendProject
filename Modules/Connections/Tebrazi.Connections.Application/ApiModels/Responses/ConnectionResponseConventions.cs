namespace Tebrazi.Connections.Application.ApiModels.Responses;

/// <summary>
/// The naming rule for this namespace, stated in code so it is enforced by review rather than by
/// memory — and so the namespace itself exists before the first response DTO is written, which is
/// what lets five handler files in five parallel branches all carry the same
/// <c>using Tebrazi.Connections.Application.ApiModels.Responses;</c> and still compile.
///
/// <para><b>Every endpoint gets its OWN response record.</b> There is no shared
/// <c>ConnectionDto</c> and there must not be one: the nineteen Node routes emit at least eleven
/// distinct shapes of the same row — bare scalars, scalars plus a <c>message</c>, scalars plus an
/// embedded <c>physicianUser</c>, scalars nested under a <c>connection</c> key — and a shared DTO
/// would quietly unify them and change the wire.</para>
///
/// <para><b>Every record name starts with its group's prefix.</b> The five files compile into this
/// one namespace, so two files declaring <c>ConnectionResponse</c> is a build error that nobody
/// sees until the branches meet. The prefixes are below; see docs/connections-surface.md §8 for
/// the endpoint-to-group map.</para>
/// </summary>
public static class ConnectionResponseConventions
{
    /// <summary>
    /// The edge lifecycle: <c>GET /</c>, <c>POST /request</c>, <c>PUT /{id}/accept</c>,
    /// <c>PUT /{id}/reject</c>, <c>DELETE /{id}</c>, <c>GET /patient-summaries</c>.
    /// </summary>
    public const string LinkPrefix = "ConnLink";

    /// <summary>
    /// Dependant edges: <c>POST /assign-subprofile</c> and
    /// <c>DELETE /unassign-subprofile/{subprofileId}/{physicianUserId}</c>.
    /// </summary>
    public const string SubprofilePrefix = "ConnSubprofile";

    /// <summary>
    /// Finding, creating and inviting people: <c>GET /search</c>, <c>POST /add-by-phone</c>,
    /// <c>POST /create-patient</c>, <c>POST /invite-email</c>, <c>GET /invite-link</c>, and the two
    /// endpoints the client calls that Node does not implement
    /// (<c>GET /search-physicians</c>, <c>POST /</c>).
    /// </summary>
    public const string DiscoverPrefix = "ConnDiscover";

    /// <summary>
    /// Walk-in charts: <c>POST /clinic-patients</c>, <c>GET /clinic-patients</c>,
    /// <c>DELETE /clinic-patients/{id}</c>.
    /// </summary>
    public const string ClinicPatientPrefix = "ConnClinicPatient";

    /// <summary>
    /// Pairing: <c>POST /generate-pin</c>, <c>POST /connect-by-pin</c>, <c>GET /qr-code</c>.
    /// </summary>
    public const string PinPrefix = "ConnPin";

    // ⚠ THIS TABLE IS THE ROUTE-TO-PREFIX REGISTER AND IT IS AUTHORITATIVE.
    //
    // Two entries were corrected after the five groups were written in parallel, because the file
    // and the code disagreed and nothing in the build would have said so — a second handler for
    // the same route under a different prefix compiles clean and only fails at runtime, and the
    // controller author would have had no signal which pair to wire:
    //
    //   * invite-email / invite-link were listed under PinPrefix and are implemented under
    //     ConnDiscover (ConnectionDiscoveryUseCases.cs). Discovery now owns them; there is no
    //     ConnPinInvite* type and none is to be added.
    //   * GET /patient-summaries was listed under SubprofilePrefix and is implemented as
    //     ConnLinkPatientSummariesQuery in ConnectionLifecycleUseCases.cs. Lifecycle owns it.
    //
    // Both were resolved by moving the DOCUMENTED assignment to where the working code already is,
    // rather than renaming types for symmetry. Every route in the module appears exactly once
    // above.
}
