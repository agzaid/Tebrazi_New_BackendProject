using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Patients.Domain.Entities;

/// <summary>
/// Shared shape of the three health-record types. Each row belongs to EITHER the account holder
/// (<see cref="PatientProfileId"/>) or one dependant (<see cref="SubprofileId"/>) — exactly one
/// is set, and both columns are nullable in the Node schema.
///
/// All three are SOFT deleted via <see cref="DeletedAt"/>: a removed allergy still has to be
/// visible on a visit recorded before it was removed.
/// </summary>
public abstract class HealthRecord : MutableEntity<string>
{
    public string? PatientProfileId { get; protected set; }
    public string? SubprofileId { get; protected set; }
    public DateTime? DeletedAt { get; protected set; }

    public bool IsDeleted => DeletedAt.HasValue;

    /// <summary>Marks the row deleted. Idempotent — a second call keeps the original instant.</summary>
    public void SoftDelete(DateTime utcNow) => DeletedAt ??= utcNow;

    /// <summary>True when this row belongs to the given owner.</summary>
    public bool BelongsTo(string patientProfileId, string? subprofileId)
        => subprofileId is null
            ? PatientProfileId == patientProfileId
            : SubprofileId == subprofileId;

    protected void AssignOwner(string patientProfileId, string? subprofileId)
    {
        // A row owned by a dependant carries only the subprofile id, mirroring how the Node
        // routes write it: setting both would make "whose record is this" ambiguous.
        if (subprofileId is null) PatientProfileId = patientProfileId;
        else SubprofileId = subprofileId;
    }
}

/// <summary>Port of the Prisma <c>Allergy</c> model (table <c>allergies</c>).</summary>
public sealed class Allergy : HealthRecord
{
    private Allergy() { }

    public string Allergen { get; private set; } = null!;
    public string? Severity { get; private set; }
    public string? Reaction { get; private set; }

    public static Allergy Create(
        string patientProfileId, string? subprofileId,
        string allergen, string? severity, string? reaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allergen);

        var record = new Allergy
        {
            Id = Guid.NewGuid().ToString(),
            Allergen = allergen.Trim(),
            Severity = severity,
            Reaction = reaction
        };

        record.AssignOwner(patientProfileId, subprofileId);
        return record;
    }
}

/// <summary>Port of the Prisma <c>ChronicCondition</c> model (table <c>chronic_conditions</c>).</summary>
public sealed class ChronicCondition : HealthRecord
{
    private ChronicCondition() { }

    public string Condition { get; private set; } = null!;
    public DateTime? DiagnosedDate { get; private set; }
    public string? Notes { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static ChronicCondition Create(
        string patientProfileId, string? subprofileId,
        string condition, DateTime? diagnosedDate, string? notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);

        var record = new ChronicCondition
        {
            Id = Guid.NewGuid().ToString(),
            Condition = condition.Trim(),
            DiagnosedDate = diagnosedDate,
            Notes = notes
        };

        record.AssignOwner(patientProfileId, subprofileId);
        return record;
    }

    /// <summary>Resolved rather than deleted — the history stays on the record.</summary>
    public void Resolve() => IsActive = false;
}

/// <summary>
/// Port of the Prisma <c>CurrentMedication</c> model (table <c>current_medications</c>) —
/// what the patient reports taking, which is not the same as what has been prescribed. The
/// reconciliation endpoint compares the two.
/// </summary>
public sealed class CurrentMedication : HealthRecord
{
    private CurrentMedication() { }

    public string DrugName { get; private set; } = null!;
    public string? Dosage { get; private set; }
    public string? Frequency { get; private set; }
    public string? PrescribedBy { get; private set; }
    public DateTime? StartDate { get; private set; }
    public DateTime? EndDate { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static CurrentMedication Create(
        string patientProfileId, string? subprofileId,
        string drugName, string? dosage, string? frequency, string? prescribedBy,
        DateTime? startDate, DateTime? endDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drugName);

        var record = new CurrentMedication
        {
            Id = Guid.NewGuid().ToString(),
            DrugName = drugName.Trim(),
            Dosage = dosage,
            Frequency = frequency,
            PrescribedBy = prescribedBy,
            StartDate = startDate,
            EndDate = endDate
        };

        record.AssignOwner(patientProfileId, subprofileId);
        return record;
    }

    public void Stop() => IsActive = false;
}
