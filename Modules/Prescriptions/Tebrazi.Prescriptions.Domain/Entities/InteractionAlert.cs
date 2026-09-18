using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Prescriptions.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>InteractionAlert</c> model (table <c>interaction_alerts</c>) — the
/// audit trail of drug-interaction checks.
///
/// It lives in Prescriptions because it has no relations at all and is only ever touched from
/// <c>prescriptions.js</c>. Nothing reads it back through a cross-module port.
///
/// Both owner columns are nullable and exactly one is set per row: a check run by a physician
/// records <see cref="PhysicianId"/> and leaves <see cref="PatientUserId"/> null, and a check
/// run by a patient does the reverse (prescriptions.js:843).
/// </summary>
public sealed class InteractionAlert : ImmutableEntity<string>
{
    private InteractionAlert() { }

    /// <summary>The physician PROFILE id, or null when a patient ran the check.</summary>
    public string? PhysicianId { get; private set; }

    /// <summary>The patient's USER id, or null when a physician ran the check.</summary>
    public string? PatientUserId { get; private set; }

    public string? VisitId { get; private set; }

    /// <summary>Opaque JSON array of the drug names checked. Required.</summary>
    public string Drugs { get; private set; } = null!;

    /// <summary>
    /// Opaque JSON array of interaction results, straight from the gateway. Defaults to
    /// <c>"[]"</c> rather than null, matching the Prisma default.
    /// </summary>
    public string Interactions { get; private set; } = "[]";

    public int AlertCount { get; private set; }

    /// <summary>
    /// The Prisma field, distinct from the inherited <c>CreatedAt</c> audit stamp — which the
    /// API never returns.
    /// </summary>
    public DateTime CheckedAt { get; private set; }

    public static InteractionAlert Create(
        string drugs,
        string? interactions = null,
        int alertCount = 0,
        string? physicianId = null,
        string? patientUserId = null,
        string? visitId = null,
        DateTime? checkedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drugs);

        return new InteractionAlert
        {
            Id = Guid.NewGuid().ToString(),
            PhysicianId = physicianId,
            PatientUserId = patientUserId,
            VisitId = visitId,
            Drugs = drugs,
            Interactions = string.IsNullOrWhiteSpace(interactions) ? "[]" : interactions,
            AlertCount = alertCount,
            CheckedAt = checkedAt ?? DateTime.UtcNow
        };
    }
}
