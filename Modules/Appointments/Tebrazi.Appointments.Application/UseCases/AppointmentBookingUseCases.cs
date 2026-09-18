using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
// The three BOOKING endpoints, ported from server/src/routes/appointments.js:
//
//   POST /api/appointments           book one appointment      (appointments.js:430-505)
//   POST /api/appointments/recurring create a series           (appointments.js:700-735)
//   POST /api/appointments/walk-in   reception registers an    (appointments.js:556-695)
//                                    unbooked arrival
//
// All three answer 201, and all three are WRITE handlers, so they take the caller's user id as an
// explicit command parameter rather than reading ICurrentUser — the house rule from the Visits
// module.
//
// Three cross-cutting facts about this group:
//
//  * TWO of them book for the CALLER. POST / and POST /recurring both write
//    `patientUserId = req.user.id` and the body has no patient field at all, so a physician or a
//    receptionist hitting either one creates an appointment with THEMSELVES as the patient. Only
//    /walk-in names the patient, and it does not validate the id it is given.
//
//  * `appointments` carries NO cross-module foreign key in this port (see
//    AppointmentConfigurations — clinics and subprofiles live in other modules), while Prisma
//    DOES enforce clinicId and subprofileId. Node relies on those FKs to reject a bad id, and the
//    router's catch-all turns the violation into a 500. Where that 500 is observable, these
//    handlers reproduce it with an explicit directory read placed at the point the INSERT would
//    have failed, so no row is persisted — see the comments at each site.
//
//  * The XSS sanitiser at server/src/index.js:56-81 has already rewritten every string in the
//    body before the Node handler runs (it deletes "javascript:", "data:" and "vbscript:"
//    ANYWHERE in a value, so a reason of "data: 5mg" is stored as " 5mg"). That is middleware, not
//    handler behaviour, and it is out of scope for this file — but it means stored `reason` text
//    will differ between the two backends until an equivalent filter exists in the pipeline.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The bare <c>{ "error": "..." }</c> bodies these routes return. Every error in appointments.js is
/// that single-key shape, so <c>ValidationException</c> (which emits
/// <c>{"error":"Validation failed","message":…}</c>) and <c>ConflictException</c> (which emits
/// <c>{"error":"Conflict","message":…}</c>) are both wrong here. Passing the same literal as both
/// error and message makes <c>ExceptionHandlingMiddleware</c> drop the redundant <c>message</c>
/// key and emit exactly <c>{"error": … }</c>.
/// </summary>
internal static class BookingErrors
{
    public static BusinessException Node(string error, int statusCode = 400)
        => new(error, error, statusCode);
}

/// <summary>
/// The route-level <c>try/catch</c> each of these three Node handlers is wrapped in end to end,
/// whose ONLY outcome is that route's own named 500: <c>"Failed to book appointment"</c>
/// (appointments.js:501-504), <c>"Failed to create recurring appointments"</c> (:731-734),
/// <c>"Failed to create walk-in"</c> (:690-693). The chunk-5 contract states it as "500 … on ANY
/// thrown error".
///
/// <para>The handlers reproduce the ANTICIPATED 500s themselves; this closes the rest. A
/// DbUpdateException from SaveChangesAsync, a connection fault on a directory read, an
/// OverflowException out of DateTime.Parse, or an ArgumentException out of
/// <c>Appointment.Create</c> would otherwise reach ExceptionHandlingMiddleware and answer
/// <c>{"error":"Internal Server Error","message":"An unexpected error occurred"}</c>. That body is
/// client-visible — AppointmentsPage.jsx:380 renders <c>err.response?.data?.error</c> straight
/// into the page — so the user would read "Internal Server Error" where Node says
/// "Failed to book appointment".</para>
/// </summary>
internal static class BookingGuard
{
    /// <inheritdoc cref="BookingGuard"/>
    /// <remarks>
    /// An <see cref="AppException"/> passes through untouched, so every modelled 400/403/404 and
    /// the handlers' own named 500s keep their bodies. A cancellation the caller triggered also
    /// passes through. The best-effort notification blocks inside the handlers keep their OWN
    /// inner catches, exactly as Node nests its own.
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
            throw BookingErrors.Node(failureError, 500);
        }
    }
}

/// <summary>
/// The JavaScript coercions these three handlers depend on. Node tests <c>falsiness</c>, so an
/// empty string is indistinguishable from a missing key everywhere in this file.
/// </summary>
internal static class BookingCoercions
{
    /// <summary><c>value || null</c> — "" collapses to null.</summary>
    public static string? OrNull(string? value) => value is { Length: > 0 } text ? text : null;

    /// <summary><c>value || fallback</c> for strings.</summary>
    public static string OrElse(string? value, string fallback)
        => value is { Length: > 0 } text ? text : fallback;

    /// <summary>
    /// <c>new Date("YYYY-MM-DD")</c>. V8 reads a bare date as UTC MIDNIGHT, so parsing it with
    /// local-time defaults would shift the stored instant by the server's offset. The same idiom
    /// the Visits module uses.
    ///
    /// <para>Known divergence: V8 reads a zone-LESS date-time
    /// (<c>"2026-03-10T09:00:00"</c>) as LOCAL while <see cref="DateTimeStyles.AssumeUniversal"/>
    /// reads it as UTC. The client only ever sends "YYYY-MM-DD" here, so the two agree on every
    /// real request.</para>
    /// </summary>
    /// <param name="value">The raw body string, "YYYY-MM-DD" in every request the client makes.</param>
    /// <param name="failureError">
    /// The route's own catch-all message. <c>new Date("garbage")</c> is an Invalid Date, Prisma
    /// rejects it and the router answers its 500 — letting the <c>FormatException</c> escape would
    /// emit the middleware's generic body instead.
    /// </param>
    public static DateTime ParseUtcDate(string value, string failureError)
    {
        try
        {
            return DateTime.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw BookingErrors.Node(failureError, 500);
        }
    }
}

// ── POST /api/appointments ───────────────────────────────────────────────────

/// <param name="ClinicId">
/// Required, and never validated: not checked to exist, not checked to belong to the physician,
/// and <c>clinic.allowPatientBooking</c> is not enforced.
/// </param>
/// <param name="PhysicianUserId">
/// Required. The doctor's <b>User</b> id, not the physician-profile id the row stores. The handler
/// resolves one to the other, and the notification is then addressed back to THIS value — two
/// different identifiers in one flow.
/// </param>
/// <param name="AppointmentDate">Required "YYYY-MM-DD", stored as the UTC-midnight instant.</param>
/// <param name="StartTime">Required "HH:MM". Unvalidated — non-time garbage is stored verbatim.</param>
/// <param name="EndTime">Required "HH:MM". Never used by the conflict check.</param>
/// <param name="Reason">Optional; "" is coerced to null, so an empty reason and no reason are indistinguishable.</param>
/// <param name="AppointmentType">
/// Optional, defaults to <c>IN_PERSON</c>, and NOT whitelisted by Node: a value outside the enum
/// reaches Prisma, which throws, and the client sees 500 rather than a 400.
/// </param>
/// <param name="SubprofileId">
/// Optional; "" is coerced to null. NEVER checked against the caller's own patient profile — any
/// known subprofile id is accepted and its owner's name and relation are then echoed back
/// (audit-2, authorization defect; reproduced, not fixed).
/// </param>
public sealed record BookingCreateAppointmentBody(
    string? ClinicId,
    string? PhysicianUserId,
    string? AppointmentDate,
    string? StartTime,
    string? EndTime,
    string? Reason,
    string? AppointmentType,
    string? SubprofileId);

public sealed record BookingCreateAppointmentCommand(
    string CallerUserId,
    BookingCreateAppointmentBody? Body) : IRequest<BookingCreatedResponse>;

/// <summary>
/// Port of <c>POST /api/appointments</c> (appointments.js:430-505). Books a PENDING appointment for
/// the CALLER against the physician named in the body, rejects an exact same-day same-start-time
/// collision, and then notifies (and emails) the physician best-effort. Answers <b>201</b>.
///
/// <para>There is NO authorization gate of any kind: no userType check, no clinic ownership check,
/// no staff check, and <c>clinic.allowPatientBooking</c> is NOT consulted — only
/// <c>GET /available</c> honours that flag, so a patient can book at a booking-disabled clinic by
/// posting straight here. <c>clinicId</c> is never validated against the physician either.</para>
///
/// <para>No slot validation whatsoever: any <c>startTime</c>/<c>endTime</c> string is accepted,
/// out-of-hours values and non-time garbage included.</para>
/// </summary>
public sealed class BookingCreateAppointmentHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    INotificationPublisher notifications,
    IAppLogger<BookingCreateAppointmentHandler> logger)
    : IRequestHandler<BookingCreateAppointmentCommand, BookingCreatedResponse>
{
    private const string FailureError = "Failed to book appointment";

    public Task<BookingCreatedResponse> Handle(
        BookingCreateAppointmentCommand request, CancellationToken cancellationToken = default)
        => BookingGuard.RunAsync(
            logger, "[Appointments] Book error", FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<BookingCreatedResponse> HandleCore(
        BookingCreateAppointmentCommand request, CancellationToken cancellationToken)
    {
        var body = request.Body;

        var clinicId = body?.ClinicId;
        var physicianUserId = body?.PhysicianUserId;
        var appointmentDate = body?.AppointmentDate;
        var startTime = body?.StartTime;
        var endTime = body?.EndTime;

        // One guard covering all five, so the client cannot tell WHICH field was missing. "" counts
        // as missing (appointments.js:434). Note the message: /recurring's near-identical guard says
        // something different and shorter.
        if (string.IsNullOrEmpty(clinicId)
            || string.IsNullOrEmpty(physicianUserId)
            || string.IsNullOrEmpty(appointmentDate)
            || string.IsNullOrEmpty(startTime)
            || string.IsNullOrEmpty(endTime))
        {
            throw BookingErrors.Node("All booking fields are required");
        }

        // 400, NOT 404 (appointments.js:442).
        var physician = await identity.GetPhysicianByUserIdAsync(physicianUserId, cancellationToken)
            ?? throw BookingErrors.Node("Physician not found");

        var date = BookingCoercions.ParseUtcDate(appointmentDate, FailureError);

        // appointments.js:450-458. EXACT startTime string equality on that physician's day, statuses
        // PENDING/CONFIRMED only, and NOT scoped to clinicId:
        //
        //   * 09:00-10:00 does NOT block a new 09:30-10:00 — endTime is ignored and there is no
        //     overlap logic at all. Turning this into an interval check would reject bookings Node
        //     accepts.
        //   * a booking at the physician's OTHER clinic DOES block this clinic's same start time,
        //     so the client can get a 409 for a slot GET /available just showed as free.
        //   * CANCELLED / COMPLETED / NO_SHOW rows do not hold the slot, so a cancelled booking is
        //     rebookable.
        //
        // It is a check-then-create with no transaction and no unique index, so two concurrent
        // requests both pass and both answer 201. Reproduced deliberately — no lock is taken.
        //
        // The RAW body value is compared, not the trimmed one, because that is the string Node puts
        // in the WHERE clause. (Appointment.Create then trims what it stores; a padded startTime is
        // therefore stored differently by the two backends. Owned by the entity, recorded in the
        // port report.)
        if (await appointments.HasBookingAtAsync(physician.Id, date, startTime, cancellationToken))
            throw BookingErrors.Node("This slot is already booked", 409);

        // `appointmentType || 'IN_PERSON'` with no whitelist (appointments.js:487). An unknown value
        // makes Prisma throw and surfaces as this route's 500, never a 400 — so the parse is a
        // literal match on the four enum names. Enum.TryParse cannot be used: it would silently
        // accept the NUMERIC string "3" as WALK_IN, which Prisma rejects.
        var appointmentType = body?.AppointmentType switch
        {
            null or "" or "IN_PERSON" => AppointmentType.IN_PERSON,
            "VIDEO" => AppointmentType.VIDEO,
            "PHONE" => AppointmentType.PHONE,
            "WALK_IN" => AppointmentType.WALK_IN,
            _ => throw BookingErrors.Node(FailureError, 500)
        };

        var subprofileId = BookingCoercions.OrNull(body?.SubprofileId);

        // Stands in for the Prisma FK on appointments.clinic_id. Placed AFTER the 409 so a bad
        // clinic on an already-taken slot still answers 409, exactly as Node's ordering does, and
        // BEFORE the insert so nothing is persisted — a FK violation rolls the whole create back.
        // The name is needed for the response's `clinic` object regardless.
        var clinic = await clinics.GetClinicAsync(clinicId, cancellationToken)
            ?? throw BookingErrors.Node(FailureError, 500);

        // Same reasoning for appointments.subprofile_id, which carries an FK (onDelete: SetNull) in
        // Prisma: a nonexistent id is a 500, not a 400 (audit-2). The row is also what the response
        // and the notification text need.
        SubprofileSummary? subprofile = null;
        if (subprofileId is not null)
        {
            subprofile = await patients.GetSubprofileAsync(subprofileId, cancellationToken)
                ?? throw BookingErrors.Node(FailureError, 500);
        }

        var reason = BookingCoercions.OrNull(body?.Reason);

        var appointment = Appointment.Create(
            clinicId: clinicId,
            physicianId: physician.Id,
            patientUserId: request.CallerUserId,
            appointmentDate: date,
            startTime: startTime,
            endTime: endTime,
            subprofileId: subprofileId,
            // clinicPatientId is never written by this route, and `notes` is never read from the
            // body — both stay null.
            reason: reason,
            appointmentType: appointmentType);

        appointments.Add(appointment);
        await dbContext.SaveChangesAsync(cancellationToken);

        await NotifyPhysicianAsync(
            appointment, physicianUserId, request.CallerUserId, appointmentDate, startTime, reason,
            subprofile, cancellationToken);

        return BookingMapper.ToCreatedResponse(
            appointment, clinic, physician.DisplayName, subprofile);
    }

    /// <summary>
    /// appointments.js:481-500. Notifies the physician and sends a transactional EMAIL, which Node
    /// awaits inside the request.
    ///
    /// <para>The whole block sits in its own <c>try/catch</c> that only logs, so a dead mail server
    /// or an unresolvable patient still answers 201 and the body says nothing about it. The catch is
    /// kept here — not for <c>INotificationPublisher</c>, which never throws by contract, but for
    /// the caller lookup Node wraps in the same block.</para>
    /// </summary>
    private async Task NotifyPhysicianAsync(
        Appointment appointment,
        string physicianUserId,
        string callerUserId,
        string rawAppointmentDate,
        string rawStartTime,
        string? reason,
        SubprofileSummary? subprofile,
        CancellationToken cancellationToken)
    {
        try
        {
            var patient = await identity.GetUserAsync(callerUserId, cancellationToken);
            var patientName = BookingCoercions.OrElse(patient?.DisplayName, "A patient");

            // toLocaleDateString('en-US', { weekday:'short', month:'short', day:'numeric' }) renders
            // in the SERVER's timezone from a UTC-midnight instant, so it can name the PREVIOUS day
            // on a negative-offset host. The text is stored in the notification row, so the quirk is
            // reproduced rather than corrected.
            var dateStr = BookingCoercions
                .ParseUtcDate(rawAppointmentDate, FailureError)
                .ToLocalTime()
                .ToString("ddd, MMM d", CultureInfo.GetCultureInfo("en-US"));

            var forMember = subprofile is null ? string.Empty : $" (for {subprofile.Name})";

            // ' — ' carries an EM DASH (U+2014).
            var reasonSuffix = reason is null ? string.Empty : $" — {reason}";

            await notifications.PublishAsync(
                new NotificationRequest(
                    // Addressed to the body's physicianUserId (a USER id) while the appointment
                    // stores the physician PROFILE id.
                    UserId: physicianUserId,
                    // Not a member of notificationService.NOTIFICATION_TYPES (that list has
                    // 'APPOINTMENT_BOOKED'); the column is free text, so it persists unvalidated and
                    // any client switch on the documented list misses it.
                    Type: "NEW_APPOINTMENT_REQUEST",
                    Title: "New Appointment Request",
                    Message:
                        $"{patientName}{forMember} booked an appointment on {dateStr} "
                        + $"at {rawStartTime}{reasonSuffix}.",
                    Data: new JsonObject
                    {
                        ["appointmentId"] = appointment.Id,
                        ["patientUserId"] = callerUserId
                    }.ToJsonString(),
                    SendEmail: true),
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // appointments.js:500 — `catch (e) { logger.error(...) }`. The booking is already
            // committed and the 201 must stand.
        }
    }
}

// ── POST /api/appointments/recurring ─────────────────────────────────────────

/// <param name="ClinicId">Required, and never validated against the physician or for existence.</param>
/// <param name="PhysicianUserId">Required. A <b>User</b> id, resolved to a physician-profile id.</param>
/// <param name="StartDate">Required "YYYY-MM-DD" — occurrence 0's date.</param>
/// <param name="StartTime">Required "HH:MM", identical on every occurrence.</param>
/// <param name="EndTime">Required "HH:MM", identical on every occurrence.</param>
/// <param name="Reason">Optional; "" is coerced to null. Copied to every occurrence.</param>
/// <param name="Rule">
/// Required, and stored VERBATIM. Only "WEEKLY" and "BIWEEKLY" are recognised for the spacing;
/// "MONTHLY", "DAILY", lowercase spellings and outright typos all silently become 30-day
/// intervals while <c>recurringRule</c> keeps the unrecognised string — so the persisted rule and
/// the actual spacing disagree.
/// </param>
/// <param name="Count">
/// Optional, clamped by <c>Math.min(count || 4, 12)</c>. Typed as <c>JsonElement?</c> because the
/// JavaScript coercions are observable: <c>0</c> is falsy and becomes 4, a negative value makes the
/// loop body never run, and a non-numeric value yields <c>NaN</c> which also runs nothing — each of
/// those answering 201 with a different <c>count</c>. A plain <c>int?</c> cannot express them.
/// </param>
public sealed record BookingCreateRecurringBody(
    string? ClinicId,
    string? PhysicianUserId,
    string? StartDate,
    string? StartTime,
    string? EndTime,
    string? Reason,
    string? Rule,
    JsonElement? Count);

public sealed record BookingCreateRecurringCommand(
    string CallerUserId,
    BookingCreateRecurringBody? Body) : IRequest<BookingRecurringResponse>;

/// <summary>
/// Port of <c>POST /api/appointments/recurring</c> (appointments.js:700-735). Creates up to 12
/// PENDING appointments spaced by a fixed day interval, all sharing one generated
/// <c>recurringGroupId</c> and all with <c>patientUserId</c> = the CALLER. Answers <b>201</b> with
/// <c>{ count, groupId, appointments }</c>.
///
/// <para>NO authorization gate of any kind, and — unlike <c>POST /</c> — <b>no conflict check</b>:
/// this route never returns 409, so a series can be stacked straight on top of existing
/// PENDING/CONFIRMED bookings, on other patients' bookings, and on itself (audit-2).</para>
///
/// <para><b>No notification of any kind.</b> The physician is never told a 12-appointment series
/// was written onto their calendar. It is the only create route in this router that notifies
/// nobody, and that asymmetry is the contract.</para>
///
/// <para><b>Not transactional, deliberately.</b> Node awaits the creates one at a time in a plain
/// <c>for</c> loop, so a failure on occurrence <i>k</i> answers 500 while leaving occurrences
/// 0..<i>k-1</i> COMMITTED under a groupId the client never learns. Each occurrence is therefore
/// saved on its own; a single <c>AddRange</c> plus one save — or an
/// <c>ExecuteInTransactionAsync</c> — would leave zero rows and change observable behaviour.</para>
/// </summary>
public sealed class BookingCreateRecurringHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<BookingCreateRecurringHandler> logger)
    : IRequestHandler<BookingCreateRecurringCommand, BookingRecurringResponse>
{
    private const string FailureError = "Failed to create recurring appointments";

    /// <summary><c>count || 4</c> — the value a falsy <c>count</c> falls back to.</summary>
    private const double DefaultOccurrences = 4;

    /// <summary><c>Math.min(…, 12)</c> — the high-side clamp. There is no low-side clamp.</summary>
    private const double MaxOccurrences = 12;

    public Task<BookingRecurringResponse> Handle(
        BookingCreateRecurringCommand request, CancellationToken cancellationToken = default)
        => BookingGuard.RunAsync(
            logger, "[Appointments] Recurring error", FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<BookingRecurringResponse> HandleCore(
        BookingCreateRecurringCommand request, CancellationToken cancellationToken)
    {
        var body = request.Body;

        var clinicId = body?.ClinicId;
        var physicianUserId = body?.PhysicianUserId;
        var startDate = body?.StartDate;
        var startTime = body?.StartTime;
        var endTime = body?.EndTime;
        var rule = body?.Rule;

        // Six fields, and `count` is NOT one of them. The message is the bare 'All fields required'
        // (appointments.js:706) — shorter and different from POST /'s 'All booking fields are
        // required'. Two near-identical validations, two strings; both are matched by the client.
        if (string.IsNullOrEmpty(clinicId)
            || string.IsNullOrEmpty(physicianUserId)
            || string.IsNullOrEmpty(startDate)
            || string.IsNullOrEmpty(startTime)
            || string.IsNullOrEmpty(endTime)
            || string.IsNullOrEmpty(rule))
        {
            throw BookingErrors.Node("All fields required");
        }

        // 400, not 404 — same as POST /.
        var physician = await identity.GetPhysicianByUserIdAsync(physicianUserId, cancellationToken)
            ?? throw BookingErrors.Node("Physician not found");

        var occurrences = ResolveOccurrences(body?.Count);

        // require('crypto').randomUUID() — generated even when nothing is created, so a 201 can
        // carry a groupId that belongs to no row at all.
        var groupId = Guid.NewGuid().ToString();

        // appointments.js:714. Anything that is not exactly 'WEEKLY' or 'BIWEEKLY' — including
        // 'MONTHLY' — falls through to 30 CALENDAR DAYS, not a calendar month, so a Jan 31 start
        // lands on Mar 2, Apr 1, May 1 and the day-of-month drifts. Case-sensitive.
        var intervalDays = rule switch
        {
            "WEEKLY" => 7,
            "BIWEEKLY" => 14,
            _ => 30
        };

        var reason = BookingCoercions.OrNull(body?.Reason);

        // Stands in for the Prisma FK on appointments.clinic_id, which is what makes a bad clinicId
        // a 500 here (audit-2) — Node has no clinic read on this route at all. Gated on the loop
        // actually running, because with zero occurrences Node never touches the database and
        // answers 201 even for a nonsense clinicId.
        if (occurrences > 0 && await clinics.GetClinicAsync(clinicId, cancellationToken) is null)
            throw BookingErrors.Node(FailureError, 500);

        var created = new List<BookingRecurringAppointmentResponse>(occurrences);

        for (var i = 0; i < occurrences; i++)
        {
            // Re-parsed from the original string on every iteration (appointments.js:716), so there
            // is no compounding drift — and so that a nonsense startDate is only ever reached INSIDE
            // the loop. With zero occurrences it is never parsed, which is why a 201 with an empty
            // array can carry an unparseable date the handler never looked at.
            //
            // KNOWN DIVERGENCE (audit-2, accepted): Node advances the date with
            // `date.setDate(date.getDate() + i * intervalDays)`, i.e. LOCAL date components applied
            // to a UTC-midnight instant. On a negative-offset host getDate() reads the previous day,
            // and a DST transition inside the series shifts later occurrences' stored UTC
            // time-of-day by an hour. UTC arithmetic here keeps every occurrence at exactly
            // start + k*interval, which matches Node only on a UTC (or DST-free positive-offset)
            // server. Recorded in docs/PORT-STATUS.md rather than emulated.
            var date = BookingCoercions
                .ParseUtcDate(startDate, FailureError)
                .AddDays(i * intervalDays);

            var appointment = Appointment.Create(
                clinicId: clinicId,
                physicianId: physician.Id,
                patientUserId: request.CallerUserId,
                appointmentDate: date,
                startTime: startTime,
                endTime: endTime,
                // subprofileId, notes and appointmentType are never read from the body on this
                // route: a series cannot be booked for a family member and is always IN_PERSON.
                reason: reason,
                recurringRule: rule,
                recurringGroupId: groupId);

            appointments.Add(appointment);

            // One save PER OCCURRENCE. See the class remarks: the orphan rows a mid-series failure
            // leaves behind are the contract.
            await dbContext.SaveChangesAsync(cancellationToken);

            created.Add(BookingMapper.ToRecurringRow(appointment));
        }

        // `count` is created.length — the ACTUAL insert count, not the clamp result — and `groupId`
        // is the same value each row reports as `recurringGroupId`.
        return new BookingRecurringResponse(created.Count, groupId, created);
    }

    /// <summary>
    /// <c>Math.min(count || 4, 12)</c> followed by <c>for (let i = 0; i &lt; occurrences; i++)</c>,
    /// reduced to the number of iterations that loop performs.
    ///
    /// <list type="bullet">
    /// <item>absent, null, false, 0 or "" — falsy, so the default 4;</item>
    /// <item>100 → clamped to 12; 4.5 → 5 iterations, because the guard is <c>i &lt; 4.5</c>;</item>
    /// <item>-3 → <c>Math.min(-3, 12)</c> is -3, the body never runs, and the 201 carries
    /// <c>{ count: 0, groupId, appointments: [] }</c>;</item>
    /// <item>a non-numeric value → <c>NaN</c>, <c>0 &lt; NaN</c> is false, so also zero
    /// iterations.</item>
    /// </list>
    /// </summary>
    private static int ResolveOccurrences(JsonElement? count)
    {
        var requested = count switch
        {
            // Falsy: `count || 4` takes the default. An absent key, an explicit null, `false`, `0`
            // and "" all land here.
            null => DefaultOccurrences,
            { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False }
                => DefaultOccurrences,
            { ValueKind: JsonValueKind.Number } number
                => number.TryGetDouble(out var value) && value != 0 ? value : DefaultOccurrences,
            // Number(true) is 1, so `count: true` creates a single occurrence.
            { ValueKind: JsonValueKind.True } => 1d,
            { ValueKind: JsonValueKind.String } text => FromString(text.GetString()),
            // An object or an array is truthy, and Math.min then coerces it to NaN.
            _ => double.NaN
        };

        if (double.IsNaN(requested)) return 0;

        var occurrences = Math.Min(requested, MaxOccurrences);

        // The loop runs for every integer i in [0, occurrences), which is Ceiling for a positive
        // value and nothing at all for a negative one.
        return occurrences <= 0 ? 0 : (int)Math.Ceiling(occurrences);

        // `count || 4` applies its falsy test to the RAW body value, and the ONLY falsy string is
        // "". Every other string — including "0" and " " — is TRUTHY, so it survives the `||`
        // and Math.min then coerces it with Number(): Number("0") and Number(" ") are both 0,
        // Number("  5  ") is 5, and anything non-numeric is NaN.
        //
        // The falsy test must therefore NOT be re-applied to the coerced number. Doing so turns
        // `count: "0"` and `count: " "` into FOUR committed appointment rows and a 201 reporting
        // `count: 4`, where Node runs `for (let i = 0; i < 0; i++)`, commits nothing, and answers
        // 201 with `{count: 0, groupId, appointments: []}`.
        static double FromString(string? text)
        {
            // "" — the one falsy string, so the `|| 4` default applies before any coercion.
            if (string.IsNullOrEmpty(text)) return DefaultOccurrences;

            // Truthy but numerically zero: Number(" ") is 0, which means ZERO occurrences.
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return 0d;

            return double.TryParse(
                trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : double.NaN;
        }
    }
}

// ── POST /api/appointments/walk-in ───────────────────────────────────────────

/// <param name="ClinicId">
/// Required, and read from the BODY only — this route ignores <c>X-Clinic-Id</c>.
/// </param>
/// <param name="PatientUserId">
/// Required. <c>appointments.patient_user_id</c> has NO relation and NO foreign key in the Prisma
/// schema, and this route does not validate it either — any arbitrary string is accepted and
/// stored, and the appointment then renders with a null patient everywhere downstream.
/// </param>
/// <param name="Reason">Optional; defaults to the literal "Walk-in", so it is never null.</param>
/// <param name="AppointmentDate">
/// Optional "YYYY-MM-DD". Parsed as <c>new Date(value + 'T12:00:00')</c> — server-LOCAL noon —
/// where <c>POST /</c> parses the identical input as UTC midnight. The two routes persist different
/// instants for the same date string, and both behaviours must stay distinct. Omitted, it becomes
/// <c>now</c>: a full timestamp with a real time-of-day.
/// </param>
/// <param name="StartTime">Optional "HH:MM"; defaults to the server-LOCAL clock time of now.</param>
/// <param name="EndTime">
/// Optional "HH:MM"; defaults to the resolved start, producing a zero-length appointment.
/// </param>
/// <param name="SubprofileId">Optional; the client's empty form field "" is coerced to null.</param>
public sealed record BookingCreateWalkInBody(
    string? ClinicId,
    string? PatientUserId,
    string? Reason,
    string? AppointmentDate,
    string? StartTime,
    string? EndTime,
    string? SubprofileId);

public sealed record BookingCreateWalkInCommand(
    string CallerUserId,
    BookingCreateWalkInBody? Body) : IRequest<BookingWalkInResponse>;

/// <summary>
/// Port of <c>POST /api/appointments/walk-in</c> (appointments.js:556-695). Creates an immediately
/// CONFIRMED <c>WALK_IN</c> appointment for a NAMED patient, then runs two independent best-effort
/// blocks: notify the physician, and push the patient onto today's waiting queue. Answers
/// <b>201</b> with the row plus <c>clinic { name }</c> and nothing else.
///
/// <para>The physician branch takes precedence and performs NO ownership check: if the caller has
/// any physician profile, the appointment is attributed to THEIR OWN profile whatever
/// <c>clinicId</c> was passed — a physician can create a walk-in in another physician's clinic.
/// Only on the staff branch is the clinic read, so <c>404 "Clinic not found"</c> is UNREACHABLE for
/// a physician caller: the same bad input answers 403, 404 or 500 depending on WHO calls.</para>
///
/// <para>No duplicate or slot-conflict check at all, unlike <c>POST /</c>'s 409 — the same patient
/// can be walked in repeatedly at the same minute and each call creates another appointment.</para>
///
/// <para>This route ignores <c>X-Clinic-Id</c> entirely; <c>clinicId</c> must be in the body.</para>
/// </summary>
public sealed class BookingCreateWalkInHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    INotificationPublisher notifications,
    IWaitingQueueWriter waitingQueue,
    IAppLogger<BookingCreateWalkInHandler> logger)
    : IRequestHandler<BookingCreateWalkInCommand, BookingWalkInResponse>
{
    private const string FailureError = "Failed to create walk-in";

    public Task<BookingWalkInResponse> Handle(
        BookingCreateWalkInCommand request, CancellationToken cancellationToken = default)
        => BookingGuard.RunAsync(
            logger, "[Appointments] Walk-in error", FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<BookingWalkInResponse> HandleCore(
        BookingCreateWalkInCommand request, CancellationToken cancellationToken)
    {
        var body = request.Body;

        var clinicId = body?.ClinicId;
        var patientUserId = body?.PatientUserId;

        if (string.IsNullOrEmpty(clinicId) || string.IsNullOrEmpty(patientUserId))
            throw BookingErrors.Node("clinicId and patientUserId are required");

        // ── Resolve the physician (appointments.js:566-581) ──
        var callerPhysician = await identity.GetPhysicianByUserIdAsync(
            request.CallerUserId, cancellationToken);

        string physicianId;
        ClinicSummary? clinic = null;

        if (callerPhysician is not null)
        {
            // Unconditional acceptance — no clinic ownership check whatsoever.
            physicianId = callerPhysician.Id;
        }
        else
        {
            // ClinicStaff.findFirst({ userId, clinicId, isActive: true }). Neither the staff role
            // nor the permissions blob is ever consulted.
            if (!await clinics.IsActiveStaffAsync(request.CallerUserId, clinicId, cancellationToken))
                throw new ForbiddenException("Not authorized");

            clinic = await clinics.GetClinicAsync(clinicId, cancellationToken)
                ?? throw new NotFoundException("Clinic not found");

            physicianId = clinic.PhysicianId;
        }

        // ── Resolve date and times (appointments.js:583-588) ──
        //
        // `new Date()` once, used both as the stored instant and — through its LOCAL components —
        // as the default HH:mm.
        var nowLocal = DateTime.Now;

        var resolvedDate = ParseLocalNoonOrNow(body?.AppointmentDate, nowLocal);

        // String(now.getHours()).padStart(2,'0') + ':' + minutes, in SERVER-LOCAL time.
        var resolvedStart = BookingCoercions.OrElse(
            body?.StartTime, nowLocal.ToString("HH:mm", CultureInfo.InvariantCulture));

        // `endTime || resolvedStart` — omitting endTime produces a ZERO-LENGTH appointment.
        var resolvedEnd = BookingCoercions.OrElse(body?.EndTime, resolvedStart);

        // The client posts the whole walk-in form including subprofileId: '' , so this coercion is
        // load-bearing (client/src/pages/AppointmentsPage.jsx:436).
        var subprofileId = BookingCoercions.OrNull(body?.SubprofileId);

        // Prisma's FK on appointments.subprofile_id is what makes a nonexistent id a 500 rather
        // than a 400, and the dependant's NAME is also what the queue row needs, so the row is read
        // once here instead of twice later.
        SubprofileSummary? subprofile = null;
        if (subprofileId is not null)
        {
            subprofile = await patients.GetSubprofileAsync(subprofileId, cancellationToken)
                ?? throw BookingErrors.Node(FailureError, 500);
        }

        // Stands in for the FK on appointments.clinic_id — a bad clinicId on the PHYSICIAN branch is
        // a 500 and never the 404 the staff branch produces. Already loaded on the staff branch,
        // where it answered that 404, so the read happens at most once.
        var resolvedClinic = clinic
            ?? await clinics.GetClinicAsync(clinicId, cancellationToken)
            ?? throw BookingErrors.Node(FailureError, 500);

        // `reason || 'Walk-in'` — never null on this route, so client code testing `reason == null`
        // will never see it.
        var reason = BookingCoercions.OrElse(body?.Reason, "Walk-in");

        var appointment = Appointment.Create(
            clinicId: clinicId,
            physicianId: physicianId,
            patientUserId: patientUserId,
            appointmentDate: resolvedDate,
            startTime: resolvedStart,
            endTime: resolvedEnd,
            subprofileId: subprofileId,
            reason: reason,
            // The literal 'WALK_IN' (appointments.js:597). GET /queue detects a check-in by
            // notes.includes('CHECKIN:'), so a walk-in reads as NOT checked in even though it was
            // auto-queued.
            notes: "WALK_IN",
            appointmentType: AppointmentType.WALK_IN,
            status: AppointmentStatus.CONFIRMED);

        // Walk-ins skip PENDING entirely, so there is no /confirm step for them. Confirm() is what
        // stamps confirmedAt = now; Create alone would leave it null.
        appointment.Confirm();

        appointments.Add(appointment);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Both side-effect blocks below commit through OTHER modules' units of work, so they run
        // after our save and outside any transaction of ours — the Node ordering.
        var physicianProfile = await NotifyPhysicianAsync(
            appointment, physicianId, patientUserId, request.CallerUserId, resolvedClinic,
            cancellationToken);

        await EnqueueAsync(
            appointment, physicianProfile, patientUserId, request.CallerUserId, clinicId, reason,
            subprofile, cancellationToken);

        return BookingMapper.ToWalkInResponse(appointment, resolvedClinic);
    }

    /// <summary>
    /// <c>new Date(appointmentDate + 'T12:00:00')</c> — server-LOCAL noon — or <c>now</c> when the
    /// body omitted the date.
    ///
    /// <para>When the date is omitted the stored value is a FULL timestamp with a real time-of-day,
    /// not a normalised midnight or noon, so walk-in rows are inconsistent with every other
    /// appointment row's date normalisation. And supplying <c>appointmentDate</c> without
    /// <c>startTime</c> takes the DATE from the body and the TIME from the server clock with no
    /// consistency check, so a walk-in dated next Tuesday gets today's clock time.</para>
    /// </summary>
    private static DateTime ParseLocalNoonOrNow(string? appointmentDate, DateTime nowLocal)
    {
        if (appointmentDate is not { Length: > 0 }) return nowLocal.ToUniversalTime();

        try
        {
            var localNoon = DateTime.Parse(
                $"{appointmentDate}T12:00:00", CultureInfo.InvariantCulture, DateTimeStyles.None);

            return DateTime.SpecifyKind(localNoon, DateTimeKind.Local).ToUniversalTime();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            // A client sending a full ISO timestamp instead of 'YYYY-MM-DD' makes the concatenation
            // unparseable → Invalid Date → Prisma throws → this route's 500, not a 400
            // (appointments.js:585, audit-2).
            throw BookingErrors.Node(FailureError, 500);
        }
    }

    /// <summary>
    /// appointments.js:601-621. Notifies the physician ONLY when someone other than the physician
    /// added the walk-in, and never sends an email (<c>sendEmail</c> is not passed).
    /// </summary>
    /// <returns>
    /// The physician's profile, so the queue block can reuse it. Node re-reads it there; the value
    /// is identical, and null is handled the same way on both paths.
    /// </returns>
    private async Task<PhysicianSummary?> NotifyPhysicianAsync(
        Appointment appointment,
        string physicianId,
        string patientUserId,
        string callerUserId,
        ClinicSummary clinic,
        CancellationToken cancellationToken)
    {
        try
        {
            // The stored PhysicianId is a profile id; the notification needs the USER id.
            var physicianProfile = await identity.GetPhysicianAsync(physicianId, cancellationToken);

            // The ACCOUNT holder's display name, never the dependant's — unlike the queue row
            // below, which does substitute the dependant.
            var patient = await identity.GetUserAsync(patientUserId, cancellationToken);

            if (physicianProfile is not null && physicianProfile.UserId != callerUserId)
            {
                var patientName = BookingCoercions.OrElse(patient?.DisplayName, "A patient");
                var clinicName = BookingCoercions.OrElse(clinic.Name, "your clinic");

                await notifications.PublishAsync(
                    new NotificationRequest(
                        UserId: physicianProfile.UserId,
                        Type: "PATIENT_ARRIVED",
                        Title: "Walk-in Patient",
                        Message: $"{patientName} has been added as a walk-in at {clinicName}",
                        Data: new JsonObject
                        {
                            ["appointmentId"] = appointment.Id,
                            ["patientUserId"] = patientUserId,
                            ["clinicId"] = appointment.ClinicId
                        }.ToJsonString()),
                    cancellationToken);
            }

            return physicianProfile;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // appointments.js:621 — `catch (e) { logger.warn(...) }`. Suppressed entirely when the
            // physician adds their own walk-in, and silent in the response either way.
            return null;
        }
    }

    /// <summary>
    /// The auto-check-in at appointments.js:624-687, behind
    /// <see cref="IWaitingQueueWriter"/> — the WaitingRoom module does not exist yet, and its
    /// current registration is a documented NO-OP, so <b>no waiting-room row is written by this
    /// port</b>. Nothing in the 201 body reports the queue, so the response stays byte-valid; what
    /// is lost is the physician's waiting-room screen, which will not see the patient.
    ///
    /// <para>The "already queued today?" test and the <c>queueNumber</c> assignment live behind the
    /// port on purpose: Node runs them as three unsynchronised queries and the race belongs to
    /// whoever implements the table.</para>
    /// </summary>
    private async Task EnqueueAsync(
        Appointment appointment,
        PhysicianSummary? physicianProfile,
        string patientUserId,
        string callerUserId,
        string clinicId,
        string reason,
        SubprofileSummary? subprofile,
        CancellationToken cancellationToken)
    {
        try
        {
            var patient = await identity.GetUserAsync(patientUserId, cancellationToken);

            // The DEPENDANT's name wins here, then the account display name, then the literal
            // 'Patient' (appointments.js:658-667). Compare the notification above, which never
            // substitutes the dependant. `subprofileName` is populated only when the dependant row
            // actually resolved.
            var queuePatientName = subprofile?.Name is { Length: > 0 } dependantName
                ? dependantName
                : BookingCoercions.OrElse(patient?.DisplayName, "Patient");

            await waitingQueue.EnqueueIfAbsentAsync(
                new WaitingQueueEnrolment(
                    ClinicId: clinicId,
                    // `physicianProfile?.userId || userId` — a receptionist's own id can end up in
                    // the physician column when the profile cannot be read.
                    PhysicianUserId: physicianProfile?.UserId is { Length: > 0 } physicianUserId
                        ? physicianUserId
                        : callerUserId,
                    PatientUserId: patientUserId,
                    PatientName: queuePatientName,
                    AppointmentId: appointment.Id,
                    // TODAY's local midnight, NOT the appointment's resolved date
                    // (appointments.js:627), so a walk-in dated in the future is still queued for
                    // today and the two dates disagree.
                    QueueDate: DateTime.Today,
                    // `subprofileId || null` — the id the appointment stored, written to the queue
                    // row even when the dependant row itself could not be read. Only
                    // `subprofileName` depends on that read succeeding.
                    SubprofileId: appointment.SubprofileId,
                    SubprofileName: subprofile?.Name,
                    Reason: reason,
                    // 'APPOINTMENT', not 'WALK_IN' — contradicting both the route name and the
                    // column's own schema default (appointments.js:679).
                    Source: "APPOINTMENT"),
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // appointments.js:687 — `catch (e) { logger.warn(...) }`. The port never throws by
            // contract; the catch stays because the display-name lookup Node wraps in the same
            // block can, and a failed check-in must still answer 201.
        }
    }
}
