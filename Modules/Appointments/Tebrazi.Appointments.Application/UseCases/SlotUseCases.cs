using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tebrazi.Appointments.Application.Abstractions.Persistence;
using Tebrazi.Appointments.Application.ApiModels.Responses;
using Tebrazi.Appointments.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Appointments.Application.UseCases;

// ─────────────────────────────────────────────────────────────────────────────
// The TIME-SLOT endpoints of server/src/routes/appointments.js — a physician's recurring weekly
// availability template, the route that appends to it and the two that regenerate it.
//
//   GET    /api/appointments/slots                  appointments.js:28-50
//   POST   /api/appointments/slots                  appointments.js:57-90
//   DELETE /api/appointments/slots/{id}             appointments.js:96-115
//   POST   /api/appointments/slots/bulk             appointments.js:126-178
//   POST   /api/appointments/slots/sync-from-hours  appointments.js:183-248
//
// THREE THINGS RUN THROUGH THIS WHOLE FILE.
//
// 1. DESTRUCTIVE-THEN-FAIL. Both generation routes issue their `deleteMany` BEFORE they know
//    whether anything can be created, and Node LEAVES THE DELETION COMMITTED when the request
//    then fails — /slots/bulk answers 400 "Time range too short for slot duration" with the
//    day's slots already gone (appointments.js:138-140 then :170), and either route's
//    `createMany` can throw after the wipe. ITimeSlotStore.DeleteAllAsync is an ExecuteDelete
//    that commits on its own for exactly this reason. NOTHING HERE MAY BE WRAPPED IN
//    ExecuteInTransactionAsync: a transaction would roll the deletion back, so the client would
//    keep slots that Node destroys. That is an observable behaviour change, not a repair.
//
// 2. JAVASCRIPT COERCION IS THE CONTRACT. `slotDuration || 30` turns a sent 0 into 30;
//    `dayOfWeek === undefined` lets a present null through; `slotDuration` is NOT parseInt'd, so
//    a string makes `currentMinutes + duration` CONCATENATE; and a malformed time fails softly
//    into "zero slots" rather than an error. SlotJs and SlotGenerator at the bottom of this file
//    reproduce those semantics, which is why the quirky body members are non-nullable
//    JsonElement — an absent key, a present null, a number and a numeric string all mean
//    different things here, and only ValueKind.Undefined vs ValueKind.Null keeps the first two
//    apart (a JsonElement? collapses both to C# null).
//
// 3. THE DEFAULTS DIFFER PER ROUTE. slotDuration falls back to 30 in /slots/bulk
//    (appointments.js:142) and to 15 in /slots/sync-from-hours (appointments.js:210), while the
//    column default is 30. The entity default cannot express that, so every TimeSlot.Create call
//    here passes the value explicitly.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The bare <c>{ "error": "..." }</c> bodies these five routes return — every error in
/// appointments.js is that shape. <c>ValidationException</c> would emit
/// <c>{"error":"Validation failed","message":...}</c> instead, which the client does not match
/// on; passing the same literal as both error and message makes the exception middleware drop
/// the redundant <c>message</c> key.
/// </summary>
internal static class SlotErrors
{
    public static BusinessException Node(string error, int statusCode = 400)
        => new(error, error, statusCode);
}

/// <summary>
/// The route-level <c>try/catch</c> each of these five Node handlers is wrapped in, whose ONLY
/// outcome is that route's own named 500: <c>"Failed to list slots"</c> (appointments.js:48),
/// <c>"Failed to create slots"</c> (:89), <c>"Failed to remove slot"</c> (:113),
/// <c>"Failed to create bulk slots"</c> (:176), <c>"Failed to sync slots"</c> (:246).
///
/// <para>Anything unmodelled — a SaveChanges fault, an ExecuteDelete timeout, a directory read
/// failure — would otherwise escape to ExceptionHandlingMiddleware and render the generic
/// <c>{"error":"Internal Server Error"}</c>, which the client displays verbatim. On the two
/// generation routes that matters twice over: the destructive wipe has already committed by then,
/// so the caller has to be told what actually failed.</para>
/// </summary>
internal static class SlotGuard
{
    /// <inheritdoc cref="SlotGuard"/>
    /// <remarks>
    /// An <see cref="AppException"/> passes through untouched, so every modelled 400/403/404 —
    /// and the deliberate Prisma-rejection 500s these routes throw themselves — keeps its own
    /// body. A cancellation the caller triggered also passes through.
    /// </remarks>
    public static async Task<TResponse> RunAsync<THandler, TResponse>(
        IAppLogger<THandler> logger,
        string logMessage,
        string failureError,
        CancellationToken cancellationToken,
        Func<Task<TResponse>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception exception)
            when (exception is not AppException && !cancellationToken.IsCancellationRequested)
        {
            logger.Error(logMessage, exception);
            throw SlotErrors.Node(failureError, 500);
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/slots
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The physician's own active weekly template, as a BARE ARRAY. There is no 400 and no 403 on
/// this route: a patient, a clinic staff member, an unknown <c>?clinicId</c> and another
/// physician's clinic id all answer <c>200 []</c> (appointments.js:33), because the query is
/// pinned to the caller's own physician profile.
/// </summary>
/// <param name="ClinicId">
/// Optional, and gated on JavaScript truthiness (appointments.js:36) — so <c>?clinicId=</c> is an
/// ABSENT filter, not a filter on the empty string. Never checked for existence or ownership,
/// which is safe only because <c>physicianId</c> already scopes the query. Omitted, the row set
/// spans every clinic of that physician, which is why each row carries <c>clinic.name</c>.
/// </param>
public sealed record SlotListQuery(string? ClinicId) : IRequest<IReadOnlyList<SlotListItem>>;

/// <summary>
/// Port of <c>GET /api/appointments/slots</c> (appointments.js:28-50). A read handler, so it
/// resolves the caller from <see cref="ICurrentUser"/> itself, per the Visits house rule.
/// </summary>
public sealed class SlotListHandler(
    ICurrentUser currentUser,
    ITimeSlotStore timeSlots,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<SlotListHandler> logger)
    : IRequestHandler<SlotListQuery, IReadOnlyList<SlotListItem>>
{
    public Task<IReadOnlyList<SlotListItem>> Handle(
        SlotListQuery request, CancellationToken cancellationToken = default)
        => SlotGuard.RunAsync(
            logger, "[Appointments] List slots error", "Failed to list slots", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<SlotListItem>> HandleCore(
        SlotListQuery request, CancellationToken cancellationToken)
    {
        var userId = SlotCaller.RequireUserId(currentUser);

        // No physician profile is `res.json([])` at 200, NOT a 403 — the client's
        // AppointmentsPage.fetchPhysicianData relies on it. Four other endpoints answer the same
        // condition four different ways; do not harmonise them (see docs/appointments-surface.md).
        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);
        if (physician is null) return [];

        // The store applies `IsActive == true` and orders dayOfWeek then startTime. startTime is
        // a VARCHAR, so that second key is a LEXICOGRAPHIC sort — correct only because every
        // generator here zero-pads. Sorting by a parsed TimeSpan would reorder rows whose
        // startTime was stored unpadded by POST /slots, which writes times verbatim.
        var rows = await timeSlots.ListActiveAsync(
            physician.Id, SlotFilters.Truthy(request.ClinicId), cancellationToken);

        if (rows.Count == 0) return [];

        // One batched Clinics read standing in for Prisma's include; the route is uncached and
        // unpaginated, so a physician with 15-minute slots across several clinics can return
        // several hundred rows against a handful of distinct clinic ids.
        string[] clinicIds = [.. rows.Select(slot => slot.ClinicId).Distinct()];
        var clinicsById = await clinics.GetClinicsAsync(clinicIds, cancellationToken);

        return
        [
            .. rows.Select(slot => SlotResponseMapper.ToListItem(
                slot, clinicsById.GetValueOrDefault(slot.ClinicId)))
        ];
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/appointments/slots
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The body of <c>POST /api/appointments/slots</c>.
/// </summary>
/// <param name="ClinicId">Required on JS truthiness, so <c>""</c> is missing.</param>
/// <param name="Slots">
/// Required, and required to be a NON-EMPTY array: <c>!slots || !Array.isArray(slots) ||
/// slots.length === 0</c> (appointments.js:62). An empty array is a 400 here, whereas
/// <c>/slots/sync-from-hours</c> accepts one — the two guards are deliberately different.
/// </param>
public sealed record SlotCreateBody(string? ClinicId, JsonElement Slots);

public sealed record SlotCreateCommand(
    string CallerUserId,
    SlotCreateBody? Body) : IRequest<SlotCreatedResponse>;

/// <summary>
/// Port of <c>POST /api/appointments/slots</c> (appointments.js:57-90) — the physician posting an
/// explicit list of windows rather than a range to be walked.
///
/// <para>It is the ONLY slot-creating route with no <c>deleteMany</c>: it APPENDS. Posting the
/// same array twice leaves two identical rows, and there is no unique constraint to stop it, so
/// <c>GET /slots</c> then lists every duplicate while <c>GET /available</c> collapses them by
/// startTime. That is the contract; do not add a dedupe.</para>
///
/// <para>Guard order is observable and reproduced: the body shape first (400), then the
/// physician profile (400, not 403), then clinic ownership (403, conflating "no such clinic" with
/// "someone else's clinic"). Anything the columns then refuse — an unparsed <c>dayOfWeek</c>, a
/// non-string time, a non-integer <c>slotDuration</c> — is one all-or-nothing <c>createMany</c>
/// failure and the route's own <c>500 "Failed to create slots"</c>, with nothing written.</para>
/// </summary>
public sealed class SlotCreateHandler(
    IAppointmentsDbContext dbContext,
    ITimeSlotStore timeSlots,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<SlotCreateHandler> logger)
    : IRequestHandler<SlotCreateCommand, SlotCreatedResponse>
{
    public Task<SlotCreatedResponse> Handle(
        SlotCreateCommand request, CancellationToken cancellationToken = default)
        => SlotGuard.RunAsync(
            logger, "[Appointments] Create slots error", "Failed to create slots", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<SlotCreatedResponse> HandleCore(
        SlotCreateCommand request, CancellationToken cancellationToken)
    {
        var clinicId = request.Body?.ClinicId;
        var slots = request.Body?.Slots ?? default;

        // `!clinicId || !slots || !Array.isArray(slots) || slots.length === 0`
        // (appointments.js:61-63). A non-array truthy `slots` — an object, a string — lands here
        // too, because Array.isArray rejects it.
        if (string.IsNullOrEmpty(clinicId)
            || slots.ValueKind is not JsonValueKind.Array
            || slots.GetArrayLength() == 0)
        {
            throw SlotErrors.Node("clinicId and slots array required");
        }

        // 400, not 403: clinic staff and patients land here (appointments.js:66-67).
        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken)
            ?? throw SlotErrors.Node("Physician profile required");

        // findFirst({ id: clinicId, physicianId }) — no 404 path exists (appointments.js:70-73).
        if (!await clinics.IsOwningPhysicianAsync(clinicId, physician.Id, cancellationToken))
            throw new ForbiddenException("Not your clinic");

        var created = new List<TimeSlot>();

        foreach (var entry in slots.EnumerateArray())
        {
            // `slots.map(s => ({ ... s.dayOfWeek ... }))` — destructuring is not used here, so a
            // null or scalar entry does not throw on property access; `s.dayOfWeek` on a non-object
            // is undefined, and `null.dayOfWeek` IS a TypeError. Both end at the same outer catch
            // as the column rejections below, and nothing has been written yet either way.
            if (entry.ValueKind is not JsonValueKind.Object)
                throw SlotErrors.Node("Failed to create slots", 500);

            var rawDayOfWeek = entry.TryGetProperty("dayOfWeek", out var day) ? day : default;
            var rawStartTime = entry.TryGetProperty("startTime", out var start) ? start : default;
            var rawEndTime = entry.TryGetProperty("endTime", out var end) ? end : default;
            var duration = SlotJs.ResolveDuration(
                entry.TryGetProperty("slotDuration", out var rawDuration) ? rawDuration : null,
                fallback: 30);

            // The Int and String columns are NOT NULL and NOT coerced. dayOfWeek must arrive as a
            // JSON number that is a whole Int32 — a string "1" is refused here where the bulk
            // route's parseInt would have accepted it — and the two times must arrive as strings.
            if (rawDayOfWeek.ValueKind is not JsonValueKind.Number
                || !rawDayOfWeek.TryGetDouble(out var dayNumber)
                || !double.IsInteger(dayNumber)
                || dayNumber < int.MinValue || dayNumber > int.MaxValue
                || rawStartTime.ValueKind is not JsonValueKind.String
                || rawEndTime.ValueKind is not JsonValueKind.String
                || duration.Stored is not { } slotDuration)
            {
                throw SlotErrors.Node("Failed to create slots", 500);
            }

            created.Add(TimeSlot.Create(
                clinicId: clinicId,
                physicianId: physician.Id,
                dayOfWeek: (int)dayNumber,
                startTime: rawStartTime.GetString()!,
                endTime: rawEndTime.GetString()!,
                slotDuration: slotDuration));
        }

        // One AddRange plus one save, mirroring createMany's single all-or-nothing statement.
        timeSlots.AddRange(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new SlotCreatedResponse(created.Count, $"{created.Count} slots created");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  DELETE /api/appointments/slots/{id}
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Deactivates one slot. The verb is DELETE and the operation is an UPDATE: the row survives
/// with <c>isActive = false</c> and only the two generation routes ever physically remove it.
/// </summary>
public sealed record SlotDeleteCommand(string SlotId, string CallerUserId) : IRequest<MessageResponse>;

/// <summary>
/// Port of <c>DELETE /api/appointments/slots/{id}</c> (appointments.js:96-115).
///
/// <para>GATE ORDER IS OBSERVABLE AND IS REPRODUCED: the slot is fetched FIRST, so a fake id
/// answers <c>404 "Slot not found"</c> while a real one the caller does not own answers
/// <c>403 "Not your slot"</c> — which lets any authenticated caller probe slot-id existence.
/// The two 403 reasons ("you are not a physician at all" and "this slot belongs to someone
/// else") are collapsed into one string by appointments.js:101-103; do not split them.</para>
///
/// <para>There is no clinic scoping, so a physician may deactivate a slot at a clinic they no
/// longer own, and the call is repeatable: <c>GetForUpdateAsync</c> ignores <c>IsActive</c>, so a
/// second DELETE finds the row again and answers 200 rather than 404.</para>
/// </summary>
public sealed class SlotDeleteHandler(
    IAppointmentsDbContext dbContext,
    ITimeSlotStore timeSlots,
    IIdentityDirectory identity,
    IAppLogger<SlotDeleteHandler> logger)
    : IRequestHandler<SlotDeleteCommand, MessageResponse>
{
    public Task<MessageResponse> Handle(
        SlotDeleteCommand request, CancellationToken cancellationToken = default)
        => SlotGuard.RunAsync(
            logger, "[Appointments] Delete slot error", "Failed to remove slot", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<MessageResponse> HandleCore(
        SlotDeleteCommand request, CancellationToken cancellationToken)
    {
        var slot = await timeSlots.GetForUpdateAsync(request.SlotId, cancellationToken)
            ?? throw new NotFoundException("Slot not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken);
        if (physician is null || slot.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your slot");

        // Node re-issues `data: { isActive: false }` unconditionally; EF writes nothing when the
        // row is already inactive. Same row state, same 200, same body.
        slot.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MessageResponse("Slot removed");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/appointments/slots/bulk
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The bulk body. <c>DayOfWeek</c> and <c>SlotDuration</c> are <see cref="JsonElement"/> because
/// their JavaScript semantics need the raw value: the guard on <c>dayOfWeek</c> is
/// <c>=== undefined</c>, so an ABSENT key is a 400 while a present <c>null</c> passes it, and
/// <c>slotDuration</c> is resolved with <c>||</c> against a value that may be a number, a string
/// or absent.
/// </summary>
/// <param name="ClinicId">
/// Required on JS truthiness, so <c>""</c> is missing. Left a <c>string?</c> deliberately: a
/// non-string clinicId fails in Prisma's column typing rather than in the handler, so there is no
/// handler behaviour to reproduce (the model binder answers it as a 400 where Node answers 500).
/// </param>
/// <param name="DayOfWeek">
/// Required only in the <c>=== undefined</c> sense. Stored as <c>parseInt(dayOfWeek)</c>, so
/// <c>"1abc"</c> is 1 and <c>3.7</c> is 3, and NOT range-checked by Node.
/// </param>
/// <param name="StartTime">
/// Required on truthiness, with NO format validation. Raw, because the type matters: a truthy
/// non-string reaches <c>.split(':')</c> and TypeErrors into the route's 500 — and it does so
/// AFTER the day has been wiped, which binding it as a <c>string?</c> would turn into a
/// non-destructive 400 from the model binder.
/// </param>
/// <param name="EndTime">As <paramref name="StartTime"/>.</param>
/// <param name="SlotDuration">
/// Optional, default 30 via <c>slotDuration || 30</c> — an explicit <c>0</c> becomes 30. NOT
/// parseInt'd, which is the string-concatenation trap <see cref="SlotGenerator"/> reproduces.
/// </param>
/// <remarks>
/// The four raw members are non-nullable <see cref="JsonElement"/> ON PURPOSE. A
/// <c>JsonElement?</c> cannot carry the distinction the <c>dayOfWeek</c> guard is built on:
/// System.Text.Json short-circuits a JSON <c>null</c> for any <c>Nullable&lt;T&gt;</c> target
/// before the element converter runs, so <c>{"dayOfWeek": null}</c> and an omitted
/// <c>dayOfWeek</c> both arrive as C# <c>null</c>. Left non-nullable, an absent key stays at
/// <c>default(JsonElement)</c> whose <c>ValueKind</c> is <c>Undefined</c> while an explicit null
/// binds to <c>ValueKind.Null</c> — which is exactly <c>undefined</c> versus <c>null</c>.
/// </remarks>
public sealed record SlotBulkCreateBody(
    string? ClinicId,
    JsonElement DayOfWeek,
    JsonElement StartTime,
    JsonElement EndTime,
    JsonElement SlotDuration);

public sealed record SlotBulkCreateCommand(
    string CallerUserId,
    SlotBulkCreateBody? Body) : IRequest<SlotBulkCreatedResponse>;

/// <summary>
/// Port of <c>POST /api/appointments/slots/bulk</c> (appointments.js:126-178). Wipes one
/// weekday's slots at one clinic and regenerates them by walking
/// <c>[startTime, endTime]</c> in <c>slotDuration</c> steps, then answers <c>201</c> with a
/// count — never the rows.
///
/// <para>The deletion is scoped to a SINGLE <c>dayOfWeek</c> (appointments.js:139) and carries no
/// <c>isActive</c> filter, so it purges rows an earlier <c>DELETE /slots/{id}</c> deactivated.
/// It is issued before the time range is known to be usable and is NOT rolled back when the
/// request then fails — see the file header. The client fires one request per selected weekday
/// through <c>Promise.all</c>, so each is independently destructive.</para>
/// </summary>
public sealed class SlotBulkCreateHandler(
    IAppointmentsDbContext dbContext,
    ITimeSlotStore timeSlots,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<SlotBulkCreateHandler> logger)
    : IRequestHandler<SlotBulkCreateCommand, SlotBulkCreatedResponse>
{
    public Task<SlotBulkCreatedResponse> Handle(
        SlotBulkCreateCommand request, CancellationToken cancellationToken = default)
        => SlotGuard.RunAsync(
            logger, "[Appointments] Bulk slots error", "Failed to create bulk slots", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<SlotBulkCreatedResponse> HandleCore(
        SlotBulkCreateCommand request, CancellationToken cancellationToken)
    {
        var body = request.Body;
        var clinicId = body?.ClinicId;

        // No body at all destructures to four `undefined`s in Node, which is what
        // `default(JsonElement)` is — see the remarks on SlotBulkCreateBody.
        var rawDayOfWeek = body?.DayOfWeek ?? default;
        var startTime = body?.StartTime ?? default;
        var endTime = body?.EndTime ?? default;

        // `!clinicId || dayOfWeek === undefined || !startTime || !endTime` (appointments.js:130).
        // The undefined test — not a falsy one — is what lets dayOfWeek 0 (Sunday) through, and
        // it also lets a present null through, which fails later as a 500. The two times are
        // gated on JS truthiness, so 0, "" and false are all "missing" while any object is not.
        if (string.IsNullOrEmpty(clinicId)
            || rawDayOfWeek.ValueKind is JsonValueKind.Undefined
            || !SlotJs.IsTruthy(startTime)
            || !SlotJs.IsTruthy(endTime))
        {
            throw SlotErrors.Node("All fields required");
        }

        // 400, not 403: clinic staff and patients land here (appointments.js:134).
        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken)
            ?? throw SlotErrors.Node("Physician profile required");

        // findFirst({ id: clinicId, physicianId }) — "no such clinic" and "someone else's clinic"
        // are conflated into this one 403, and there is no 404 path (appointments.js:136-140).
        if (!await clinics.IsOwningPhysicianAsync(clinicId, physician.Id, cancellationToken))
            throw new ForbiddenException("Not your clinic");

        // `parseInt(dayOfWeek)` — used both in the delete filter and in the created rows.
        //
        // A NaN (a present `null`, "abc") or an out-of-Int32 value poisons Node's delete filter
        // and its createMany alike: nothing is removed, nothing is created, and the outer catch
        // answers 500. Failing here rather than after the delete reaches the SAME observable
        // state, because a NaN filter matches no rows.
        var dayOfWeek = SlotJs.ParseIntToInt32(rawDayOfWeek)
            ?? throw SlotErrors.Node("Failed to create bulk slots", 500);

        // DESTRUCTIVE, FIRST, AND ON ITS OWN COMMIT. Everything below can still fail, and when it
        // does these rows stay gone — see the file header. Do not move this after the generation
        // and do not wrap it in a transaction.
        await timeSlots.DeleteAllAsync(physician.Id, clinicId, dayOfWeek, cancellationToken);

        // A truthy NON-STRING time reaches `.split(':')` and TypeErrors (appointments.js:145-146)
        // — after the wipe, which is why this check lives here rather than in the guard above.
        if (startTime.ValueKind is not JsonValueKind.String || endTime.ValueKind is not JsonValueKind.String)
            throw SlotErrors.Node("Failed to create bulk slots", 500);

        var duration = SlotJs.ResolveDuration(body?.SlotDuration, fallback: 30);
        var windows = SlotGenerator.Generate(startTime.GetString()!, endTime.GetString()!, duration);

        // Zero slots is a 400 AFTER the wipe. It is also the landing place for every malformed
        // input the route never validates — "abc" times, "9" without minutes, an endTime at or
        // before the startTime, an overnight range, and a string slotDuration — so the message
        // routinely lies about the cause (appointments.js:170).
        if (windows.Count == 0)
            throw SlotErrors.Node("Time range too short for slot duration");

        // An out-of-range dayOfWeek is NOT checked: Node stores 7, 99 and -1 and answers 201 with
        // slots no patient can ever see, because GET /available matches on `targetDate.getDay()`.
        // Only a value the Int column itself refuses is a 500, and for slotDuration that is a
        // non-integer, a `true` or an out-of-Int32 value reaching the column unparsed — Node's
        // own Prisma rejection, which takes the whole createMany with it.
        if (duration.Stored is not { } slotDuration)
            throw SlotErrors.Node("Failed to create bulk slots", 500);

        var created = windows
            .Select(w => TimeSlot.Create(
                clinicId: clinicId,
                physicianId: physician.Id,
                dayOfWeek: dayOfWeek,
                startTime: w.StartTime,
                endTime: w.EndTime,
                slotDuration: slotDuration))
            .ToList();

        // One AddRange plus one save, mirroring createMany's single all-or-nothing statement.
        // (Contrast POST /recurring, which must save per occurrence.)
        timeSlots.AddRange(created);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new SlotBulkCreatedResponse(created.Count, $"{created.Count} slots created");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/appointments/slots/sync-from-hours
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The sync body. <c>Schedule</c> stays a raw <see cref="JsonElement"/> for two reasons: the
/// guard is <c>Array.isArray</c> alone, and the WHOLE array — malformed entries and unknown keys
/// included — is re-serialized verbatim onto the clinic's <c>workingHours</c> at the end
/// (appointments.js:240). A typed list would silently drop the keys that must be persisted.
/// </summary>
/// <param name="ClinicId">Required on JS truthiness.</param>
/// <param name="Schedule">
/// Must be a JSON ARRAY. An EMPTY array is truthy in JavaScript and is therefore a legal
/// "delete everything" request. Each entry is read as
/// <c>{ dayOfWeek, startTime, endTime, slotDuration? }</c>.
/// </param>
public sealed record SlotSyncFromHoursBody(string? ClinicId, JsonElement? Schedule);

public sealed record SlotSyncFromHoursCommand(
    string CallerUserId,
    SlotSyncFromHoursBody? Body) : IRequest<SlotSyncFromHoursResponse>;

/// <summary>
/// Port of <c>POST /api/appointments/slots/sync-from-hours</c> (appointments.js:183-248).
/// Replaces a clinic's ENTIRE weekly grid from a structured schedule and then writes that
/// schedule back onto <c>Clinic.workingHours</c>. Answers <c>200</c> — not the 201 its sibling
/// uses — with a count that is legitimately 0.
///
/// <para>THE WIPE IS NOT DAY-SCOPED (appointments.js:200-202): it removes every weekday for the
/// physician at that clinic, including deactivated rows, so a schedule containing Monday alone
/// erases Tuesday through Sunday. It commits on its own and is never rolled back — a later
/// failure leaves the clinic with no slots AND a stale <c>workingHours</c>.</para>
///
/// <para>Malformed entries are SILENTLY SKIPPED, and an entry whose range is too short for its
/// duration silently contributes nothing — the exact opposite of <c>/slots/bulk</c>, which 400s
/// in that situation. The client cannot tell that 3 of 7 days were dropped; it only sees a lower
/// count.</para>
/// </summary>
public sealed class SlotSyncFromHoursHandler(
    IAppointmentsDbContext dbContext,
    ITimeSlotStore timeSlots,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IClinicWorkingHoursWriter workingHours,
    IAppLogger<SlotSyncFromHoursHandler> logger)
    : IRequestHandler<SlotSyncFromHoursCommand, SlotSyncFromHoursResponse>
{
    /// <summary>
    /// Node writes <c>JSON.stringify(schedule)</c>, which minifies and does not escape
    /// non-ASCII. Relaxed escaping keeps a schedule carrying Arabic clinic text byte-comparable
    /// with what Node stored; the default encoder would emit <c>\uXXXX</c> instead.
    /// </summary>
    private static readonly JsonSerializerOptions WorkingHoursSerializerOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Task<SlotSyncFromHoursResponse> Handle(
        SlotSyncFromHoursCommand request, CancellationToken cancellationToken = default)
        => SlotGuard.RunAsync(
            logger, "[Appointments] Sync from hours error", "Failed to sync slots", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<SlotSyncFromHoursResponse> HandleCore(
        SlotSyncFromHoursCommand request, CancellationToken cancellationToken)
    {
        var body = request.Body;
        var clinicId = body?.ClinicId;

        // `!clinicId || !schedule || !Array.isArray(schedule)` (appointments.js:185). Note the
        // wording — "clinicId and a schedule array are required", with the article, is NOT
        // /slots/bulk's "All fields required". An empty array passes.
        if (string.IsNullOrEmpty(clinicId)
            || body?.Schedule is not { ValueKind: JsonValueKind.Array } schedule)
        {
            throw SlotErrors.Node("clinicId and a schedule array are required");
        }

        // 400, not 403 — same as /slots/bulk (appointments.js:193).
        var physician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken)
            ?? throw SlotErrors.Node("Physician profile required");

        if (!await clinics.IsOwningPhysicianAsync(clinicId, physician.Id, cancellationToken))
            throw new ForbiddenException("Not your clinic");

        // EVERY weekday at this clinic, hard, committed on its own, before anything is known
        // about the schedule's contents. See the class summary and the file header.
        await timeSlots.DeleteAllAsync(physician.Id, clinicId, dayOfWeek: null, cancellationToken);

        var generated = new List<SlotSyncGeneratedRow>();

        foreach (var entry in schedule.EnumerateArray())
        {
            // `const { dayOfWeek, ... } = entry` — destructuring null throws a TypeError, which
            // the outer catch turns into this 500, with the wipe already committed.
            if (entry.ValueKind is JsonValueKind.Null)
                throw SlotErrors.Node("Failed to sync slots", 500);

            // Destructuring a string, number or boolean yields undefined members instead of
            // throwing, so such an entry is skipped exactly like a malformed object.
            if (entry.ValueKind is not JsonValueKind.Object)
                continue;

            // `if (dayOfWeek === undefined || !startTime || !endTime) continue` — an ABSENT
            // dayOfWeek skips the entry, a present null does not (appointments.js:208).
            if (!entry.TryGetProperty("dayOfWeek", out var rawDayOfWeek))
                continue;

            if (!entry.TryGetProperty("startTime", out var rawStartTime) || !SlotJs.IsTruthy(rawStartTime))
                continue;

            if (!entry.TryGetProperty("endTime", out var rawEndTime) || !SlotJs.IsTruthy(rawEndTime))
                continue;

            // A truthy non-string time reaches `.split(':')` and TypeErrors — a 500, not a skip.
            if (rawStartTime.ValueKind is not JsonValueKind.String
                || rawEndTime.ValueKind is not JsonValueKind.String)
            {
                throw SlotErrors.Node("Failed to sync slots", 500);
            }

            // 15 here, against /slots/bulk's 30 and the column's 30 — three defaults for one
            // column in one file (appointments.js:210).
            var duration = SlotJs.ResolveDuration(
                entry.TryGetProperty("slotDuration", out var rawDuration) ? rawDuration : (JsonElement?)null,
                fallback: 15);

            var dayOfWeek = SlotJs.ParseIntToInt32(rawDayOfWeek);

            // No dedupe across entries and no unique constraint on the table: two entries for the
            // same weekday with overlapping windows both generate, and GET /slots then shows every
            // duplicate while GET /available collapses them by startTime.
            foreach (var window in SlotGenerator.Generate(
                rawStartTime.GetString()!, rawEndTime.GetString()!, duration))
            {
                generated.Add(new SlotSyncGeneratedRow(dayOfWeek, duration.Stored, window));
            }
        }

        if (generated.Count > 0)
        {
            // createMany is ONE statement: a single unusable row fails the whole insert, so
            // nothing is created, the wipe stays committed and workingHours is never touched.
            // A NaN dayOfWeek (a present null, "abc") and a non-integer slotDuration are Node's
            // own Prisma rejections. An out-of-RANGE dayOfWeek is not one of them — 7 and 99 are
            // stored and answered 200, so they must NOT fail the batch here: doing so would leave
            // an ISO-weekday schedule (Mon=1…Sun=7) with its grid wiped and no rows rebuilt.
            if (generated.Any(row => row.DayOfWeek is null || row.SlotDuration is null))
                throw SlotErrors.Node("Failed to sync slots", 500);

            timeSlots.AddRange(generated.Select(row => TimeSlot.Create(
                clinicId: clinicId,
                physicianId: physician.Id,
                dayOfWeek: row.DayOfWeek!.Value,
                startTime: row.Window.StartTime,
                endTime: row.Window.EndTime,
                slotDuration: row.SlotDuration!.Value)));

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // DOUBLE ENCODING, ON PURPOSE. Prisma's `workingHours` is a Json? column and the route
        // assigns `JSON.stringify(schedule)` (appointments.js:240), i.e. a JSON *string* holding
        // serialized JSON rather than a JSON array — and ClinicDetailPage.jsx:81 depends on it,
        // calling JSON.parse(response.data.workingHours). Storing the array as real JSON would
        // make that parse throw. The RAW array goes in, including entries skipped above and any
        // unknown keys, so the persisted schedule can describe days that generated no slots.
        //
        // A second commit, through the Clinics unit of work, AFTER our own save and outside any
        // transaction of ours — exactly like Node's separate clinic.update. `false` means the row
        // vanished between the ownership check and here, which is Node's P2025 → 500.
        if (!await workingHours.SetWorkingHoursAsync(
                clinicId,
                JsonSerializer.Serialize(schedule, WorkingHoursSerializerOptions),
                cancellationToken))
        {
            throw SlotErrors.Node("Failed to sync slots", 500);
        }

        return new SlotSyncFromHoursResponse(generated.Count, $"{generated.Count} slots created");
    }
}

/// <summary>
/// One row <c>/slots/sync-from-hours</c> is about to insert, carrying the two values that can
/// still be unusable — a NaN or out-of-range <c>dayOfWeek</c>, and a <c>slotDuration</c> the Int
/// column would reject — so the whole batch can be judged after generation, the way
/// <c>createMany</c> judges it.
/// </summary>
internal readonly record struct SlotSyncGeneratedRow(int? DayOfWeek, int? SlotDuration, SlotWindow Window);

// ═════════════════════════════════════════════════════════════════════════════
//  Shared helpers: the caller check, the query-filter coercion, slot generation, and the
//  JavaScript semantics the two generation routes depend on.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The caller check for the one read endpoint in this group. <c>router.use(authCheck)</c> is
/// path-agnostic in Node, so a request with no usable token never reaches a handler.
/// </summary>
internal static class SlotCaller
{
    /// <summary>
    /// <c>401 {"error":"No token provided"}</c> — the literal Node body, which is why this is a
    /// <see cref="BusinessException"/> and not <c>UnauthorizedException</c> (that one emits
    /// <c>{"error":"Unauthorized", ...}</c>). Unreachable behind <c>[Authorize]</c>, and kept so
    /// a null user id cannot silently become a query filter.
    /// </summary>
    public static string RequireUserId(ICurrentUser currentUser)
        => string.IsNullOrEmpty(currentUser.UserId)
            ? throw new BusinessException("No token provided", "No token provided", 401)
            : currentUser.UserId;
}

/// <summary>Query-filter coercion for <c>GET /api/appointments/slots</c>.</summary>
internal static class SlotFilters
{
    /// <summary>
    /// JavaScript truthiness for an optional query filter. <c>if (clinicId)</c>
    /// (appointments.js:36) means <c>?clinicId=</c> is an ABSENT filter, while ASP.NET model
    /// binding hands a present-but-valueless key over as <c>""</c> and the store tests
    /// <c>is not null</c> — so without this the port would filter on the empty string and return
    /// nothing where Node returns everything.
    ///
    /// Empty ONLY, deliberately: <c>" "</c> is truthy in JavaScript, so a whitespace filter must
    /// still reach the query and match nothing.
    /// </summary>
    public static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>One generated bookable window, as the two "HH:mm" STRINGS the column stores.</summary>
internal readonly record struct SlotWindow(string StartTime, string EndTime);

/// <summary>
/// A resolved <c>slotDuration || &lt;fallback&gt;</c>, carrying enough of JavaScript's semantics
/// to reproduce both the arithmetic and the Int column's rejection of what the arithmetic used.
/// </summary>
/// <param name="Stored">
/// The value that would reach the <c>slot_duration</c> Int column, or null when Node would send
/// it something an Int cannot hold — a string, <c>true</c>, an object, a fraction. A null here
/// means the route's <c>createMany</c> throws, so the handler must answer that route's 500.
/// </param>
/// <param name="Minutes">
/// The addend for numeric arithmetic. <c>NaN</c> in concatenation mode, where it is unused.
/// </param>
/// <param name="ConcatText">
/// Non-null when <c>currentMinutes + duration</c> is STRING CONCATENATION rather than addition —
/// the value's <c>String()</c> form. Node does not <c>parseInt</c> <c>slotDuration</c>, so a
/// posted <c>"15"</c> makes <c>540 + "15"</c> the string <c>"54015"</c>, which fails the loop's
/// <c>&lt;= endMinutes</c> test and silently yields zero slots.
/// </param>
internal sealed record SlotDurationSpec(int? Stored, double Minutes, string? ConcatText)
{
    public bool IsConcatenated => ConcatText is not null;
}

/// <summary>
/// The slot-walking loop both generation routes share (appointments.js:143-167 and :211-231),
/// reproduced including its coercions.
/// </summary>
internal static class SlotGenerator
{
    /// <summary>
    /// Walks <c>[startTime, endTime]</c> in <paramref name="duration"/> steps.
    ///
    /// <para>The bound is <c>while (currentMinutes + duration &lt;= endMinutes)</c>: a slot
    /// ending exactly at <c>endTime</c> is INCLUDED and a partial trailing slot is not, so
    /// 09:00-10:00 at 15 minutes is exactly four slots ending 10:00. Every malformed input fails
    /// softly to an EMPTY list, because a NaN comparison is false in JavaScript — <c>"abc"</c>
    /// times, a <c>"9"</c> with no minutes, an <c>endTime</c> at or before the <c>startTime</c>
    /// (there is no overnight support), and a string <c>slotDuration</c> all land here. Generated
    /// times are always zero-padded to two digits, which is what keeps
    /// <c>GET /slots</c>' lexicographic ordering chronological.</para>
    ///
    /// <para>ONE DELIBERATE DIVERGENCE, and it is a refusal to hang: Node loops FOREVER when
    /// <c>currentMinutes</c> does not advance — a negative <c>slotDuration</c> walks backwards
    /// while staying below <c>endMinutes</c>, and an empty-array <c>slotDuration</c> concatenates
    /// nothing and never moves — pushing rows until the process dies. The guard below stops on
    /// any non-advancing step, which turns both into "zero slots": a 400
    /// "Time range too short for slot duration" on <c>/slots/bulk</c> and a silent skip on
    /// <c>/slots/sync-from-hours</c>. Reported with this group's hand-off for
    /// docs/PORT-STATUS.md rather than claimed here.</para>
    /// </summary>
    public static List<SlotWindow> Generate(string startTime, string endTime, SlotDurationSpec duration)
    {
        var windows = new List<SlotWindow>();

        var endMinutes = ToMinutes(endTime);
        var current = ToMinutes(startTime);

        // The JavaScript variable is a NUMBER until a string duration is concatenated onto it,
        // after which it is a numeric STRING that every later coercion reads back as a number.
        var currentText = SlotJs.ToJsString(current);

        while (true)
        {
            var candidate = duration.IsConcatenated
                ? SlotJs.ToNumber(currentText + duration.ConcatText)
                : current + duration.Minutes;

            // `currentMinutes + duration <= endMinutes`, written as the NEGATION of the JS test
            // rather than as its inverse, so that a NaN on EITHER side ends the walk: every
            // relational comparison against NaN is false, so `!(NaN <= x)` and `!(x <= NaN)` are
            // both true and both break, exactly as the `while` refuses to enter its body.
            if (!(candidate <= endMinutes))
                break;

            // The anti-hang guard described above.
            if (candidate <= current)
                break;

            windows.Add(new SlotWindow(Format(current), Format(candidate)));

            currentText = duration.IsConcatenated
                ? currentText + duration.ConcatText
                : SlotJs.ToJsString(candidate);
            current = candidate;
        }

        return windows;
    }

    /// <summary>
    /// <c>const [h, m] = time.split(':').map(Number); h * 60 + m</c>. Only the first two segments
    /// are read, so "09:00:00" parses fine and "9" yields NaN through <c>Number(undefined)</c>.
    /// </summary>
    private static double ToMinutes(string time)
    {
        var parts = time.Split(':');
        var hours = SlotJs.ToNumber(parts[0]);
        var minutes = SlotJs.ToNumber(parts.Length > 1 ? parts[1] : null);

        return hours * 60 + minutes;
    }

    /// <summary>
    /// <c>String(Math.floor(v / 60)).padStart(2, '0') + ':' + String(v % 60).padStart(2, '0')</c>.
    /// Not a time formatter: a value past 24 hours renders a three-digit hour rather than
    /// wrapping, exactly as Node does.
    /// </summary>
    private static string Format(double totalMinutes)
        => $"{Pad(Math.Floor(totalMinutes / 60))}:{Pad(totalMinutes % 60)}";

    private static string Pad(double value) => SlotJs.ToJsString(value).PadLeft(2, '0');
}

/// <summary>
/// The JavaScript coercions these two routes are built on. Kept explicit and local rather than
/// tidied away, because each one is a documented contract quirk somewhere in
/// docs/port-contracts/chunk-6.json.
/// </summary>
internal static class SlotJs
{
    /// <summary>
    /// JavaScript truthiness. <c>0</c>, <c>""</c>, <c>false</c>, <c>null</c>, an absent key and
    /// <c>NaN</c> are falsy; <c>{}</c> and <c>[]</c> are TRUTHY, which is why an empty
    /// <c>schedule</c> array is a legal request and an object <c>slotDuration</c> is not treated
    /// as absent.
    /// </summary>
    public static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.True or JsonValueKind.Object or JsonValueKind.Array => true,
        JsonValueKind.String => value.GetString() is { Length: > 0 },

        // A literal too large to read back as a double is JavaScript's Infinity, which is truthy.
        JsonValueKind.Number => !value.TryGetDouble(out var number) || (number != 0 && !double.IsNaN(number)),
        _ => false
    };

    /// <summary><c>Number(value)</c> for a string operand.</summary>
    /// <remarks>
    /// <c>Number(undefined)</c> is NaN while <c>Number("")</c> and <c>Number("   ")</c> are 0,
    /// and whitespace around a numeral is ignored — so a null argument stands for the missing
    /// segment of a one-part time, not for an empty one.
    /// </remarks>
    public static double ToNumber(string? value)
    {
        if (value is null) return double.NaN;

        var trimmed = value.Trim();
        if (trimmed.Length == 0) return 0;

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : double.NaN;
    }

    /// <summary><c>String(value)</c> for a number: no trailing ".0", and NaN spelled out.</summary>
    public static string ToJsString(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>String(value)</c> for a JSON value, which is what both <c>parseInt</c> and string
    /// concatenation see. An object stringifies to the literal <c>[object Object]</c> and an
    /// array to its elements joined by commas with null rendered as empty — both of which then
    /// coerce to NaN in the generation loop and yield zero slots.
    /// </summary>
    public static string ToJsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.TryGetDouble(out var number) ? ToJsString(number) : value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "undefined",
        JsonValueKind.Object => "[object Object]",
        JsonValueKind.Array => string.Join(
            ",",
            value.EnumerateArray().Select(element =>
                element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? string.Empty
                    : ToJsString(element))),
        _ => string.Empty
    };

    /// <summary>
    /// <c>parseInt(value)</c> with no radix: <c>String(value)</c>, then leading whitespace, an
    /// optional sign and as many decimal digits as follow — so <c>"1abc"</c> is 1, <c>3.7</c> is
    /// 3 (its string form stops at the dot), and <c>"abc"</c>, <c>null</c> and <c>true</c> are
    /// all NaN.
    /// </summary>
    /// <returns>The parsed value, or NaN.</returns>
    public static double ParseInt(JsonElement value)
    {
        var text = ToJsString(value);
        var index = 0;

        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;

        var negative = false;
        if (index < text.Length && (text[index] == '+' || text[index] == '-'))
        {
            negative = text[index] == '-';
            index++;
        }

        var digitsStart = index;
        while (index < text.Length && text[index] is >= '0' and <= '9') index++;

        if (index == digitsStart) return double.NaN;

        var digits = double.Parse(
            text[digitsStart..index], NumberStyles.None, CultureInfo.InvariantCulture);

        return negative ? -digits : digits;
    }

    /// <summary>
    /// <see cref="ParseInt(JsonElement)"/> narrowed to the <c>day_of_week</c> Int column.
    /// </summary>
    /// <returns>
    /// Null when the result is NaN or too large for the column — both of which Node's Prisma
    /// layer rejects, taking the whole statement with it, so the caller must answer its route's
    /// own 500 rather than a partial success.
    /// </returns>
    public static int? ParseIntToInt32(JsonElement value)
    {
        var parsed = ParseInt(value);

        return double.IsNaN(parsed) || parsed < int.MinValue || parsed > int.MaxValue
            ? null
            : (int)parsed;
    }

    /// <summary>
    /// <c>slotDuration || fallback</c> — JS truthiness, NOT a null check, so a posted <c>0</c>
    /// becomes the fallback. The fallback itself differs per route: 30 for <c>/slots/bulk</c>,
    /// 15 for <c>/slots/sync-from-hours</c>.
    /// </summary>
    public static SlotDurationSpec ResolveDuration(JsonElement? raw, int fallback)
    {
        if (raw is not { } value || !IsTruthy(value))
            return new SlotDurationSpec(fallback, fallback, null);

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                var minutes = value.TryGetDouble(out var parsed) ? parsed : double.NaN;

                // A fraction or an out-of-Int32 value is added correctly and then refused by the
                // Int column, so it generates windows and fails the insert — Node's own 500.
                var stored = double.IsInteger(minutes) && minutes >= int.MinValue && minutes <= int.MaxValue
                    ? (int)minutes
                    : (int?)null;

                return new SlotDurationSpec(stored, minutes, null);

            case JsonValueKind.True:
                // `540 + true` is 541 — arithmetic, with true worth 1 — but the raw `true` is
                // what reaches the Int column, so any generated row fails the insert.
                return new SlotDurationSpec(null, 1, null);

            default:
                // A string, an object or an array all reach `currentMinutes + duration` as
                // CONCATENATION, and none of them can be stored in an Int column either.
                return new SlotDurationSpec(null, double.NaN, ToJsString(value));
        }
    }
}
