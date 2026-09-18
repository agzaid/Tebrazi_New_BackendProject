using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Application.ApiModels.Responses;

/// <summary>
/// One lab or imaging order, exactly as Prisma serialized an <c>investigations</c> row for
/// <c>POST /api/visits/{id}/investigations</c> and <c>PUT .../{invId}</c>: twelve scalars, no
/// relations, in schema declaration order (server/prisma/schema.prisma L882-L903).
///
/// There is deliberately no <c>createdAt</c> key. The Prisma model carries <c>requestedAt</c>
/// instead, and the entity's inherited <c>CreatedAt</c> is an internal audit stamp that the Node
/// API never exposed — adding it would put a thirteenth key on the wire.
/// </summary>
public sealed record InvestigationResponse(
    string Id,
    string VisitId,
    string? SubprofileId,
    string Type,
    string Name,
    string? Instructions,
    InvestigationStatus Status,
    string? ResultUrl,
    string? ResultNotes,
    DateTime RequestedAt,
    DateTime? CompletedAt,
    DateTime UpdatedAt);

public static class InvestigationMapper
{
    /// <summary>
    /// <c>UpdatedAt ?? CreatedAt</c> because Prisma's <c>@updatedAt</c> is non-nullable and is
    /// set on insert, while <see cref="Tebrazi.SharedKernel.Base.MutableEntity{TKey}.UpdatedAt"/>
    /// stays null until the row is first modified. Without the fallback a freshly created
    /// investigation would report <c>updatedAt: null</c> where Node reports a timestamp.
    /// </summary>
    public static InvestigationResponse ToResponse(Investigation investigation)
        => new(investigation.Id,
               investigation.VisitId,
               investigation.SubprofileId,
               investigation.Type,
               investigation.Name,
               investigation.Instructions,
               investigation.Status,
               investigation.ResultUrl,
               investigation.ResultNotes,
               investigation.RequestedAt,
               investigation.CompletedAt,
               investigation.UpdatedAt ?? investigation.CreatedAt);
}
