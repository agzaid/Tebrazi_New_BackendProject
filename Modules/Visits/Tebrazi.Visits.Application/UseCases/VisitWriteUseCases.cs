using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Visits.Application.Abstractions.Persistence;
using Tebrazi.Visits.Application.ApiModels.Responses;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Application.UseCases;

// ─────────────────────────────────────────────────────────────────────────────
// The visit WRITE endpoints, ported from server/src/routes/visits.js.
//
// The update body models "key ABSENT" and "key present with value null" as different states,
// because visits.js destructures req.body and tests `!== undefined`. That distinction is
// load-bearing on PUT /{id}: an empty body on a COMPLETED visit is a 400, while
// {"followUpDate": null} on the same visit is a 200 that clears the column. A plain nullable DTO
// cannot tell those apart, which is why every field there is a JsonElement? — System.Text.Json
// leaves an absent key null and gives a present null ValueKind.Null.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The bare <c>{ "error": "..." }</c> 400 bodies these routes return. <c>ValidationException</c>
/// would emit <c>{"error":"Validation failed","message":...}</c> instead, which the client does not
/// match on; passing the same literal as both error and message makes the middleware drop the
/// redundant <c>message</c> key.
/// </summary>
internal static class VisitWriteErrors
{
    public static BusinessException BadRequest(string error) => new(error, error, 400);
}

// ── POST /api/visits ─────────────────────────────────────────────────────────

/// <param name="ClinicId">Required, and checked before everything else including the patient.</param>
/// <param name="AppointmentId">
/// Read but never persisted — <c>visits</c> has no <c>appointment_id</c> column. It exists only so a
/// visit started from a booking can inherit that booking's <c>subprofileId</c>.
/// </param>
public sealed record VisitWriteCreateVisitBody(
    string? ClinicId,
    string? PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string? ChiefComplaint,
    string? RawNotes,
    string? AppointmentId);

public sealed record VisitWriteCreateVisitCommand(
    string CallerUserId,
    VisitWriteCreateVisitBody? Body) : IRequest<VisitWriteCreatedResponse>;

/// <summary>
/// Port of <c>POST /api/visits</c> (visits.js:182-262). Creates an IN_PROGRESS visit for either an
/// app-user patient or a physician-owned local chart, and answers 201 with the row plus three
/// narrow relation objects.
/// </summary>
public sealed class VisitWriteCreateVisitHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IClinicPatientDirectory clinicPatients,
    IPatientDirectory patients,
    IAppointmentDirectory appointments)
    : IRequestHandler<VisitWriteCreateVisitCommand, VisitWriteCreatedResponse>
{
    public async Task<VisitWriteCreatedResponse> Handle(
        VisitWriteCreateVisitCommand request, CancellationToken cancellationToken = default)
    {
        var body = request.Body;

        // Node tests falsiness throughout, so "" counts as missing everywhere below.
        var clinicId = body?.ClinicId;
        var patientUserId = body?.PatientUserId;
        var clinicPatientId = body?.ClinicPatientId;

        if (string.IsNullOrEmpty(clinicId))
            throw VisitWriteErrors.BadRequest("clinicId is required");

        if (string.IsNullOrEmpty(patientUserId) && string.IsNullOrEmpty(clinicPatientId))
            throw VisitWriteErrors.BadRequest("patientUserId or clinicPatientId is required");

        // 400, not 403 — the only gate in this file that answers a missing physician profile with a
        // bad-request status.
        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken)
            ?? throw VisitWriteErrors.BadRequest("Physician profile required");

        // findFirst({ id: clinicId, physicianId }) — "no such clinic" and "someone else's clinic"
        // share one body.
        var clinic = await clinics.GetClinicAsync(clinicId, cancellationToken);
        if (clinic is null || clinic.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your clinic");

        // Loaded whenever a chart id was sent, because the Prisma include renders the chart in the
        // 201 even on the branch that does not need it to resolve the patient.
        ClinicPatientSummary? chart = null;
        if (!string.IsNullOrEmpty(clinicPatientId))
            chart = await clinicPatients.GetAsync(clinicPatientId, cancellationToken);

        var resolvedPatientUserId = patientUserId;
        if (!string.IsNullOrEmpty(clinicPatientId) && string.IsNullOrEmpty(patientUserId))
        {
            // visits.js:214-215 — `cp?.linkedUserId || userId`. An unlinked (or missing) chart stores
            // THE PHYSICIAN'S OWN user id as the visit's patientUserId. That is what later lets the
            // physician satisfy the patient-only gates on this same visit, and it is deliberate
            // here: no 404, no error.
            resolvedPatientUserId = chart?.LinkedUserId is { Length: > 0 } linkedUserId
                ? linkedUserId
                : request.CallerUserId;
        }

        var resolvedSubprofileId = body?.SubprofileId is { Length: > 0 } subprofileId ? subprofileId : null;

        var appointmentId = body?.AppointmentId;
        if (resolvedSubprofileId is null && !string.IsNullOrEmpty(appointmentId))
        {
            try
            {
                // No ownership check on the appointment — visits.js:222-229 looks it up by id alone,
                // so any tenant's booking can donate its subprofile.
                var appointment = await appointments.GetAppointmentAsync(appointmentId, cancellationToken);
                if (appointment?.SubprofileId is { Length: > 0 } inheritedSubprofileId)
                    resolvedSubprofileId = inheritedSubprofileId;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // visits.js:223-229 wraps the lookup in a bare `catch { /* continue without */ }`, so
                // a failed appointment read must still answer 201 with no subprofile, not 500.
            }
        }

        var visit = Visit.Create(
            organizationId: clinic.OrganizationId,
            clinicId: clinicId,
            physicianId: physician.Id,
            patientUserId: resolvedPatientUserId!,
            subprofileId: resolvedSubprofileId,
            clinicPatientId: string.IsNullOrEmpty(clinicPatientId) ? null : clinicPatientId,
            chiefComplaint: body?.ChiefComplaint is { Length: > 0 } chiefComplaint ? chiefComplaint : null,
            rawNotes: body?.RawNotes is { Length: > 0 } rawNotes ? rawNotes : null,
            visitDate: DateTime.UtcNow);

        visits.Add(visit);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(clinicPatientId))
        {
            try
            {
                // A second commit, through the Clinics unit of work — so it runs after ours and
                // outside any transaction of ours.
                await clinicPatients.TouchLastVisitAsync(
                    clinicPatientId, visit.VisitDate, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // visits.js:249-252 wraps this update in `.catch(() => {})`: a chart that cannot be
                // touched must not turn the 201 into a 500.
            }
        }

        var subprofile = resolvedSubprofileId is null
            ? null
            : await patients.GetSubprofileAsync(resolvedSubprofileId, cancellationToken);

        return VisitWriteMapper.ToCreatedResponse(visit, clinic, subprofile, chart);
    }
}

// ── PUT /api/visits/{id} ─────────────────────────────────────────────────────

/// <summary>
/// The update body. Each member is <c>JsonElement?</c> so the handler can tell an absent key from a
/// null one: absent leaves the member null, present-with-null yields
/// <see cref="JsonValueKind.Null"/>. That is the <c>!== undefined</c> test visits.js runs, and the
/// completed-visit guard is decided on presence alone.
/// </summary>
public sealed record VisitWriteUpdateVisitBody(
    JsonElement? RawNotes,
    JsonElement? RawTranscript,
    JsonElement? ChiefComplaint,
    JsonElement? Diagnosis,
    JsonElement? DiagnosisCodes,
    JsonElement? SpecialtyData,
    JsonElement? Subjective,
    JsonElement? Objective,
    JsonElement? Assessment,
    JsonElement? Plan,
    JsonElement? FollowUpDate,
    JsonElement? FollowUpNotes);

public sealed record VisitWriteUpdateVisitCommand(
    string VisitId,
    string CallerUserId,
    VisitWriteUpdateVisitBody? Body) : IRequest<VisitWriteVisitResponse>;

/// <summary>
/// Port of <c>PUT /api/visits/{id}</c> (visits.js:391-476). Two branches: a COMPLETED visit accepts
/// follow-up edits only, anything else is a field-by-field patch. Both answer with the raw visit row.
/// </summary>
public sealed class VisitWriteUpdateVisitHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IIdentityDirectory identity,
    INotificationPublisher notifications)
    : IRequestHandler<VisitWriteUpdateVisitCommand, VisitWriteVisitResponse>
{
    public async Task<VisitWriteVisitResponse> Handle(
        VisitWriteUpdateVisitCommand request, CancellationToken cancellationToken = default)
    {
        // 404 before 403 here — the opposite of DELETE /{id}, and observable.
        var visit = await visits.GetForUpdateAsync(request.VisitId, cancellationToken)
            ?? throw new NotFoundException("Visit not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken);
        if (physician is null || visit.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your visit");

        var body = request.Body;
        var followUpDate = body?.FollowUpDate;
        var followUpNotes = body?.FollowUpNotes;

        if (visit.Status == VisitStatus.COMPLETED)
        {
            var isFollowUpOnly =
                (followUpDate is not null || followUpNotes is not null)
                && body?.RawNotes is null && body?.RawTranscript is null
                && body?.ChiefComplaint is null && body?.Diagnosis is null
                && body?.DiagnosisCodes is null && body?.Subjective is null
                && body?.Objective is null && body?.Assessment is null && body?.Plan is null;

            if (!isFollowUpOnly)
                throw VisitWriteErrors.BadRequest("Cannot edit a completed visit");

            // specialtyData is MISSING from the guard's exclusion list at visits.js:413-417, so a
            // body of {followUpNotes, specialtyData} gets past it — but this branch builds its update
            // from the two follow-up fields only (visits.js:420-422), so specialtyData is silently
            // discarded and the 200 echoes the OLD value. Not applying it is the faithful behaviour,
            // not an oversight.
            var previousFollowUpDate = visit.FollowUpDate;

            visit.SetFollowUp(
                followUpDate is not null ? ReadUtcDate(followUpDate) : visit.FollowUpDate,
                followUpNotes is not null ? ReadString(followUpNotes) : visit.FollowUpNotes);

            // prisma.visit.update runs unconditionally (visits.js:423-426) and @updatedAt bumps even
            // when the submitted values equal the stored ones. EF would see no property change, skip
            // the row and echo the OLD updatedAt, so the stamp is applied by hand — the same idiom
            // InvestigationUseCases uses. BaseDbContext's audit pass then overwrites the null
            // updatedBy with the real user id, because the entity is Modified once UpdatedAt differs.
            visit.ApplyModificationAudit(DateTime.UtcNow, null);

            await dbContext.SaveChangesAsync(cancellationToken);

            await NotifyFollowUpSetAsync(
                visit, physician, previousFollowUpDate, followUpDate, followUpNotes, cancellationToken);

            return VisitWriteMapper.ToVisitResponse(visit);
        }

        // Only status COMPLETED is guarded, so ARCHIVED and CANCELLED visits stay fully editable.
        //
        // Node's rule is `if (key !== undefined) updateData[key] = value` (visits.js:451-463), so a
        // PRESENT null CLEARS the column. Visit.Update cannot express that — null there means "not
        // supplied" — so every field with an unconditional setter is routed through that setter
        // instead. chiefComplaint, rawNotes, specialtyData and diagnosis have none, and therefore
        // still ignore an explicit null; closing that needs a new entity member.
        visit.Update(
            chiefComplaint: ReadString(body?.ChiefComplaint),
            rawNotes: ReadString(body?.RawNotes),
            diagnosis: ReadStringArray(body?.Diagnosis),
            specialtyData: ReadRawJson(body?.SpecialtyData));

        if (body is { RawTranscript: not null })
            visit.SetTranscript(ReadString(body.RawTranscript));

        // The nullable-Json path: a present null must land as SQL NULL and echo back as `null`.
        if (body is { DiagnosisCodes: not null })
            visit.SetDiagnosisCodes(ReadRawJson(body.DiagnosisCodes));

        if (body is not null
            && (body.Subjective is not null || body.Objective is not null
                || body.Assessment is not null || body.Plan is not null))
        {
            // ApplySoap writes all four unconditionally, so the keys the body omitted are re-supplied
            // with what is already stored and only the present ones move.
            visit.ApplySoap(
                body.Subjective is not null ? ReadString(body.Subjective) : visit.Subjective,
                body.Objective is not null ? ReadString(body.Objective) : visit.Objective,
                body.Assessment is not null ? ReadString(body.Assessment) : visit.Assessment,
                body.Plan is not null ? ReadString(body.Plan) : visit.Plan);
        }

        // Routed through SetFollowUp rather than Update because the client clears the follow-up by
        // sending {"followUpDate": null}, and Update's null-means-unchanged rule cannot express that.
        if (followUpDate is not null || followUpNotes is not null)
        {
            visit.SetFollowUp(
                followUpDate is not null ? ReadUtcDate(followUpDate) : visit.FollowUpDate,
                followUpNotes is not null ? ReadString(followUpNotes) : visit.FollowUpNotes);
        }

        // visits.js:465-468 issues the update even for an empty body, and @updatedAt bumps. See the
        // note on the same call in the COMPLETED branch above.
        visit.ApplyModificationAudit(DateTime.UtcNow, null);

        await dbContext.SaveChangesAsync(cancellationToken);

        return VisitWriteMapper.ToVisitResponse(visit);
    }

    /// <summary>
    /// visits.js:429-447. Fires only when the client sent a truthy follow-up date that differs from
    /// the stored one — clearing the date notifies nobody.
    /// </summary>
    private async Task NotifyFollowUpSetAsync(
        Visit visit,
        PhysicianSummary physician,
        DateTime? previousFollowUpDate,
        JsonElement? followUpDate,
        JsonElement? followUpNotes,
        CancellationToken cancellationToken)
    {
        var requestedDate = ReadString(followUpDate);
        if (string.IsNullOrEmpty(requestedDate)) return;

        // Node compares the RAW client string against the stored instant's UTC date part, so a client
        // sending a full ISO timestamp re-notifies on every 3-second autosave. Stored values are UTC,
        // so formatting the DateTime directly yields the same "YYYY-MM-DD" toISOString() produced.
        var previousKey = previousFollowUpDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (requestedDate == previousKey) return;

        var doctorName = string.IsNullOrEmpty(physician.DisplayName) ? "Your doctor" : physician.DisplayName;

        // toLocaleDateString('en-US', {weekday, month, day, year}) renders in the SERVER's timezone,
        // so a UTC-midnight date reads as the previous day on a negative-offset host.
        var readableDate = visit.FollowUpDate?.ToLocalTime()
            .ToString("dddd, MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));

        var notes = ReadString(followUpNotes);
        var suffix = string.IsNullOrEmpty(notes) ? string.Empty : $" Note: {notes}";

        await notifications.PublishAsync(
            new NotificationRequest(
                UserId: visit.PatientUserId,
                Type: "FOLLOW_UP_SET",
                Title: "Follow-up Date Set",
                Message: $"Dr. {doctorName} has scheduled a follow-up for {readableDate}.{suffix}",
                // The payload carries the raw client string, not the normalized instant.
                Data: new JsonObject
                {
                    ["visitId"] = visit.Id,
                    ["followUpDate"] = requestedDate
                }.ToJsonString(),
                SendEmail: true),
            cancellationToken);
    }

    private static string? ReadString(JsonElement? value)
        => value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static IReadOnlyList<string>? ReadStringArray(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array) return null;

        var items = new List<string>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String) items.Add(element.GetString()!);
        }

        return items;
    }

    /// <summary>Arbitrary JSON is stored verbatim; nothing server-side reads into these columns.</summary>
    private static string? ReadRawJson(JsonElement? value)
        => value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } json
            ? json.GetRawText()
            : null;

    /// <summary>
    /// The client sends a bare "YYYY-MM-DD", which JavaScript's <c>new Date()</c> reads as UTC
    /// midnight. Parsing it with local-time defaults would shift every follow-up by the server's
    /// offset, so UTC is forced. Falsy input clears the column, matching
    /// <c>followUpDate ? new Date(followUpDate) : null</c>.
    /// </summary>
    private static DateTime? ReadUtcDate(JsonElement? value)
    {
        var raw = ReadString(value);
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            return DateTime.Parse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        catch (FormatException)
        {
            // `new Date("garbage")` is an Invalid Date, Prisma rejects it and the route's catch-all
            // answers 500 {"error":"Failed to update visit"} (visits.js:471-474). Letting the
            // FormatException escape would emit the middleware's generic body instead.
            throw new BusinessException("Failed to update visit", "Failed to update visit", 500);
        }
    }
}

// ── PUT /api/visits/{id}/complete ────────────────────────────────────────────

/// <param name="SharedSections">
/// The SOAP sections the patient may see. Typed as <c>JsonElement?</c> because visits.js gates on
/// <c>Array.isArray</c> alone: a string, an object or a number is not rejected, it silently means
/// "share everything".
/// </param>
public sealed record VisitWriteCompleteVisitBody(JsonElement? SharedSections);

public sealed record VisitWriteCompleteVisitCommand(
    string VisitId,
    string CallerUserId,
    VisitWriteCompleteVisitBody? Body) : IRequest<VisitWriteCompletedResponse>;

/// <summary>
/// Port of <c>PUT /api/visits/{id}/complete</c> (visits.js:488-600). Signs the visit off, stores the
/// section whitelist, and notifies the patient and every active staff member at the clinic.
///
/// There is no idempotency guard: re-completing an already COMPLETED visit succeeds, overwrites the
/// whitelist and re-fires every notification.
///
/// ONE KNOWN DIVERGENCE, owned by the entity: visits.js:513 writes <c>completedAt: new Date()</c>
/// unconditionally, so a re-complete moves the timestamp forward and the 200 echoes the new one.
/// <c>Visit.Complete</c> stamps with <c>??=</c> and there is no setter for the column, so the port
/// keeps the FIRST completion's instant. Closing this needs a new entity member.
/// </summary>
public sealed class VisitWriteCompleteVisitHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    INotificationPublisher notifications)
    : IRequestHandler<VisitWriteCompleteVisitCommand, VisitWriteCompletedResponse>
{
    private static readonly string[] ValidSections = ["subjective", "objective", "assessment", "plan"];

    public async Task<VisitWriteCompletedResponse> Handle(
        VisitWriteCompleteVisitCommand request, CancellationToken cancellationToken = default)
    {
        var visit = await visits.GetForUpdateAsync(request.VisitId, cancellationToken)
            ?? throw new NotFoundException("Visit not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken);
        if (physician is null || visit.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your visit");

        var sectionsToStore = FilterSharedSections(request.Body?.SharedSections);

        visit.Complete(DateTime.UtcNow);

        // NULL means "share EVERY section" (GET /{id} reads it that way), and FilterSharedSections
        // collapses an empty survivor set back to NULL. So {"sharedSections":[]} — which is exactly
        // what the React checkbox grid sends when the physician unchecks everything — and
        // {"sharedSections":["bogus"]} both publish the ENTIRE SOAP note. That privacy inversion is
        // the shipped behaviour and the client's bytes depend on it.
        //
        // visits.js:511-515 writes `sharedSections: sectionsToStore` UNCONDITIONALLY, so the write
        // must go through SetSharedSections: a re-complete that resolves to null has to CLEAR the
        // whitelist stored by the first completion. Visit.Update would treat that null as "not
        // supplied", keep the old restriction and echo it back in the 200.
        visit.SetSharedSections(sectionsToStore);

        // Node re-runs prisma.visit.update on every call, bumping @updatedAt even when the row's
        // values are unchanged (a re-complete). See the note in VisitWriteUpdateVisitHandler.
        visit.ApplyModificationAudit(DateTime.UtcNow, null);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Notifications commit through the Notifications module, so they stay outside our unit of
        // work and cannot fail the completion.
        var patient = await identity.GetUserAsync(visit.PatientUserId, cancellationToken);
        var staffUserIds = await clinics.ListActiveStaffUserIdsAsync(visit.ClinicId, cancellationToken);

        var patientName = patient?.DisplayName is { Length: > 0 } displayName ? displayName : "Patient";

        // ' — ' carries an EM DASH (U+2014), and toLocaleDateString() with no locale follows the
        // server's culture. Both are reproduced as-is: this text is stored in the notification row.
        var followUpText = visit.FollowUpDate is { } due
            ? $" — Follow-up: {due.ToLocalTime().ToShortDateString()}"
            : string.Empty;

        // JSON.stringify of a Date gives exactly three fractional digits and a literal Z. Stored
        // instants are UTC, so the value is FORMATTED rather than converted — ToUniversalTime on a
        // DateTime the provider hands back as Unspecified would shift it by the server offset.
        var followUpIso = visit.FollowUpDate?.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var staffData = new JsonObject
        {
            ["visitId"] = visit.Id,
            ["patientUserId"] = visit.PatientUserId,
            ["followUpDate"] = followUpIso,
            ["clinicId"] = visit.ClinicId
        }.ToJsonString();

        var requests = new List<NotificationRequest>(staffUserIds.Count + 1)
        {
            new(
                UserId: visit.PatientUserId,
                Type: "VISIT_COMPLETED",
                Title: "Visit Summary Available",
                Message: "Your visit summary is now available. Check your health records for details.",
                Data: new JsonObject { ["visitId"] = visit.Id }.ToJsonString(),
                SendEmail: true)
        };

        foreach (var staffUserId in staffUserIds)
        {
            requests.Add(new NotificationRequest(
                UserId: staffUserId,
                Type: "VISIT_COMPLETED_STAFF",
                Title: "Visit Completed",
                Message: $"{patientName}'s visit is done{followUpText}",
                Data: staffData));
        }

        // One commit for the patient and all staff, rather than the sequential await-in-a-loop Node
        // runs inside the request.
        await notifications.PublishAsync(requests, cancellationToken);

        return VisitWriteMapper.ToCompletedResponse(visit, "Visit completed and shared with patient");
    }

    /// <summary>
    /// visits.js:505-508. Keeps only the four recognised section names, PRESERVING the client's order
    /// and any duplicates, and returns null — meaning "share all" — when nothing survives or the
    /// value was not an array at all.
    /// </summary>
    private static string? FilterSharedSections(JsonElement? sharedSections)
    {
        if (sharedSections is not { ValueKind: JsonValueKind.Array } array) return null;

        var kept = new JsonArray();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) continue;

            // Array.includes is case-sensitive, so "Subjective" is dropped.
            var name = element.GetString();
            if (name is not null && ValidSections.Contains(name)) kept.Add(name);
        }

        return kept.Count == 0 ? null : kept.ToJsonString();
    }
}

// ── DELETE /api/visits/{id} ──────────────────────────────────────────────────

public sealed record VisitWriteArchiveVisitCommand(string VisitId, string CallerUserId)
    : IRequest<MessageResponse>;

/// <summary>
/// Port of <c>DELETE /api/visits/{id}</c> (visits.js:1342-1368). Despite the verb and the JSDoc above
/// it, nothing is deleted: the status moves to ARCHIVED, <c>deleted_at</c> is untouched, and no child
/// rows are affected. The patient keeps access to the archived record.
/// </summary>
public sealed class VisitWriteArchiveVisitHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IIdentityDirectory identity)
    : IRequestHandler<VisitWriteArchiveVisitCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        VisitWriteArchiveVisitCommand request, CancellationToken cancellationToken = default)
    {
        // GUARD ORDER IS INVERTED relative to every other handler in visits.js: the physician profile
        // is resolved FIRST (visits.js:1347-1348), so a patient calling this on an id that does not
        // exist gets 403 where the rest of the file gives 404. Keep this order.
        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken)
            ?? throw new ForbiddenException("Physician access required");

        var visit = await visits.GetForUpdateAsync(request.VisitId, cancellationToken)
            ?? throw new NotFoundException("Visit not found");

        if (visit.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your visit");

        // No status precondition: IN_PROGRESS, COMPLETED and already-ARCHIVED all succeed alike.
        visit.Archive();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Visit archived successfully");
    }
}

// ── PATCH /api/visits/{id}/patient-note ──────────────────────────────────────

/// <param name="Note">
/// Stored UNTRIMMED, so "   " is a real note. Falsy — absent, null or "" — clears both the note and
/// its timestamp with no confirmation.
/// </param>
public sealed record VisitWriteSavePatientNoteBody(string? Note);

public sealed record VisitWriteSavePatientNoteCommand(
    string VisitId,
    string CallerUserId,
    VisitWriteSavePatientNoteBody? Body) : IRequest<VisitWritePatientNoteResponse>;

/// <summary>
/// Port of <c>PATCH /api/visits/{id}/patient-note</c> (visits.js:1379-1409). The patient's own note on
/// a visit, written into the same two columns as <c>PUT /{id}/patient-feedback</c> but with different
/// validation and no notification to the physician.
/// </summary>
public sealed class VisitWriteSavePatientNoteHandler(IVisitsDbContext dbContext, IVisitStore visits)
    : IRequestHandler<VisitWriteSavePatientNoteCommand, VisitWritePatientNoteResponse>
{
    public async Task<VisitWritePatientNoteResponse> Handle(
        VisitWriteSavePatientNoteCommand request, CancellationToken cancellationToken = default)
    {
        var visit = await visits.GetForUpdateAsync(request.VisitId, cancellationToken)
            ?? throw new NotFoundException("Visit not found");

        if (visit.PatientUserId != request.CallerUserId)
            throw new ForbiddenException("Not your visit");

        // NO status gate — unlike the sibling PUT /{id}/patient-feedback, which rejects anything but
        // COMPLETED with a 400. A patient can annotate an IN_PROGRESS or CANCELLED visit here, one
        // their own visit list cannot even show them. The asymmetry is real; do not harmonise it.
        var note = request.Body?.Note is { Length: > 0 } text ? text : null;

        visit.SetPatientFeedback(note);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new VisitWritePatientNoteResponse(
            visit.Id,
            visit.PatientFeedback,
            // Clearing the note nulls the timestamp too. Visit.SetPatientFeedback always stamps it,
            // so the null is applied here to keep the response faithful — the stored column keeps a
            // timestamp it should not, which needs an entity method to fix.
            note is null ? null : visit.PatientFeedbackAt);
    }
}

// ── PUT /api/visits/{id}/patient-feedback ────────────────────────────────────

public sealed record VisitWritePatientFeedbackBody(string? Feedback);

public sealed record VisitWriteSavePatientFeedbackCommand(
    string VisitId,
    string CallerUserId,
    VisitWritePatientFeedbackBody? Body) : IRequest<VisitWritePatientFeedbackResponse>;

/// <summary>
/// Port of <c>PUT /api/visits/{id}/patient-feedback</c> (visits.js:611-664). The patient attaches a
/// question to a COMPLETED visit and the physician is notified.
/// </summary>
public sealed class VisitWriteSavePatientFeedbackHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IIdentityDirectory identity,
    INotificationPublisher notifications)
    : IRequestHandler<VisitWriteSavePatientFeedbackCommand, VisitWritePatientFeedbackResponse>
{
    public async Task<VisitWritePatientFeedbackResponse> Handle(
        VisitWriteSavePatientFeedbackCommand request, CancellationToken cancellationToken = default)
    {
        var feedback = request.Body?.Feedback;

        // Four gates, and their ORDER is observable. The text check runs before the visit is even
        // fetched, so empty feedback against a nonexistent visit answers 400, not 404.
        if (string.IsNullOrWhiteSpace(feedback))
            throw VisitWriteErrors.BadRequest("Feedback text is required");

        var visit = await visits.GetForUpdateAsync(request.VisitId, cancellationToken)
            ?? throw new NotFoundException("Visit not found");

        // Different wording from the "Not your visit" every other route in this file uses.
        if (visit.PatientUserId != request.CallerUserId)
            throw new ForbiddenException("Only the patient can leave feedback");

        // ARCHIVED visits are rejected even though the patient can still see them in their inbox.
        if (visit.Status != VisitStatus.COMPLETED)
            throw VisitWriteErrors.BadRequest("Can only add feedback to completed visits");

        // Stored trimmed here, stored raw by PATCH /{id}/patient-note — the same column, two rules.
        visit.SetPatientFeedback(feedback.Trim());
        await dbContext.SaveChangesAsync(cancellationToken);

        // Looked up by PROFILE id, not user id: the visit stores physicianId and the notification
        // needs the owner's userId. No notification at all if the profile has gone.
        var physician = await identity.GetPhysicianAsync(visit.PhysicianId, cancellationToken);
        if (physician is not null)
        {
            var patient = await identity.GetUserAsync(request.CallerUserId, cancellationToken);
            var patientName = patient?.DisplayName is { Length: > 0 } displayName
                ? displayName
                : "A patient";

            await notifications.PublishAsync(
                new NotificationRequest(
                    UserId: physician.UserId,
                    Type: "PATIENT_FEEDBACK",
                    Title: "Patient Left Visit Feedback",
                    Message: $"{patientName} left a note on a completed visit.",
                    Data: new JsonObject { ["visitId"] = visit.Id }.ToJsonString(),
                    SendEmail: false),
                cancellationToken);
        }

        return new VisitWritePatientFeedbackResponse(
            visit.PatientFeedback!, visit.PatientFeedbackAt!.Value);
    }
}

// ── DELETE /api/visits/{id}/patient-dismiss ──────────────────────────────────

public sealed record VisitWriteDismissVisitCommand(string VisitId, string CallerUserId)
    : IRequest<MessageResponse>;

/// <summary>
/// Port of <c>DELETE /api/visits/{id}/patient-dismiss</c> (visits.js:1419-1440). The patient hides a
/// visit from their own inbox; the physician's record is untouched and nothing is deleted. No
/// endpoint anywhere undoes this.
/// </summary>
public sealed class VisitWriteDismissVisitHandler(IVisitsDbContext dbContext, IVisitStore visits)
    : IRequestHandler<VisitWriteDismissVisitCommand, MessageResponse>
{
    public async Task<MessageResponse> Handle(
        VisitWriteDismissVisitCommand request, CancellationToken cancellationToken = default)
    {
        var visit = await visits.GetForUpdateAsync(request.VisitId, cancellationToken)
            ?? throw new NotFoundException("Visit not found");

        // No status gate: an IN_PROGRESS visit can be dismissed before the physician completes it,
        // and completing it later does not bring it back.
        if (visit.PatientUserId != request.CallerUserId)
            throw new ForbiddenException("Not your visit");

        visit.DismissForPatient();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Visit removed from your inbox");
    }
}
