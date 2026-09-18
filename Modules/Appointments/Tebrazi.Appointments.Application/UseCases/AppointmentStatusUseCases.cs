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

// ═════════════════════════════════════════════════════════════════════════════
//  The four status transitions plus the hard delete, ported from
//  server/src/routes/appointments.js:
//
//      PUT    /api/appointments/{id}/confirm    (L741-L783)
//      PUT    /api/appointments/{id}/cancel     (L789-L853)
//      PUT    /api/appointments/{id}/complete   (L859-L888)
//      PUT    /api/appointments/{id}/no-show    (L894-L922)
//      DELETE /api/appointments/{id}            (L928-L955)
//
//  Three things are true of all five and stated once here rather than five times:
//
//  * They are WRITE endpoints, so per the module's house rule the caller's user id arrives as an
//    explicit `CallerUserId` on the command; none of them injects ICurrentUser.
//  * 404 comes BEFORE 403. Every one of them reads the appointment first and answers
//    `{"error":"Appointment not found"}` for a miss, so a caller with no rights to a row that does
//    not exist learns it does not exist.
//  * There is NO state machine and NO `deletedAt` filter. A CANCELLED row can be confirmed, a
//    COMPLETED row can be marked no-show, and a soft-deleted row is fully transitionable — nothing
//    in appointments.js reads that column. Do not add a guard the client does not expect.
//
//  THE DELETE-IS-AN-UPDATE ASYMMETRY (audit-2's name for it): the two DELETE verbs in this router
//  mean opposite things. `DELETE /api/appointments/slots/{id}` (appointments.js:96-110, another
//  file's endpoint) is an UPDATE — it writes `isActive: false` and the row survives. This file's
//  `DELETE /api/appointments/{id}` (appointments.js:948) is `prisma.appointment.delete`, a HARD
//  delete, even though `Appointment` carries a `deletedAt` soft-delete column. So this group takes
//  `IAppointmentStore.Remove` and must never call `Appointment.SoftDelete()`.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Every error body in appointments.js is a bare <c>{ "error": "..." }</c>. Passing the same literal
/// as error and message makes <c>ExceptionHandlingMiddleware</c> drop the redundant <c>message</c>
/// key; <c>ValidationException</c> / <c>ConflictException</c> would instead emit
/// <c>{"error":"Validation failed", "message": ...}</c>, which the client does not match on.
///
/// Declared internal to this file on purpose — four sibling handler files are being written into the
/// same namespace and a shared copy of this type would collide.
/// </summary>
internal static class StatusErrors
{
    public static BusinessException Node(string error, int statusCode) => new(error, error, statusCode);
}

/// <summary>
/// The outcome of the shared authorization gate, carrying the branch flag <c>/cancel</c> needs to
/// pick a notification recipient.
///
/// <c>Physician</c> is the caller's OWN physician profile, or null when they have none. It is
/// resolved even for a caller who turns out to be authorized as the patient or as staff, because
/// Node reads it unconditionally before testing anything.
/// </summary>
internal readonly record struct StatusCaller(
    PhysicianSummary? Physician,
    bool IsPhysician,
    bool IsPatient,
    bool IsStaff);

/// <summary>
/// The three-caller gate these five endpoints share, with the ONE difference between them modelled
/// as the <c>allowPatient</c> argument.
///
/// The accepted callers, tested in this order (appointments.js:749-758, :798-808, :867-876,
/// :902-911, :936-946):
///
/// <list type="number">
/// <item>the physician whose PROFILE ID equals <c>appointment.physicianId</c> — identity, not clinic
/// membership, so a different physician practising at the same clinic is NOT authorized unless they
/// also hold a staff row, and the owning physician stays authorized at a clinic they no longer
/// own;</item>
/// <item>the patient the appointment is for — <c>cancel</c> and <c>DELETE</c> only. This is the only
/// axis on which the five differ: a patient can cancel or permanently erase their appointment but
/// cannot confirm, complete or no-show it;</item>
/// <item>an ACTIVE <c>ClinicStaff</c> row at <c>appointment.clinicId</c>. There is no
/// <c>userType</c> check anywhere, so a PATIENT-typed account that is active clinic staff can
/// confirm and complete other people's appointments.</item>
/// </list>
///
/// The staff query is SHORT-CIRCUITED — Node writes it as
/// <c>!isPhysician ? findFirst(...) : null</c>, so it never runs for the physician (or, where
/// allowed, the patient). Reproduced below with <c>&amp;&amp;</c> so the query count matches; the
/// outcome would be the same either way.
/// </summary>
internal static class StatusAuthorization
{
    public static async Task<StatusCaller> ResolveAsync(
        Appointment appointment,
        string callerUserId,
        bool allowPatient,
        IIdentityDirectory identity,
        IClinicDirectory clinics,
        CancellationToken cancellationToken)
    {
        var physician = await identity.GetPhysicianByUserIdAsync(callerUserId, cancellationToken);
        var isPhysician = physician is not null && appointment.PhysicianId == physician.Id;
        var isPatient = allowPatient && appointment.PatientUserId == callerUserId;

        var isStaff = !isPhysician
            && !isPatient
            && await clinics.IsActiveStaffAsync(callerUserId, appointment.ClinicId, cancellationToken);

        if (!isPhysician && !isPatient && !isStaff)
            throw new ForbiddenException("Not authorized");

        return new StatusCaller(physician, isPhysician, isPatient, isStaff);
    }
}

/// <summary>
/// Commits the transition, mapping any persistence failure onto the route's OWN 500 body.
///
/// Each Node handler is wrapped in a try/catch whose only outcome is a named 500 —
/// <c>{"error":"Failed to confirm"}</c>, <c>"Failed to cancel"</c>, <c>"Failed to complete"</c>,
/// <c>"Failed to mark no-show"</c>, <c>"Failed to delete appointment"</c>. Letting an EF exception
/// escape would emit the middleware's generic <c>{"error":"Internal Server Error"}</c> instead, so
/// the original is logged the way Node logs it and the named body is thrown in its place.
///
/// This is also the path that reproduces Node's concurrency behaviour on <c>DELETE /{id}</c>: the
/// find-then-delete pair is not atomic, so when a second request has already removed the row the
/// caller gets 500 <c>"Failed to delete appointment"</c> (Prisma P2025), NOT the 404 the pre-check
/// was meant to give.
/// </summary>
internal static class StatusPersistence
{
    /// <summary>
    /// The route-level guard. Node wraps each handler END TO END — appointments.js:742-782
    /// (confirm), :790-852 (cancel), :860-887 (complete), :895-921 (no-show), :929-954 (delete) —
    /// so a failure in the PRE-SAVE work answers the same named 500 as a failure in the save:
    /// the appointment read, <c>IIdentityDirectory.GetPhysicianByUserIdAsync</c> and
    /// <c>IClinicDirectory.IsActiveStaffAsync</c> all sit inside that try. <see cref="SaveAsync"/>
    /// covers only <c>SaveChangesAsync</c>, so without this a DB timeout on one of those reads
    /// escapes to ExceptionHandlingMiddleware and the client — which branches on
    /// <c>err.response.data.error</c> — reads "Internal Server Error" instead of "Failed to
    /// confirm".
    /// </summary>
    /// <remarks>
    /// An <see cref="AppException"/> passes through untouched, so NotFoundException and
    /// ForbiddenException still produce their 404 and 403, and <see cref="SaveAsync"/>'s own named
    /// 500 is not re-wrapped or re-logged. /cancel's inner notification catch stays nested inside
    /// this, exactly as Node nests its own.
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
            throw StatusErrors.Node(failureError, 500);
        }
    }

    public static async Task SaveAsync<THandler>(
        IAppointmentsDbContext dbContext,
        IAppLogger<THandler> logger,
        string logMessage,
        string failureError,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is not AppException && !cancellationToken.IsCancellationRequested)
        {
            logger.Error(logMessage, exception);
            throw StatusErrors.Node(failureError, 500);
        }
    }
}

// ── PUT /api/appointments/{id}/confirm ───────────────────────────────────────

public sealed record StatusConfirmAppointmentCommand(
    string AppointmentId,
    string CallerUserId) : IRequest<StatusConfirmedResponse>;

/// <summary>
/// Port of <c>PUT /api/appointments/{id}/confirm</c> (appointments.js:741-783). Flips the row to
/// CONFIRMED, re-stamps <c>confirmedAt</c>, and notifies the PATIENT with an email.
///
/// The request body is not read at all.
/// </summary>
public sealed class StatusConfirmAppointmentHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    INotificationPublisher notifications,
    IAppLogger<StatusConfirmAppointmentHandler> logger)
    : IRequestHandler<StatusConfirmAppointmentCommand, StatusConfirmedResponse>
{
    public Task<StatusConfirmedResponse> Handle(
        StatusConfirmAppointmentCommand request, CancellationToken cancellationToken = default)
        => StatusPersistence.RunAsync(
            logger, "[Appointments] Confirm error", "Failed to confirm", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<StatusConfirmedResponse> HandleCore(
        StatusConfirmAppointmentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetForUpdateAsync(request.AppointmentId, cancellationToken)
            ?? throw new NotFoundException("Appointment not found");

        // The booking patient CANNOT confirm their own appointment — only the physician or staff.
        await StatusAuthorization.ResolveAsync(
            appointment, request.CallerUserId, allowPatient: false, identity, clinics, cancellationToken);

        // Status -> CONFIRMED and confirmedAt OVERWRITTEN with now. appointments.js:762 writes
        // `confirmedAt: new Date()` unconditionally, so a second confirm moves the timestamp — the
        // exact opposite of /check-in, which preserves it with `appt.confirmedAt || new Date()`.
        appointment.Confirm();

        await StatusPersistence.SaveAsync(
            dbContext, logger, "[Appointments] Confirm error", "Failed to confirm", cancellationToken);

        // Notifications commit through the Notifications module's unit of work, so they run AFTER
        // our save and outside any transaction of ours. The port's INotificationPublisher never
        // throws, which is the guarantee that replaces Node's own try/catch at appointments.js:766.
        //
        // Node builds this text from `appt` (the PRE-update read) rather than from `updated`; the
        // transition touches neither appointmentDate nor startTime, so the entity's own values are
        // identical.
        await notifications.PublishAsync(
            new NotificationRequest(
                UserId: appointment.PatientUserId,
                Type: "APPOINTMENT_CONFIRMED",
                Title: "Appointment Confirmed",
                // `toLocaleDateString()` with NO locale argument — server-culture short date, and
                // rendered in the server's timezone. /cancel formats the same column differently
                // (explicit 'en-US' + weekday/month/day); both formats are copied separately
                // because both end up stored in a notification row.
                Message: $"Your appointment on {appointment.AppointmentDate.ToLocalTime().ToShortDateString()}"
                    + $" at {appointment.StartTime} has been confirmed.",
                Data: new JsonObject { ["appointmentId"] = appointment.Id }.ToJsonString(),
                SendEmail: true),
            cancellationToken);

        return StatusAppointmentMapper.ToConfirmedResponse(appointment, "Appointment confirmed");
    }
}

// ── PUT /api/appointments/{id}/cancel ────────────────────────────────────────

/// <param name="Reason">
/// The ONLY field this route reads. Typed <c>JsonElement?</c> rather than <c>string?</c> because
/// Node stores <c>reason || null</c> (appointments.js:815) and the JS truthiness of a non-string is
/// observable: <c>0</c>, <c>false</c>, <c>""</c> and <c>null</c> all become a stored null, whereas
/// the STRING <c>"0"</c> is kept, and a truthy non-string (a number, <c>true</c>, an object) reaches
/// Prisma as the wrong type for a <c>String?</c> column and surfaces as
/// <c>500 {"error":"Failed to cancel"}</c>. A plain <c>string?</c> member would let the model binder
/// answer 400 for those instead.
/// </param>
public sealed record StatusCancelAppointmentBody(JsonElement? Reason);

public sealed record StatusCancelAppointmentCommand(
    string AppointmentId,
    string CallerUserId,
    StatusCancelAppointmentBody? Body) : IRequest<StatusCancelledResponse>;

/// <summary>
/// Port of <c>PUT /api/appointments/{id}/cancel</c> (appointments.js:789-853). The widest
/// authorization of the five — the patient may cancel — and the only one whose notification picks a
/// recipient from a branch.
/// </summary>
public sealed class StatusCancelAppointmentHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    INotificationPublisher notifications,
    IAppLogger<StatusCancelAppointmentHandler> logger)
    : IRequestHandler<StatusCancelAppointmentCommand, StatusCancelledResponse>
{
    /// <summary>
    /// <c>toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })</c>
    /// (appointments.js:822) — "Tue, Mar 10". Note there is NO year.
    /// </summary>
    private const string CancelDateFormat = "ddd, MMM d";

    public Task<StatusCancelledResponse> Handle(
        StatusCancelAppointmentCommand request, CancellationToken cancellationToken = default)
        => StatusPersistence.RunAsync(
            logger, "[Appointments] Cancel error", "Failed to cancel", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<StatusCancelledResponse> HandleCore(
        StatusCancelAppointmentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetForUpdateAsync(request.AppointmentId, cancellationToken)
            ?? throw new NotFoundException("Appointment not found");

        var caller = await StatusAuthorization.ResolveAsync(
            appointment, request.CallerUserId, allowPatient: true, identity, clinics, cancellationToken);

        var reason = ResolveReason(request.Body?.Reason);

        // Status -> CANCELLED, cancelledAt OVERWRITTEN, cancelReason replaced verbatim with no length
        // limit. Because the write is unconditional, a second cancel with an empty body NULLS OUT the
        // reason the first one stored.
        appointment.Cancel(reason);

        await StatusPersistence.SaveAsync(
            dbContext, logger, "[Appointments] Cancel error", "Failed to cancel", cancellationToken);

        await NotifyCancellationAsync(appointment, caller, request.CallerUserId, reason, cancellationToken);

        return StatusAppointmentMapper.ToCancelledResponse(appointment, "Appointment cancelled");
    }

    /// <summary>
    /// appointments.js:819-846. Node wraps this WHOLE block — its two directory reads included — in a
    /// try/catch that only logs, so a failure to resolve either party must still answer 200 with the
    /// cancelled row. INotificationPublisher's own no-throw guarantee does not cover the reads, so
    /// the catch is reproduced here.
    /// </summary>
    private async Task NotifyCancellationAsync(
        Appointment appointment,
        StatusCaller caller,
        string callerUserId,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            // Read unconditionally, exactly as Node does — it is a DEAD read on the physician/staff
            // branch, where `canceller` is fetched and then never used. Kept for query parity.
            var canceller = await identity.GetUserAsync(callerUserId, cancellationToken);

            var dateText = appointment.AppointmentDate
                .ToLocalTime()
                .ToString(CancelDateFormat, CultureInfo.GetCultureInfo("en-US"));

            // ' — Reason: ' carries a literal EM DASH (U+2014). The suffix is gated on the RAW body
            // value's truthiness, which is the same test that turned "" into a stored null, so the
            // resolved reason drives both.
            var reasonSuffix = reason is null ? string.Empty : $" — Reason: {reason}";

            var data = new JsonObject { ["appointmentId"] = appointment.Id }.ToJsonString();

            // The branch is `if (isPatient)`, NOT "if not the physician". So when one account is both
            // the physician and the patient (a physician who booked themselves) the patient branch
            // wins: they notify themselves and no email is sent.
            if (caller.IsPatient)
            {
                // Patient cancelled -> notify the physician, with NO email. The stored PhysicianId is
                // a profile id, so it has to be turned into a user id first; when that profile cannot
                // be read, Node sends NOTHING rather than falling back to another recipient.
                var physicianProfile = await identity.GetPhysicianAsync(
                    appointment.PhysicianId, cancellationToken);

                if (physicianProfile is null) return;

                var cancellerName = canceller?.DisplayName is { Length: > 0 } displayName
                    ? displayName
                    : "A patient";

                await notifications.PublishAsync(
                    new NotificationRequest(
                        UserId: physicianProfile.UserId,
                        Type: "APPOINTMENT_CANCELLED",
                        Title: "Appointment Cancelled",
                        Message: $"{cancellerName} cancelled their appointment on {dateText}"
                            + $" at {appointment.StartTime}{reasonSuffix}.",
                        Data: data),
                    cancellationToken);

                return;
            }

            // Physician OR staff cancelled -> notify the patient, WITH email. A staff-initiated
            // cancellation therefore reads "Your appointment ... has been cancelled" and never names
            // the staff member, even though their display name was just fetched.
            await notifications.PublishAsync(
                new NotificationRequest(
                    UserId: appointment.PatientUserId,
                    Type: "APPOINTMENT_CANCELLED",
                    Title: "Appointment Cancelled",
                    Message: $"Your appointment on {dateText} at {appointment.StartTime}"
                        + $" has been cancelled{reasonSuffix}.",
                    Data: data,
                    SendEmail: true),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Error("[Appointments] Cancel notification error", exception);
        }
    }

    /// <summary>
    /// <c>cancelReason: reason || null</c> (appointments.js:815) over a raw JSON value. Falsy inputs
    /// store null; a truthy non-string is a type error for the column and takes the route's own 500.
    /// </summary>
    private static string? ResolveReason(JsonElement? reason)
        => reason switch
        {
            null => null,
            { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False } => null,
            { ValueKind: JsonValueKind.String } text
                => text.GetString() is { Length: > 0 } value ? value : null,
            // `0 || null` is null; any other number is truthy and then fails Prisma's String check.
            { ValueKind: JsonValueKind.Number } number
                => number.TryGetDouble(out var value) && value == 0
                    ? null
                    : throw StatusErrors.Node("Failed to cancel", 500),
            _ => throw StatusErrors.Node("Failed to cancel", 500)
        };
}

// ── PUT /api/appointments/{id}/complete ──────────────────────────────────────

public sealed record StatusCompleteAppointmentCommand(
    string AppointmentId,
    string CallerUserId) : IRequest<StatusCompletedResponse>;

/// <summary>
/// Port of <c>PUT /api/appointments/{id}/complete</c> (appointments.js:859-888). The simplest of the
/// five: one column pair written, and NO side effect of any kind.
///
/// audit-2 corrects the extraction here — completing sends no notification, but neither does
/// <c>/no-show</c>, so TWO of the four transitions are silent and nothing may be inferred from
/// this one being silent. It also does not complete the waiting-queue entry, does not collect the
/// PENDING payment that <c>/check-in</c> created, and does not touch any Visit; <c>GET /queue</c>
/// overlays live Visit status separately, so the queue and the appointment can disagree.
///
/// The request body is not read at all.
/// </summary>
public sealed class StatusCompleteAppointmentHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<StatusCompleteAppointmentHandler> logger)
    : IRequestHandler<StatusCompleteAppointmentCommand, StatusCompletedResponse>
{
    public Task<StatusCompletedResponse> Handle(
        StatusCompleteAppointmentCommand request, CancellationToken cancellationToken = default)
        => StatusPersistence.RunAsync(
            logger, "[Appointments] Complete error", "Failed to complete", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<StatusCompletedResponse> HandleCore(
        StatusCompleteAppointmentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetForUpdateAsync(request.AppointmentId, cancellationToken)
            ?? throw new NotFoundException("Appointment not found");

        // The patient cannot complete their own appointment.
        await StatusAuthorization.ResolveAsync(
            appointment, request.CallerUserId, allowPatient: false, identity, clinics, cancellationToken);

        // Status -> COMPLETED and completedAt OVERWRITTEN (appointments.js:880). cancelledAt and
        // cancelReason are deliberately NOT cleared.
        appointment.Complete();

        // The 500 label is "Failed to complete", not "Failed to complete appointment".
        await StatusPersistence.SaveAsync(
            dbContext, logger, "[Appointments] Complete error", "Failed to complete", cancellationToken);

        return StatusAppointmentMapper.ToCompletedResponse(appointment, "Appointment completed");
    }
}

// ── PUT /api/appointments/{id}/no-show ───────────────────────────────────────

public sealed record StatusNoShowAppointmentCommand(
    string AppointmentId,
    string CallerUserId) : IRequest<StatusNoShowResponse>;

/// <summary>
/// Port of <c>PUT /api/appointments/{id}/no-show</c> (appointments.js:894-922). Writes the status
/// column and NOTHING else — no timestamp, no notification, no payment void, no queue update.
///
/// The request body is not read at all.
/// </summary>
public sealed class StatusNoShowAppointmentHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<StatusNoShowAppointmentHandler> logger)
    : IRequestHandler<StatusNoShowAppointmentCommand, StatusNoShowResponse>
{
    public Task<StatusNoShowResponse> Handle(
        StatusNoShowAppointmentCommand request, CancellationToken cancellationToken = default)
        => StatusPersistence.RunAsync(
            logger, "[Appointments] No-show error", "Failed to mark no-show", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<StatusNoShowResponse> HandleCore(
        StatusNoShowAppointmentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetForUpdateAsync(request.AppointmentId, cancellationToken)
            ?? throw new NotFoundException("Appointment not found");

        // The patient cannot mark their own no-show.
        await StatusAuthorization.ResolveAsync(
            appointment, request.CallerUserId, allowPatient: false, identity, clinics, cancellationToken);

        // There is no `noShowAt` column: appointments.js:914 writes `{ status: 'NO_SHOW' }` alone, so
        // a previously CONFIRMED row keeps its confirmedAt while reading NO_SHOW.
        appointment.MarkNoShow();

        // This is the ONE transition whose write can leave the row genuinely unchanged — re-marking
        // an already-NO_SHOW appointment sets the same value, EF would see no modification, and the
        // response would echo a stale `updatedAt`. Node's `prisma.appointment.update` runs
        // regardless and Prisma's @updatedAt bumps every time, so the stamp is forced by hand;
        // BaseDbContext's audit pass then replaces the null updatedBy with the real caller.
        appointment.ApplyModificationAudit(DateTime.UtcNow, null);

        await StatusPersistence.SaveAsync(
            dbContext, logger, "[Appointments] No-show error", "Failed to mark no-show", cancellationToken);

        // "Marked as no-show" — not "Appointment marked as no-show".
        return StatusAppointmentMapper.ToNoShowResponse(appointment, "Marked as no-show");
    }
}

// ── DELETE /api/appointments/{id} ────────────────────────────────────────────

public sealed record StatusDeleteAppointmentCommand(
    string AppointmentId,
    string CallerUserId) : IRequest<MessageResponse>;

/// <summary>
/// Port of <c>DELETE /api/appointments/{id}</c> (appointments.js:928-955). A HARD delete of the row,
/// answering the bare <c>{"message":"Appointment deleted"}</c> — no id, no count, no echo of what
/// went away.
///
/// Deliberately reproduced, all of it defect-shaped:
/// <list type="bullet">
/// <item>THE PATIENT IS IN THE ALLOW-LIST, so a patient can permanently erase a record from the
/// physician's calendar — and nobody is notified, unlike <c>/cancel</c>.</item>
/// <item>No status restriction: a COMPLETED appointment is as deletable as a PENDING one.</item>
/// <item>No dependent cleanup. <c>payments.appointmentId</c>, <c>waiting_queues.appointmentId</c> and
/// <c>video_sessions.appointmentId</c> are unconstrained <c>String?</c> columns in the Node schema, so
/// the delete always succeeds and leaves them dangling. Whoever ports Payments / WaitingRoom must
/// keep those columns FK-free or this endpoint starts failing (or cascading).</item>
/// <item>A non-UUID id simply misses and 404s — <c>Appointment.id</c> is a plain text column, so
/// there is no cast error to turn into a 500.</item>
/// </list>
///
/// One live Node behaviour that lives in the ROUTING and must not change: <c>DELETE
/// /api/appointments/slots</c> with no slot id has no route of its own, binds HERE with
/// <c>id = "slots"</c>, and returns <c>404 {"error":"Appointment not found"}</c> — not a route miss
/// and not a 405.
/// </summary>
public sealed class StatusDeleteAppointmentHandler(
    IAppointmentsDbContext dbContext,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<StatusDeleteAppointmentHandler> logger)
    : IRequestHandler<StatusDeleteAppointmentCommand, MessageResponse>
{
    public Task<MessageResponse> Handle(
        StatusDeleteAppointmentCommand request, CancellationToken cancellationToken = default)
        => StatusPersistence.RunAsync(
            logger, "[Appointments] Delete error", "Failed to delete appointment", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<MessageResponse> HandleCore(
        StatusDeleteAppointmentCommand request, CancellationToken cancellationToken)
    {
        var appointment = await appointments.GetForUpdateAsync(request.AppointmentId, cancellationToken)
            ?? throw new NotFoundException("Appointment not found");

        await StatusAuthorization.ResolveAsync(
            appointment, request.CallerUserId, allowPatient: true, identity, clinics, cancellationToken);

        // `prisma.appointment.delete` — the row is REMOVED. Appointment.SoftDelete() would leave a
        // row that every read endpoint in this module still returns, because AppointmentsDbContext
        // deliberately carries no soft-delete query filter.
        appointments.Remove(appointment);

        await StatusPersistence.SaveAsync(
            dbContext, logger, "[Appointments] Delete error", "Failed to delete appointment",
            cancellationToken);

        return new MessageResponse("Appointment deleted");
    }
}
