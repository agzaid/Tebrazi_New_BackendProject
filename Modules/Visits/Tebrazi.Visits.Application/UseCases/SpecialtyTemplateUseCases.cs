using System.Text.Json.Serialization;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Visits.Application.Services;

namespace Tebrazi.Visits.Application.UseCases;

// ── Specialty exam templates (visits.js:32-71) ───────────────────────────────
//
// Both routes sit behind router.use(authCheck) and nothing else: no userType gate, no ownership
// check, so a PATIENT may call either one and read all 11 clinical templates. Neither writes
// anything, raises a notification or opens a transaction, and the by-key route touches no
// database at all — it answers even with the database down.
//
// The response records live in this file rather than ApiModels/Responses because the whole group
// is two endpoints over one static config; SpecialtyTemplates (Services/) owns the payload types.

// ── GET /api/visits/specialty-template ───────────────────────────────────────

/// <summary>
/// The template matched to the caller's own <c>PhysicianProfile.specialty</c>, plus the picker's
/// summary list of all 11 templates.
/// </summary>
/// <param name="UserId">
/// The caller. The physician profile is resolved from it, and a caller who has none is answered,
/// not rejected.
/// </param>
public sealed record SpecialtyTemplateForCallerQuery(string UserId)
    : IRequest<SpecialtyTemplatePickerResponse>;

/// <summary>
/// The 200 body in one of TWO shapes. The key sets genuinely differ — visits.js:35 returns early
/// with no <c>specialty</c> key at all — so each shape is its own record rather than one record
/// with a conditionally omitted property.
///
/// The derived types are declared on the base so serialization stays polymorphic even where the
/// declared type is this base; without them such a write would emit <c>{}</c>. Neither declares a
/// type discriminator, so no <c>$type</c> key reaches the client.
/// </summary>
[JsonDerivedType(typeof(SpecialtyTemplateWithProfileResponse))]
[JsonDerivedType(typeof(SpecialtyTemplateNoProfileResponse))]
public abstract record SpecialtyTemplatePickerResponse;

/// <summary>
/// The three-key body a caller who owns a PhysicianProfile gets:
/// <c>template</c>, <c>specialty</c>, <c>allTemplates</c>, in that order.
/// </summary>
/// <param name="Template">
/// The full matched template, or JSON null when the specialty matches no alias. 22 of the 35
/// seeded specialties (Urology, Nephrology, General Surgery...) never match, and that is the
/// design: the physician then picks from <paramref name="AllTemplates"/> by hand.
/// </param>
/// <param name="Specialty">
/// The raw, un-normalised column value. It is a non-null String in the schema, so this key is
/// always present and always a string — possibly the empty one.
/// </param>
/// <param name="AllTemplates">All 11 summaries, whatever the physician's own specialty is.</param>
public sealed record SpecialtyTemplateWithProfileResponse(
    SpecialtyTemplateDefinition? Template,
    string Specialty,
    IReadOnlyList<SpecialtyTemplateSummary> AllTemplates) : SpecialtyTemplatePickerResponse;

/// <summary>
/// The two-key body for a caller with no PhysicianProfile — every patient. <c>specialty</c> is
/// ABSENT rather than null, and <c>allTemplates</c> is EMPTY even though the 11 templates exist
/// and cost nothing to serve. The client's picker silently falls back to its own hard-coded copy
/// of the list (VisitTemplatePicker.jsx), which is why the emptiness has gone unnoticed.
/// </summary>
public sealed record SpecialtyTemplateNoProfileResponse(
    SpecialtyTemplateDefinition? Template,
    IReadOnlyList<SpecialtyTemplateSummary> AllTemplates) : SpecialtyTemplatePickerResponse;

public sealed class SpecialtyTemplateForCallerHandler(IIdentityDirectory identity)
    : IRequestHandler<SpecialtyTemplateForCallerQuery, SpecialtyTemplatePickerResponse>
{
    public async Task<SpecialtyTemplatePickerResponse> Handle(
        SpecialtyTemplateForCallerQuery request, CancellationToken cancellationToken = default)
    {
        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, cancellationToken);

        // No physician profile is a 200 with the degenerate body, never a 403.
        if (physician is null)
            return new SpecialtyTemplateNoProfileResponse(null, []);

        return new SpecialtyTemplateWithProfileResponse(
            SpecialtyTemplates.GetTemplateForSpecialty(physician.Specialty),
            physician.Specialty,
            SpecialtyTemplates.Summaries);
    }
}

// ── GET /api/visits/specialty-template/{key} ─────────────────────────────────

/// <summary>One template by its literal key, for the picker and for restoring a saved one.</summary>
/// <param name="Key">One of the 11 template keys, exactly as declared.</param>
public sealed record SpecialtyTemplateByKeyQuery(string Key)
    : IRequest<SpecialtyTemplateByKeyResponse>;

/// <summary>
/// The single-key envelope <c>{ "template": { ... } }</c>. It carries neither <c>specialty</c>
/// nor <c>allTemplates</c>, unlike its sibling route.
/// </summary>
public sealed record SpecialtyTemplateByKeyResponse(SpecialtyTemplateDefinition Template);

public sealed class SpecialtyTemplateByKeyHandler
    : IRequestHandler<SpecialtyTemplateByKeyQuery, SpecialtyTemplateByKeyResponse>
{
    public Task<SpecialtyTemplateByKeyResponse> Handle(
        SpecialtyTemplateByKeyQuery request, CancellationToken cancellationToken = default)
    {
        // The raw path segment, neither lowercased nor alias-resolved: "Ophthalmology" is a 404,
        // and so is "eye" even though it is a valid specialty alias for the sibling route.
        //
        // One deliberate divergence: Node indexes a plain object literal, so Object.prototype
        // member names slip past its falsy check — GET .../__proto__ answers 200 {"template":{}}
        // and .../constructor answers 200 {}. A dictionary lookup 404s them, which is the only
        // shape the client's bare .catch was ever going to handle anyway.
        var template = SpecialtyTemplates.TryGetTemplate(request.Key);

        // Task.FromException, NOT `throw`. This Handle needs no await, and Mediator dispatches
        // through MethodInfo.Invoke (Kernel/Tebrazi.Infrastructure.Shared/Mediation/Mediator.cs),
        // which wraps a SYNCHRONOUS throw in TargetInvocationException. ExceptionHandlingMiddleware
        // matches on AppException, so a bare throw from a non-async handler would fall through to
        // the generic catch and answer 500 {"error":"Internal Server Error"} instead of Node's
        // 404 {"error":"Template not found"} (visits.js:65). An async handler is safe because the
        // throw lands in the returned task; this one is not, so it must fault the task by hand.
        return template is null
            ? Task.FromException<SpecialtyTemplateByKeyResponse>(new NotFoundException("Template not found"))
            : Task.FromResult(new SpecialtyTemplateByKeyResponse(template));
    }
}
