using System.Globalization;
using System.Text.Encodings.Web;
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

// ═════════════════════════════════════════════════════════════════════════════
//  The ASSISTANT endpoints — reception's two writes, ported from
//  server/src/routes/appointments.js:958-1203.
//
//  PUT  /api/appointments/{id}/check-in      the patient has arrived
//  POST /api/appointments/{id}/intake-note   reception's pre-visit note for the physician
//
//  These are the only two Appointments routes a NON-physician is allowed to write through: both
//  admit any ACTIVE ClinicStaff row at the appointment's clinic, and both use a 403 message of
//  their own rather than the bare "Not authorized" the status transitions share.
//
//  They are also the two most side-effect-heavy routes in the file. /check-in performs three
//  writes into tables Appointments does not own — notifications, waiting_queues and payments —
//  and /intake-note's entire product is a row in patient_notes. Each of those goes through a
//  published kernel port. Three of the four ports are best-effort by contract and never throw;
//  IPatientNoteWriter is not, because the note IS the response body.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The bare <c>{ "error": "..." }</c> bodies these two routes return at every status code.
/// <c>ValidationException</c> would emit <c>{"error":"Validation failed","message":...}</c> and
/// <c>ConflictException</c> a <c>"Conflict"</c> label, neither of which the client matches on;
/// passing the same literal as both error and message makes
/// <c>ExceptionHandlingMiddleware</c> drop the redundant <c>message</c> key.
/// </summary>
internal static class AssistantErrors
{
    public static BusinessException Node(string error, int statusCode = 400) => new(error, error, statusCode);
}

/// <summary>
/// The route-level <c>try/catch</c> both Node handlers are wrapped in, whose ONLY outcome is that
/// route's own named 500: <c>"Failed to check in patient"</c> (appointments.js:1116) and
/// <c>"Failed to save intake note"</c> (:1201). Anything unmodelled would otherwise escape to
/// ExceptionHandlingMiddleware and render the generic
/// <c>{"error":"Internal Server Error","message":"An unexpected error occurred"}</c>, which the
/// client shows verbatim.
///
/// <para>This is the OUTER guard only. It sits outside the best-effort side-effect blocks, which
/// keep their own catches and are what actually holds these two routes to chunk-6's rule that the
/// notification, waiting-queue and payment blocks "CANNOT produce a 500" — the outer guard would
/// turn such a failure into one, so it must never be the only protection.</para>
/// </summary>
internal static class AssistantGuard
{
    /// <inheritdoc cref="AssistantGuard"/>
    /// <remarks>
    /// An <see cref="AppException"/> passes through untouched, so the 400s, the 404, the 403 and
    /// the handlers' own named 500s keep their bodies. A cancellation the caller triggered also
    /// passes through.
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
            throw AssistantErrors.Node(failureError, 500);
        }
    }
}

/// <summary>
/// The JavaScript semantics these two handlers depend on, applied to bodies bound as
/// <see cref="JsonElement"/> so that "key absent", "key present but null" and "key present with a
/// non-string value" stay distinguishable — all three are separate observable outcomes here.
/// </summary>
internal static class AssistantJs
{
    /// <summary>
    /// <c>JSON.stringify</c> parity. <c>System.Text.Json</c>'s default encoder escapes every
    /// non-ASCII character plus <c>&amp; + &lt; &gt; ' "</c> as <c>\uXXXX</c>; JSON.stringify
    /// escapes only control characters, <c>"</c> and <c>\</c>. The difference is visible in the
    /// <c>CHECKIN:</c> blob whenever a room label is not plain ASCII ("عيادة 3"), and that blob is
    /// stored, re-parsed by <c>GET /api/appointments/queue</c> and echoed to the client.
    /// </summary>
    private static readonly JsonSerializerOptions StringifyOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Compact, JSON.stringify-equivalent text for a node built by these handlers.</summary>
    public static string Stringify(JsonObject value) => value.ToJsonString(StringifyOptions);

    /// <summary>
    /// <c>new Date().toISOString()</c>: UTC, always exactly three fractional digits, always a
    /// trailing <c>Z</c>. The default <c>DateTime</c> round-trip format writes up to seven digits
    /// and drops trailing zeroes, which would change the bytes <c>GET /queue</c> hands back as
    /// <c>checkedInAt</c>.
    /// </summary>
    public static string ToIsoString(DateTime utc)
        => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// JavaScript truthiness. <c>undefined</c>, <c>null</c>, <c>false</c>, <c>0</c> and <c>""</c>
    /// are falsy; every object and array is truthy even when empty. Node tests
    /// <c>room ? … : …</c> and <c>room || null</c>, so <c>room: 0</c> and <c>room: ""</c> behave
    /// exactly like a missing room.
    /// </summary>
    public static bool IsTruthy(JsonElement? value)
    {
        if (value is not { } element) return false;

        return element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.String => element.GetString() is { Length: > 0 },
            JsonValueKind.Number => element.TryGetDouble(out var number) && number != 0 && !double.IsNaN(number),
            _ => true,
        };
    }

    /// <summary>
    /// The raw value, re-hosted as a <see cref="JsonNode"/> so it can be written into a JSON
    /// object with its original type intact — a numeric <c>room</c> stays a number, a string
    /// stays a string. Node smuggles the client's value through unchanged, and
    /// <c>GET /queue</c> hands whatever it finds straight back.
    /// </summary>
    public static JsonNode? ToNode(JsonElement? value)
    {
        if (value is not { } element) return null;

        return element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : JsonNode.Parse(element.GetRawText());
    }

    /// <summary>
    /// <c>`${value}`</c> — the string a template literal interpolates, which is JavaScript's
    /// ToString and NOT <c>JSON.stringify</c>. Numbers use the shortest round-trip form, which
    /// matches <c>Number.prototype.toString</c> for every value a room label plausibly holds.
    ///
    /// <para>The two non-scalar kinds are the ones a naive port gets wrong, and both are
    /// reachable because every object and array is TRUTHY in JavaScript: an object interpolates
    /// as the literal <c>[object Object]</c> via <c>Object.prototype.toString</c>, never as its
    /// JSON; and an array interpolates as <c>Array.prototype.toString</c>, i.e. its elements
    /// joined with <c>","</c>, with null and undefined elements contributing the EMPTY string and
    /// nested arrays flattening through their own toString. So <c>{}</c> is "[object Object]",
    /// <c>[]</c> is "", <c>[1,2]</c> is "1,2", <c>[null,null,3]</c> is ",,3" and
    /// <c>[[1,2],3]</c> is "1,2,3". Emitting <c>GetRawText()</c> would put "{}" and "[1,2]" into
    /// the stored notification message instead.</para>
    /// </summary>
    public static string ToDisplayText(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number when value.TryGetDouble(out var number) =>
                number.ToString("R", CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",

            // `String(null)` and `String(undefined)` are "null"/"undefined" on their own, but an
            // ELEMENT of an array contributes "" — and an array element is the only way either
            // reaches this method, since a null/undefined room is falsy and never interpolated.
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,

            JsonValueKind.Array => string.Join(
                ",", value.EnumerateArray().Select(ToDisplayText)),

            _ => "[object Object]",
        };
}

// ── PUT /api/appointments/{id}/check-in ──────────────────────────────────────

/// <summary>
/// The check-in body. <c>room</c> is the only field read, and it is deliberately a
/// <see cref="JsonElement"/> rather than a <c>string?</c> for two reasons: the client sends it as
/// a string on some screens and a number on others (a <c>string?</c> binding would make a numeric
/// room a 400 from the model binder), and "no body at all" must be distinguishable from
/// <c>{"room": null}</c> because the notification payload DROPS the key in the first case and
/// writes <c>"room":null</c> in the second.
/// </summary>
/// <param name="Room">
/// Optional room label. Falsy values (absent, null, <c>0</c>, <c>""</c>) are treated as no room:
/// the notes blob stores <c>null</c> and the notification text omits the room clause entirely.
///
/// <para>NON-NULLABLE on purpose. A <c>JsonElement?</c> cannot carry the absent/null distinction
/// this record exists for: System.Text.Json short-circuits a JSON <c>null</c> for any
/// <c>Nullable&lt;T&gt;</c> target before the element converter runs, so <c>{"room": null}</c>
/// and an omitted <c>room</c> would BOTH arrive as C# <c>null</c> and the notification payload
/// would drop the key in both cases. Left non-nullable, an absent key stays at
/// <c>default(JsonElement)</c> whose <c>ValueKind</c> is <c>Undefined</c> — JavaScript's
/// <c>undefined</c>, the value <c>JSON.stringify</c> drops — while an explicit null binds to
/// <c>ValueKind.Null</c> and is written as <c>"room":null</c>.</para>
/// </param>
public sealed record AssistantCheckInBody(JsonElement Room);

/// <param name="AppointmentId">The <c>{id}</c> path segment.</param>
/// <param name="CallerUserId">
/// The acting user. Recorded three times: inside the notes blob as <c>checkedInBy</c>, at the top
/// level as <c>checkedInBy</c>, and nowhere in the database besides the notes text.
/// </param>
/// <param name="Body">Nullable so an absent body reaches this handler's own falsiness tests.</param>
public sealed record AssistantCheckInCommand(
    string AppointmentId,
    string CallerUserId,
    AssistantCheckInBody? Body) : IRequest<AssistantCheckInResponse>;

/// <summary>
/// Port of <c>PUT /api/appointments/{id}/check-in</c> (appointments.js:966-1118). Reception — or
/// the physician — marks the patient arrived. The appointment write itself is small: status is
/// FORCED to CONFIRMED, <c>confirmedAt</c> is preserved-or-stamped, and a
/// <c>CHECKIN:{json}</c> blob is appended to the free-text <c>notes</c> column. Everything else
/// is three independent best-effort side effects, none of which can fail the request.
///
/// <para><b>Not transactional, on purpose.</b> Node runs the update and the three side blocks as
/// four separate operations with no <c>$transaction</c>, so partial success is the normal case and
/// a 200 says nothing about which side rows landed. Wrapping any of this in
/// <c>ExecuteInTransactionAsync</c> would also be wrong mechanically: three of the four writes
/// commit through other modules' units of work and the retry strategy may run the delegate
/// twice.</para>
///
/// <para><b>No state guard.</b> There is no check that the appointment is PENDING: checking in a
/// CANCELLED, COMPLETED or NO_SHOW appointment resurrects it as CONFIRMED, and the patient cannot
/// check themselves in — only the owning physician or active clinic staff.</para>
/// </summary>
public sealed class AssistantCheckInHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IVisitDirectory visits,
    INotificationPublisher notifications,
    IWaitingQueueWriter waitingQueue,
    IPaymentWriter payments,
    IAppLogger<AssistantCheckInHandler> logger)
    : IRequestHandler<AssistantCheckInCommand, AssistantCheckInResponse>
{
    public Task<AssistantCheckInResponse> Handle(
        AssistantCheckInCommand request, CancellationToken cancellationToken = default)
        => AssistantGuard.RunAsync(
            logger, "[Appointments] Check-in error", "Failed to check in patient", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<AssistantCheckInResponse> HandleCore(
        AssistantCheckInCommand request, CancellationToken cancellationToken)
    {
        // 404 first, before any authorization work — appointments.js:972-978.
        var appointment = await appointments.GetForUpdateAsync(request.AppointmentId, cancellationToken)
            ?? throw new NotFoundException("Appointment not found");

        // physicianProfile.findUnique({ where: { userId } }) — "is the caller a physician AT ALL",
        // then "is this THEIR appointment" (appointments.js:981-982).
        var callerPhysician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken);
        var isPhysician = callerPhysician is not null && appointment.PhysicianId == callerPhysician.Id;

        // The staff row is queried ONLY when the caller is not the owning physician
        // (appointments.js:984-986), so a physician checking in their own patient never touches
        // clinic_staff. Reproduced because it is one fewer query on the common path.
        var isActiveStaff = !isPhysician
            && await clinics.IsActiveStaffAsync(request.CallerUserId, appointment.ClinicId, cancellationToken);

        // A message of its own: NOT the bare "Not authorized" that /confirm, /cancel, /complete,
        // /no-show and DELETE /{id} share (appointments.js:989).
        if (!isPhysician && !isActiveStaff)
            throw new ForbiddenException("Not authorized to check in this patient");

        // `patient?.displayName || 'Patient'` — a dangling patientUserId (there is no FK on that
        // column) or a blank display name both fall back to the literal (appointments.js:1021).
        var patient = await identity.GetUserAsync(appointment.PatientUserId, cancellationToken);
        var patientName = patient?.DisplayName is { Length: > 0 } displayName ? displayName : "Patient";

        // No body at all destructures to `undefined` in Node, which is what default(JsonElement)
        // is — see the remarks on AssistantCheckInBody.Room.
        var room = request.Body?.Room ?? default;
        var roomIsSet = AssistantJs.IsTruthy(room);

        // JSON.stringify({ checkedInAt, checkedInBy, room: room || null }) — key order, absence of
        // spaces and the ISO-with-milliseconds timestamp are all a parsing contract with
        // GET /api/appointments/queue, which JSON.parses everything after the LAST marker and
        // degrades to `checkedIn=true, checkedInAt=null, room=null` through a bare catch if the
        // bytes do not match (appointments.js:1002-1006, :1260-1268).
        var checkInPayload = AssistantJs.Stringify(new JsonObject
        {
            ["checkedInAt"] = AssistantJs.ToIsoString(DateTime.UtcNow),
            ["checkedInBy"] = request.CallerUserId,
            ["room"] = roomIsSet ? AssistantJs.ToNode(room) : null,
        });

        // Status → CONFIRMED, confirmedAt PRESERVED when already set (the opposite of
        // PUT /{id}/confirm, which overwrites), and the blob appended behind a single "\n" so a
        // walk-in becomes "WALK_IN\nCHECKIN:{…}" and a repeat check-in stacks another marker.
        appointment.CheckIn(checkInPayload);
        await dbContext.SaveChangesAsync(cancellationToken);

        // ── Side effects 1 and 2 of 3: tell the physician, then push onto the waiting room ───
        //
        // BEST-EFFORT, AND THE WHOLE BLOCK IS SWALLOWED. Node gives each of these its own
        // try/catch — appointments.js:1016-1026 (`catch (e) { logger.warn(… '[CheckIn]
        // Notification error') }`) and :1029-1062 — and chunk-6.json states it as a hard
        // contract: the 500 on this route is reachable "only if the appointment read/update or
        // the patient lookup throws; the notification, waiting-queue and payment blocks each
        // swallow their own errors and CANNOT produce a 500". The appointment UPDATE is already
        // committed above, so letting a transient fault out of here would report failure for an
        // operation that succeeded — and with the middleware's generic body, not the route's.
        //
        // The two Node blocks collapse into one catch because both dereference
        // `appt.physician.user.id`: a physician profile that cannot be resolved makes both throw
        // and be swallowed, giving 200, no notification and no queue row. The port must swallow
        // the profile READ failing as well, not just the profile being null — the port defers
        // that read to here, whereas Node had it eagerly in the include.
        try
        {
            var appointmentPhysician = await identity.GetPhysicianAsync(
                appointment.PhysicianId, cancellationToken);

            if (appointmentPhysician is not null)
            {
                // The separator at appointments.js:1018 is U+2014 EM DASH (UTF-8 E2 80 94), NOT a
                // hyphen and not an en dash. Written as an escape so the character cannot be lost to a
                // source-encoding step: this port ships UTF-8 verbatim
                // (JavaScriptEncoder.UnsafeRelaxedJsonEscaping), so a substituted hyphen would be a
                // byte-level divergence in every stored notification message.
                var roomClause = roomIsSet
                    ? $" \u2014 Room {AssistantJs.ToDisplayText(room)}"
                    : string.Empty;

                var notificationData = new JsonObject
                {
                    ["appointmentId"] = appointment.Id,
                    ["patientUserId"] = appointment.PatientUserId,
                };

                // `data: { …, room }` passes the RAW body value, not `room || null`. An absent key is
                // `undefined`, which JSON.stringify DROPS entirely; an explicit `{"room": null}`
                // survives as `"room":null`; a falsy `0` survives as `0`. That is why the body binds
                // room as a JsonElement rather than a string.
                if (room.ValueKind is not JsonValueKind.Undefined)
                    notificationData["room"] = AssistantJs.ToNode(room);

                // Never throws, and commits through the Notifications unit of work — hence after our
                // own save, and never inside a transaction of ours. sendEmail is not passed at this
                // call site, so it stays false.
                await notifications.PublishAsync(
                    new NotificationRequest(
                        UserId: appointmentPhysician.UserId,
                        Type: "PATIENT_ARRIVED",
                        Title: "Patient Arrived",
                        Message: $"{patientName} has checked in{roomClause} ({appointment.StartTime})",
                        Data: AssistantJs.Stringify(notificationData),
                        SendEmail: false),
                    cancellationToken);

                // ── Side effect 2 of 3: push onto the clinic's waiting room ─────────────────────
                //
                // NOT REPRODUCED YET. appointments.js:1027-1062 checks for an existing WAITING /
                // BEING_SEEN entry for this clinic+patient today, reads the day's highest
                // queueNumber, and inserts queueNumber + 1. The WaitingRoom module that owns
                // waiting_queues does not exist, so IWaitingQueueWriter's only registration is
                // UnimplementedWaitingQueueWriter, which logs at warning level and returns.
                //
                // The endpoint stays byte-valid: neither the response nor GET /api/appointments/queue
                // reads waiting_queues (that route derives checkedIn/checkedInAt/room from the notes
                // marker written above). What is lost is the PHYSICIAN'S WAITING-ROOM SCREEN, a
                // different module's endpoints, which will not show a patient checked in here.
                //
                // queueDate is LOCAL midnight, matching `new Date(y, m, d)` at appointments.js:1031 —
                // not UtcNow.Date. Every Node query over this column builds the window the same
                // local-midnight way, so it is self-consistent and an implementation that switches to
                // UTC would mismatch every existing row.
                var localToday = DateTime.Now.Date;

                await waitingQueue.EnqueueIfAbsentAsync(
                    new WaitingQueueEnrolment(
                        ClinicId: appointment.ClinicId,
                        PhysicianUserId: appointmentPhysician.UserId,
                        PatientUserId: appointment.PatientUserId,
                        // The ACCOUNT HOLDER's name even when the booking is for a dependant: unlike
                        // POST /walk-in, this path never resolves the subprofile (appointments.js:1053).
                        PatientName: patientName,
                        AppointmentId: appointment.Id,
                        QueueDate: localToday,
                        // Both null here for the same reason — see PatientName above.
                        SubprofileId: null,
                        SubprofileName: null,
                        // `appt.reason || null`, so an empty-string reason is stored as null.
                        Reason: appointment.Reason is { Length: > 0 } reason ? reason : null,
                        // "APPOINTMENT" on both appointment paths, despite the column defaulting to
                        // "WALK_IN" (appointments.js:1055).
                        Source: "APPOINTMENT"),
                    cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // appointments.js:1026 and :1062 — two `logger.warn` calls, no rethrow, no effect on
            // the response. Warning level, matching Node; the check-in itself has committed.
            logger.Warning(
                "[CheckIn] Notification or waiting-room error",
                new { AppointmentId = appointment.Id, Error = exception.ToString() });
        }

        // ── Side effect 3 of 3: auto-create the PENDING cash payment ────────────────────────
        //
        // PARTIALLY REPRODUCED. The fee DECISION below is fully ported — it is an Appointments
        // contract quirk and must stay visible here — but the payments row itself belongs to the
        // Payments module, which does not exist, so IPaymentWriter's only registration is
        // UnimplementedPaymentWriter and it returns null. appointments.js:1092-1105 is the insert
        // that is not reproduced.
        //
        // null is a value Node itself produces here in three situations (a payment already
        // existed, no clinic row, or the write threw), so the response stays byte-valid. What is
        // lost is that `payment` is now ALWAYS null, so the client can never distinguish "fee due"
        // from "already handled".
        AppointmentPaymentSummary? payment = null;

        // BEST-EFFORT, AND THE WHOLE BLOCK IS SWALLOWED — appointments.js:1067-1106 is one
        // try/catch whose failure path simply leaves `autoPayment` null. Both DB reads below (the
        // clinic row and the completed-visit count) sit inside it, so a timeout on either is a 200
        // with `payment: null`, never a 500. The appointment update has already committed.
        try
        {
            // A SECOND clinic read — the Node handler already pulled { id, name } through the
            // appointment's include and re-reads for the fee columns. `if (clinic)` skips the whole
            // block when the row is missing, which is the `payment: null` path.
            var clinic = await clinics.GetClinicAsync(appointment.ClinicId, cancellationToken);
            if (clinic is not null)
            {
                // Any COMPLETED visit for this clinic+patient makes it a follow-up
                // (appointments.js:1074-1082). Not scoped to this physician.
                var priorVisitCount = await visits.CountCompletedAsync(
                    appointment.ClinicId, appointment.PatientUserId, cancellationToken);
                var isFollowUp = priorVisitCount > 0;

                // `clinic.followUpFee || 200` / `clinic.consultationFee || 300` — `||`, not `??`, so a
                // fee a clinic deliberately configured as 0 is falsy and silently becomes 200/300. A
                // free consultation is impossible through this route. Copied verbatim
                // (appointments.js:1083-1084).
                var fee = isFollowUp
                    ? (clinic.FollowUpFee is null or 0d ? 200d : clinic.FollowUpFee.Value)
                    : (clinic.ConsultationFee is null or 0d ? 300d : clinic.ConsultationFee.Value);

                // Only reachable as false when a fee column holds a NEGATIVE number, because the
                // falsy-zero substitution above has already run (appointments.js:1090).
                if (fee > 0)
                {
                    // Never throws; returns null instead. The duplicate guard behind the port is a
                    // bare findFirst on appointmentId with NO status filter, so a CANCELLED or
                    // REFUNDED payment permanently suppresses a new one.
                    payment = await payments.CreateForAppointmentIfAbsentAsync(
                        new AppointmentPaymentRequest(
                            ClinicId: appointment.ClinicId,
                            // The appointment's physician PROFILE id, not the acting user.
                            PhysicianId: appointment.PhysicianId,
                            PatientUserId: appointment.PatientUserId,
                            AppointmentId: appointment.Id,
                            Amount: fee,
                            Description: isFollowUp ? "Follow-up visit fee" : "Consultation fee",
                            Currency: "EGP",
                            Method: "CASH",
                            Status: "PENDING"),
                        cancellationToken);
                }
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // appointments.js:1106 — `catch (e) { logger.warn(… '[CheckIn] Auto-payment failed') }`.
            // `payment` keeps whatever it held, which on this path is still null.
            logger.Warning(
                "[CheckIn] Auto-payment error",
                new { AppointmentId = appointment.Id, Error = exception.ToString() });
        }

        return AssistantMapper.ToCheckInResponse(
            appointment, "Patient checked in", request.CallerUserId, payment);
    }
}

// ── POST /api/appointments/{id}/intake-note ──────────────────────────────────

/// <summary>
/// The intake-note body. <c>note</c> is a <see cref="JsonElement"/> rather than a
/// <c>string?</c> because a NON-STRING note is an observable 500, not a 400: Node's guard is
/// <c>!note?.trim()</c>, which only short-circuits on null and undefined, so <c>123</c>,
/// <c>{}</c> and <c>[]</c> all reach <c>undefined()</c>, throw a TypeError and land in the outer
/// catch as <c>500 {"error":"Failed to save intake note"}</c>. A <c>string?</c> binding would let
/// the model binder answer <c>400 {"error":"Validation failed", …}</c> instead — a different
/// status AND a different body.
/// </summary>
/// <param name="Note">
/// The note text. Required, and must be non-empty after trimming; no maximum length is enforced
/// on this route even though <c>PUT /api/patient-notes/{patientUserId}</c> rejects content over
/// 5000 characters.
/// </param>
public sealed record AssistantIntakeNoteBody(JsonElement? Note);

/// <param name="AppointmentId">
/// The <c>{id}</c> path segment. Used ONLY to resolve the patient and the physician — the stored
/// note carries no reference back to the appointment, so two appointments for the same patient
/// write to the same note row.
/// </param>
/// <param name="CallerUserId">
/// The acting user, embedded verbatim in the note's own text as <c>[INTAKE by &lt;id&gt;]</c>.
/// </param>
/// <param name="Body">Nullable so an absent body reaches the 400 guard rather than the binder.</param>
public sealed record AssistantIntakeNoteCommand(
    string AppointmentId,
    string CallerUserId,
    AssistantIntakeNoteBody? Body) : IRequest<AssistantIntakeNoteResponse>;

/// <summary>
/// Port of <c>POST /api/appointments/{id}/intake-note</c> (appointments.js:1126-1203). Reception
/// writes a pre-visit note for the physician to review, and the physician is notified unless they
/// wrote it themselves. Answers <b>200, never 201</b>, with the stored <c>PatientNote</c> row plus
/// a <c>message</c> key.
///
/// <para><b>The note itself is not reproduced yet.</b> <c>patient_notes</c> belongs to the
/// PatientNotes module, which does not exist, so <see cref="IPatientNoteWriter"/>'s only
/// registration returns null and this handler answers Node's own failure body for the route,
/// <c>500 {"error":"Failed to save intake note"}</c>. Everything BEFORE the write is fully ported
/// — the 400, the 404, the 403, the physician-user resolution — and the notification is
/// deliberately not sent, because Node only notifies after a successful save.</para>
///
/// <para><b>No permission check beyond the staff row, and no connection check.</b> The route's own
/// docblock claims "clinic staff with canDraftNotes", but <c>permissions.canDraftNotes</c> is
/// never read: ANY active staff row at the appointment's clinic can write a clinical note, about a
/// patient the physician has no connection to. <c>PUT /api/patient-notes/{patientUserId}</c> does
/// call <c>validateConnection</c>; this route does not.</para>
/// </summary>
public sealed class AssistantIntakeNoteHandler(
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientNoteWriter patientNotes,
    INotificationPublisher notifications,
    IAppLogger<AssistantIntakeNoteHandler> logger)
    : IRequestHandler<AssistantIntakeNoteCommand, AssistantIntakeNoteResponse>
{
    /// <summary>
    /// The <c>startsWith</c> filter Node's dedupe lookup applies (appointments.js:1161). It can
    /// NEVER match, because the same handler writes content beginning
    /// <c>"[INTAKE by &lt;userId&gt;]"</c> twenty lines earlier and nothing anywhere in the repo
    /// writes the bare literal — so the update branch is unreachable and every call creates a row.
    /// Passed through anyway rather than dropped: see the note on
    /// <see cref="AssistantIntakeNoteHandler"/>.
    /// </summary>
    private const string DeadUpdateBranchPrefix = "[INTAKE]";

    public Task<AssistantIntakeNoteResponse> Handle(
        AssistantIntakeNoteCommand request, CancellationToken cancellationToken = default)
        => AssistantGuard.RunAsync(
            logger, "[Appointments] Intake note error", "Failed to save intake note", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<AssistantIntakeNoteResponse> HandleCore(
        AssistantIntakeNoteCommand request, CancellationToken cancellationToken)
    {
        // The 400 runs BEFORE the appointment is even looked up (appointments.js:1131), so a blank
        // note against a non-existent appointment is a 400, not a 404. Order is observable.
        // An absent body, an absent `note` key and an explicit `"note": null` are all the same
        // nullish value to `note?.trim()`, so all three take the 400.
        if (request.Body?.Note is not { } noteElement)
            throw AssistantErrors.Node("Note content required");

        if (noteElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw AssistantErrors.Node("Note content required");

        // `!note?.trim()` on a non-string: the optional chain does not short-circuit, `.trim` is
        // undefined, calling it throws, and the outer catch turns the intended 400 into this 500.
        // A bug, and the contract.
        if (noteElement.ValueKind is not JsonValueKind.String)
            throw AssistantErrors.Node("Failed to save intake note", 500);

        // JS String.prototype.trim and .NET String.Trim agree on every character either treats as
        // whitespace except U+FEFF, which JS strips and .NET keeps. Accepted micro-divergence.
        var noteText = (noteElement.GetString() ?? string.Empty).Trim();
        if (noteText.Length == 0)
            throw AssistantErrors.Node("Note content required");

        // AsNoTracking: this endpoint reads the appointment and never writes it.
        var appointment = await appointments.GetAsync(request.AppointmentId, cancellationToken)
            ?? throw new NotFoundException("Appointment not found");

        var callerPhysician = await identity.GetPhysicianByUserIdAsync(request.CallerUserId, cancellationToken);
        var isPhysician = callerPhysician is not null && appointment.PhysicianId == callerPhysician.Id;

        var isActiveStaff = !isPhysician
            && await clinics.IsActiveStaffAsync(request.CallerUserId, appointment.ClinicId, cancellationToken);

        // Again a message of this route's own — longer than the bare "Not authorized" used by the
        // status transitions (appointments.js:1150).
        if (!isPhysician && !isActiveStaff)
            throw new ForbiddenException("Not authorized to write intake notes");

        // `isPhysician ? userId : appt.physician.user.id` (appointments.js:1153). Both branches
        // resolve to the same value whenever the data is consistent — isPhysician already required
        // appointment.PhysicianId to be the caller's profile — but the ternary is preserved because
        // an inconsistency would make them diverge, and because the FALSE branch dereferences the
        // physician relation OUTSIDE any try/catch: a physician profile that cannot be read is a
        // TypeError caught by the outer handler as this route's 500. The true branch never
        // dereferences it, so it cannot 500 that way.
        string physicianUserId;

        if (isPhysician)
        {
            physicianUserId = request.CallerUserId;
        }
        else
        {
            var appointmentPhysician =
                await identity.GetPhysicianAsync(appointment.PhysicianId, cancellationToken)
                ?? throw AssistantErrors.Node("Failed to save intake note", 500);

            physicianUserId = appointmentPhysician.UserId;
        }

        // The raw author UUID is part of the stored text, and the client renders the string
        // verbatim (appointments.js:1156).
        var content = $"[INTAKE by {request.CallerUserId}]\n{noteText}";

        // NOT REPRODUCED YET — the create at appointments.js:1168-1174.
        //
        // This port is the one non-best-effort port on these two endpoints: the stored row's id,
        // content and timestamps ARE the response body, so there is no honest 200 without it and
        // synthesising one would report a note nobody can read back.
        //
        // MatchContentPrefix carries the dead lookup rather than dropping it, so whoever
        // implements PatientNotes inherits the broken dedupe instead of an implicit
        // create-only contract they might later "fix" into a live upsert.
        //
        // NOTE FOR THE IMPLEMENTER — patient_notes carries
        // @@unique([physicianUserId, patientUserId, subprofileId]) and this path always writes
        // subprofileId = NULL. PostgreSQL treats NULLs as DISTINCT, so Node's repeated creates all
        // succeed and quietly pile up duplicate rows; SQL Server treats them as EQUAL, so the
        // SECOND intake note for the same physician+patient pair would raise a duplicate-key error
        // and return this route's 500 where Node returns 200. That needs a filtered/NULL-tolerant
        // unique index in the PatientNotes module. It is NOT worked around here — Appointments does
        // not own the table, and no migration is added for a table this module cannot see.
        var savedNote = await patientNotes.UpsertNoteAsync(
            new PatientNoteUpsert(
                PhysicianUserId: physicianUserId,
                PatientUserId: appointment.PatientUserId,
                Content: content,
                MatchContentPrefix: DeadUpdateBranchPrefix),
            cancellationToken);

        // appointments.js:1199-1202 — the route's own catch-all body. Reached whenever no row was
        // stored, which today is always.
        if (savedNote is null)
            throw AssistantErrors.Node("Failed to save intake note", 500);

        // BEST-EFFORT, AND SWALLOWED — appointments.js:1183-1195 is a try/catch around the patient
        // read and the notification alike, so a timeout on the read leaves the already-saved
        // note's 200 intact rather than reporting a failure for a write that succeeded.
        //
        // Skipped entirely when the physician wrote their own note (appointments.js:1177), and
        // never reached at all unless a row was actually saved — Node notifies after the write, and
        // the notification carries the saved note's id.
        try
        {
            if (!isPhysician)
            {
                var patient = await identity.GetUserAsync(appointment.PatientUserId, cancellationToken);
                // Lowercase "patient" here, unlike check-in's capitalised "Patient"
                // (appointments.js:1189 vs :1021). Two literals, two spellings, both shipped.
                var patientName = patient?.DisplayName is { Length: > 0 } displayName ? displayName : "patient";

                await notifications.PublishAsync(
                    new NotificationRequest(
                        // `appt.physician.user.id` — which on this branch is exactly the
                        // physicianUserId resolved above.
                        UserId: physicianUserId,
                        Type: "INTAKE_NOTE_READY",
                        Title: "Intake Note Ready",
                        Message: $"Pre-visit intake note written for {patientName} ({appointment.StartTime})",
                        Data: AssistantJs.Stringify(new JsonObject
                        {
                            ["appointmentId"] = appointment.Id,
                            ["noteId"] = savedNote.Id,
                            ["patientUserId"] = appointment.PatientUserId,
                        }),
                        // sendEmail is not passed at this call site.
                        SendEmail: false),
                    cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // appointments.js:1195 — `catch (e) { logger.warn(… '[IntakeNote] Notification
            // error') }`. No effect on the response.
            logger.Warning(
                "[IntakeNote] Notification error",
                new { AppointmentId = appointment.Id, Error = exception.ToString() });
        }

        return AssistantMapper.ToIntakeNoteResponse(savedNote, "Intake note saved");
    }
}
