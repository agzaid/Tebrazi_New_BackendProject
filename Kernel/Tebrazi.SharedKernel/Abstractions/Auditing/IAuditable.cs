namespace Tebrazi.SharedKernel.Abstractions.Auditing;

public interface ICreationAuditable
{
    DateTime CreatedAt { get; }
    string CreatedBy { get; }
    void ApplyCreationAudit(DateTime createdAt, string createdBy);
}

public interface IModificationAuditable
{
    DateTime? UpdatedAt { get; }
    string? UpdatedBy { get; }
    void ApplyModificationAudit(DateTime updatedAt, string? updatedBy);
}
