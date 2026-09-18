using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Visits.Application.Abstractions.Persistence;
using Tebrazi.Visits.Application.ApiModels.Responses;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Application.UseCases;

// ── Investigation CRUD, scoped to a visit (visits.js:1129-1233) ───────────────
//
// None of the three routes has a userType gate, a visit.status gate, an audit row, a
// notification or a transaction. Investigations stay writable on a COMPLETED or ARCHIVED
// visit — the client hides the UI, the server does not enforce it.

/// <summary>
/// The 404-then-403 gate the three investigation routes open with (visits.js:1139-1145,
/// 1174-1180, 1216-1222): the visit must exist, and the caller must own it through a
/// PhysicianProfile.
///
/// The 403 says "Not your visit" even when the real cause is that the caller has no physician
/// profile at all; the two are indistinguishable to the client and must stay that way.
/// </summary>
internal static class InvestigationVisitAccess
{
    public static async Task<Visit> LoadOwnedVisitAsync(
        IVisitStore visits,
        IIdentityDirectory identity,
        string visitId,
        string userId,
        CancellationToken ct)
    {
        // findUnique({ where: { id } }) — no soft-delete and no status filter.
        var visit = await visits.GetForUpdateAsync(visitId, ct)
            ?? throw new NotFoundException("Visit not found");

        var physician = await identity.GetPhysicianByUserIdAsync(userId, ct);
        if (physician is null || visit.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your visit");

        return visit;
    }
}

// ── POST /api/visits/{id}/investigations ─────────────────────────────────────

/// <summary>
/// Creates an investigation attached to a visit. The controller must answer <b>201</b> here; the
/// sibling PUT and DELETE answer 200.
/// </summary>
/// <param name="SubprofileId">
/// Optional. When omitted or empty the new row INHERITS the parent visit's subprofile
/// (visits.js:1150). A supplied id is not validated against the visit or the patient.
/// </param>
public sealed record InvestigationCreateCommand(
    string VisitId,
    string UserId,
    string? Type,
    string? Name,
    string? Instructions,
    string? SubprofileId) : IRequest<InvestigationResponse>;

public sealed class InvestigationCreateHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IInvestigationStore investigations,
    IIdentityDirectory identity)
    : IRequestHandler<InvestigationCreateCommand, InvestigationResponse>
{
    public async Task<InvestigationResponse> Handle(
        InvestigationCreateCommand request, CancellationToken cancellationToken = default)
    {
        // visits.js:1135 validates BEFORE the visit lookup, so a body with no type never reaches
        // the 404. The body is a bare { error }, not the { error: "Validation failed" } envelope.
        //
        // Node's `!type || !name` is a FALSY check, so it accepts a whitespace-only "  " and
        // persists it verbatim, where this answers 400. That gap is not closable from here:
        // Investigation.Create both ThrowIfNullOrWhiteSpace's and Trim()s its type/name, so a
        // blank value has no representation, and "  CBC  " loses its spaces (chunk-7 quirk
        // "`name` is stored untrimmed"). Rejecting it beats a 500 out of the entity guard.
        if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Name))
            throw new BusinessException("type and name are required", "type and name are required");

        var visit = await InvestigationVisitAccess.LoadOwnedVisitAsync(
            visits, identity, request.VisitId, request.UserId, cancellationToken);

        var investigation = Investigation.Create(
            visitId: request.VisitId,
            // Free text, NOT an enum: the column has no whitelist and the client vocabulary
            // (BLOOD_WORK, X_RAY, LAB, ...) is a convention only.
            type: request.Type,
            name: request.Name,
            // `subprofileId || visit.subprofileId || null` (visits.js:1150) — the trailing
            // `|| null` matters too: a visit whose own subprofile_id is an empty string yields
            // null, not "".
            subprofileId: Coalesce(request.SubprofileId) ?? Coalesce(visit.SubprofileId),
            // `instructions || null` (visits.js:1153): an empty string is stored as null.
            instructions: Coalesce(request.Instructions));

        investigations.Add(investigation);
        await dbContext.SaveChangesAsync(cancellationToken);

        return InvestigationMapper.ToResponse(investigation);
    }

    /// <summary>JS <c>value || next</c>: an empty string is falsy and falls through.</summary>
    private static string? Coalesce(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

// ── PUT /api/visits/{id}/investigations/{invId} ───────────────────────────────

/// <summary>
/// Updates an investigation's status and/or result notes. Only those two fields are writable —
/// type, name, instructions and resultUrl in the body are ignored by the Node handler.
/// </summary>
/// <param name="Status">
/// Optional, and read with a TRUTHY check (visits.js:1183): null and "" are silently ignored and
/// the row keeps its status, answering 200 either way.
/// </param>
public sealed record InvestigationUpdateCommand(
    string VisitId,
    string InvestigationId,
    string UserId,
    string? Status,
    string? ResultNotes) : IRequest<InvestigationResponse>;

public sealed class InvestigationUpdateHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IInvestigationStore investigations,
    IIdentityDirectory identity)
    : IRequestHandler<InvestigationUpdateCommand, InvestigationResponse>
{
    public async Task<InvestigationResponse> Handle(
        InvestigationUpdateCommand request, CancellationToken cancellationToken = default)
    {
        await InvestigationVisitAccess.LoadOwnedVisitAsync(
            visits, identity, request.VisitId, request.UserId, cancellationToken);

        var investigation = await investigations.GetForUpdateAsync(request.InvestigationId, cancellationToken);

        // findFirst({ id: invId, visitId: id }) (visits.js:1189) — the cross-visit IDOR guard.
        // The message differs from the visit-level "Visit not found" and the client shows it
        // verbatim, so the two 404s must not be collapsed.
        if (investigation is null || investigation.VisitId != request.VisitId)
            throw new NotFoundException("Investigation not found for this visit");

        // Validated only AFTER the existence check, because in Node the bad value is not rejected
        // until prisma.investigation.update runs, which is after the findFirst.
        var status = ParseStatus(request.Status);

        // Investigation.Update now matches visits.js:1182-1184 directly: writing COMPLETED
        // re-stamps completedAt every time, and moving AWAY from COMPLETED leaves the stale
        // timestamp in place. No compensating dance is needed here any more.
        //
        // One gap remains: an explicit `"resultNotes": null` in the body is treated as
        // "not supplied", while Node's `!== undefined` check writes the null through and clears
        // the column. Closing it needs the command to distinguish an absent key from a null one
        // (a JsonElement? or a tri-state wrapper) — Investigation.ClearResultNotes() is already
        // there for the write itself.
        investigation.Update(status: status, resultNotes: request.ResultNotes);

        // visits.js:1194-1197 calls prisma.investigation.update UNCONDITIONALLY, and Prisma's
        // @updatedAt bumps the column even when `data` is {} — so an empty body, or one that
        // merely re-sends the values already stored, still answers 200 with a FRESH updatedAt.
        // EF would detect no property change, skip the row and echo the old timestamp, so the
        // modification stamp is applied by hand here. BaseDbContext's audit pass then overwrites
        // it with the real user id, because the entity is Modified once UpdatedAt differs.
        investigation.ApplyModificationAudit(DateTime.UtcNow, null);

        await dbContext.SaveChangesAsync(cancellationToken);

        return InvestigationMapper.ToResponse(investigation);
    }

    /// <summary>
    /// Node hands the raw string to Prisma, which rejects an unknown member, and the route's
    /// catch-all answers 500 <c>{"error":"Failed to update investigation"}</c>. Returning 400
    /// here would change the text the client displays, so the bad-status case keeps the 500.
    /// </summary>
    private static InvestigationStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return null;

        // Prisma's enum check is exact: case-sensitive, and "1" is not a member name — both of
        // which a bare Enum.TryParse would wrongly accept.
        if (!Enum.TryParse<InvestigationStatus>(status, out var parsed)
            || !string.Equals(Enum.GetName(parsed), status, StringComparison.Ordinal))
        {
            throw new BusinessException(
                "Failed to update investigation", "Failed to update investigation", 500);
        }

        return parsed;
    }
}

// ── DELETE /api/visits/{id}/investigations/{invId} ────────────────────────────

/// <summary>
/// Hard-deletes an investigation scoped to its visit. Answers <c>{ success: true }</c> whether or
/// not anything was removed.
/// </summary>
public sealed record InvestigationDeleteCommand(
    string VisitId,
    string InvestigationId,
    string UserId) : IRequest<SuccessResponse>;

public sealed class InvestigationDeleteHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IInvestigationStore investigations,
    IIdentityDirectory identity)
    : IRequestHandler<InvestigationDeleteCommand, SuccessResponse>
{
    public async Task<SuccessResponse> Handle(
        InvestigationDeleteCommand request, CancellationToken cancellationToken = default)
    {
        await InvestigationVisitAccess.LoadOwnedVisitAsync(
            visits, identity, request.VisitId, request.UserId, cancellationToken);

        var investigation = await investigations.GetForUpdateAsync(request.InvestigationId, cancellationToken);

        // deleteMany({ id: invId, visitId: id }) (visits.js:1224) discards its count, so an
        // unknown id, an already-deleted id and an id belonging to another visit all answer the
        // same 200 { success: true }. The client retries deletes idempotently — do NOT 404 here,
        // and note this is where DELETE parts company with its sibling PUT.
        if (investigation is not null && investigation.VisitId == request.VisitId)
        {
            // Hard delete: investigations have no soft-delete column, unlike the visit routes in
            // the same file, which archive instead.
            investigations.Remove(investigation);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new SuccessResponse(true);
    }
}
