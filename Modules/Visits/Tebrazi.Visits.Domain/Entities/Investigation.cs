using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Visits.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Investigation</c> model (table <c>investigations</c>) — a lab or imaging
/// order placed during a visit, and its result once one comes back.
///
/// It lives in the Visits module rather than in one of its own because it is only ever reached
/// through <c>/api/visits/{visitId}/investigations</c>: there is no route that reads an
/// investigation without its visit.
///
/// <c>RequestedAt</c> is the Prisma field, and it is what the client reads — it is NOT the same
/// as the inherited <c>CreatedAt</c> audit stamp, which the API never returns.
/// </summary>
public sealed class Investigation : MutableEntity<string>
{
    private Investigation() { }

    public string VisitId { get; private set; } = null!;
    public string? SubprofileId { get; private set; }

    /// <summary>Free text in the Node schema: "LAB", "IMAGING", "PATHOLOGY", ... not an enum.</summary>
    public string Type { get; private set; } = null!;

    public string Name { get; private set; } = null!;
    public string? Instructions { get; private set; }

    public InvestigationStatus Status { get; private set; } = InvestigationStatus.REQUESTED;

    public string? ResultUrl { get; private set; }
    public string? ResultNotes { get; private set; }

    public DateTime RequestedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    public static Investigation Create(
        string visitId,
        string type,
        string name,
        string? subprofileId = null,
        string? instructions = null,
        DateTime? requestedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Investigation
        {
            Id = Guid.NewGuid().ToString(),
            VisitId = visitId,
            SubprofileId = subprofileId,
            Type = type.Trim(),
            Name = name.Trim(),
            Instructions = instructions,
            Status = InvestigationStatus.REQUESTED,
            RequestedAt = requestedAt ?? DateTime.UtcNow
        };
    }

    /// <summary>
    /// Partial update: null leaves a field alone.
    ///
    /// The <c>CompletedAt</c> handling copies visits.js:1182-1184 exactly, and it is asymmetric
    /// in two ways worth stating:
    /// <list type="bullet">
    /// <item>Moving to COMPLETED stamps the time UNCONDITIONALLY, so re-completing an order
    /// moves the timestamp forward. It is not a stamp-once field.</item>
    /// <item>Moving AWAY from COMPLETED does NOT clear it. A re-opened order keeps the date it
    /// was previously completed on, which looks like a bug and is the contract.</item>
    /// </list>
    ///
    /// To clear <see cref="ResultNotes"/>, call <see cref="ClearResultNotes"/> — passing null
    /// here means "not supplied", whereas the Node route writes an explicit null through.
    /// </summary>
    public void Update(
        string? type = null,
        string? name = null,
        string? instructions = null,
        InvestigationStatus? status = null,
        string? resultUrl = null,
        string? resultNotes = null)
    {
        if (!string.IsNullOrWhiteSpace(type)) Type = type.Trim();
        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (instructions is not null) Instructions = instructions;
        if (resultUrl is not null) ResultUrl = resultUrl;
        if (resultNotes is not null) ResultNotes = resultNotes;

        if (status.HasValue)
        {
            Status = status.Value;

            if (status.Value == InvestigationStatus.COMPLETED)
                CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Writes a null <see cref="ResultNotes"/>. The Node route applies
    /// <c>if (resultNotes !== undefined)</c>, so a body carrying an explicit
    /// <c>"resultNotes": null</c> CLEARS the stored value — a case <see cref="Update"/>'s
    /// null-means-unchanged convention cannot express.
    /// </summary>
    public void ClearResultNotes() => ResultNotes = null;
}

public enum InvestigationStatus { REQUESTED, IN_PROGRESS, COMPLETED, CANCELLED }
