using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
//  The five read endpoints of server/src/routes/appointments.js:
//
//    GET /api/appointments             appointments.js:352-423
//    GET /api/appointments/today       appointments.js:511-550
//    GET /api/appointments/queue       appointments.js:1210-1315
//    GET /api/appointments/available   appointments.js:258-341
//    GET /api/appointments/ai-optimize appointments.js:1328-1557
//
//  Three of them answer a BARE ARRAY, one answers a { queue, summary } envelope and one answers
//  either { stats, insights, aiProvider } or { stats, insights, message } — never a mixture.
//  None of the five paginates: GET / is a silent take: 200 with no total and no metadata, and the
//  other four return everything they find.
//
//  All five must be mapped as LITERAL routes ahead of any parameterised route the port adds.
//  Node has no GET /{id} at all, so nothing shadows `today`, `queue`, `available` or
//  `ai-optimize` there; the moment this port declares one, literal precedence is what keeps them
//  reachable (audit-2, routeOrderHazards).
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>Query-filter helpers shared by the five read endpoints.</summary>
internal static class ApptReadFilters
{
    /// <summary>
    /// JavaScript truthiness for an optional query value. Every filter and every required-field
    /// guard in these handlers is written <c>if (value)</c> in Node, so <c>?clinicId=</c> is not
    /// a filter at all — it is an absent one. ASP.NET model binding hands a present-but-valueless
    /// query key over as <c>""</c> and the stores test <c>is not null</c>, so without this the
    /// port would filter on the empty string and return nothing where Node returns everything.
    ///
    /// <para>Empty ONLY, deliberately: <c>" "</c> is truthy in JavaScript, so a whitespace filter
    /// must still reach the query and match nothing. <c>IsNullOrWhiteSpace</c> would be wrong.</para>
    /// </summary>
    public static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// The caller check these five endpoints share. <c>router.use(authCheck)</c> is path-agnostic in
/// Node, so a request with no usable token never reaches a handler.
/// </summary>
internal static class ApptReadCaller
{
    /// <summary>
    /// <c>401 {"error":"No token provided"}</c> — the literal Node body, which is why this is a
    /// <see cref="BusinessException"/> and not <see cref="UnauthorizedException"/> (that one emits
    /// <c>{"error":"Unauthorized", …}</c>). Unreachable behind <c>[Authorize]</c>, and kept so a
    /// null user id cannot silently become a filter value.
    /// </summary>
    public static string RequireUserId(ICurrentUser currentUser)
        => string.IsNullOrEmpty(currentUser.UserId)
            ? throw new BusinessException("No token provided", "No token provided", 401)
            : currentUser.UserId;
}

/// <summary>
/// Every error body in appointments.js is a bare <c>{ "error": "…" }</c>, so every failure in
/// this file is a <see cref="BusinessException"/> whose error and message are the SAME string —
/// the middleware drops <c>message</c> when it equals <c>error</c>. <c>ValidationException</c>
/// and <c>ConflictException</c> would prepend <c>"Validation failed"</c> / <c>"Conflict"</c> and
/// push the real text into a second key the client does not read.
/// </summary>
internal static class ApptReadErrors
{
    public static BusinessException Node(string error, int status = 400) => new(error, error, status);
}

/// <summary>
/// The route-level <c>try/catch</c> every Node handler is wrapped in, whose ONLY outcome is that
/// route's own named 500 — <c>{"error":"Failed to list appointments"}</c> (appointments.js:421),
/// <c>"Failed to get today schedule"</c> (:548), <c>"Failed to load queue"</c> (:1313),
/// <c>"Failed to check availability"</c> (:339), <c>"Failed to generate scheduling insights"</c>
/// (:1555).
///
/// <para>Without it a SQL timeout, a directory fault or a serialization failure escapes to
/// ExceptionHandlingMiddleware and renders the generic
/// <c>{"error":"Internal Server Error","message":"An unexpected error occurred"}</c>. The status
/// is 500 either way, but the body differs and the client shows it verbatim — it reads
/// <c>err.response.data.error</c> — so the named body has to be produced here, where the route's
/// message is known. For <c>/today</c> and <c>/queue</c> nothing else throws it at all, so the
/// contract's documented 500 body is otherwise unreachable in the port.</para>
/// </summary>
internal static class ApptReadGuard
{
    /// <summary>
    /// Runs <paramref name="body"/>, converting anything unmodelled into
    /// <paramref name="failureError"/> at 500 after logging it the way Node logs it.
    /// </summary>
    /// <remarks>
    /// An <see cref="AppException"/> passes through untouched, so every modelled 400/403/404 keeps
    /// its own body — including the parse 500s, which are already this shape. A cancellation the
    /// caller triggered also passes through, so a client hanging up is not logged as a fault.
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
            throw ApptReadErrors.Node(failureError, 500);
        }
    }
}

/// <summary>
/// The <c>new Date(string)</c> acceptance set, to the extent these two routes depend on it.
/// <c>DateTime.TryParse</c> is NOT that set in either direction, which is why this exists.
/// </summary>
internal static partial class ApptReadJsDate
{
    /// <summary>
    /// ECMA-262's Date Time String Format, date-only: FOUR-digit year, then a TWO-digit month
    /// 01-12, then a TWO-digit day 01-31. Unpadded components are NOT accepted — the grammar
    /// requires the padding, and V8's legacy fallback parser never sees a string that also
    /// carries a time, so <c>'2026-3-10' + 'T12:00:00'</c> is an Invalid Date.
    /// </summary>
    [GeneratedRegex(@"^(\d{4})-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$")]
    private static partial Regex IsoDateOnly();

    /// <summary>
    /// Parses a bare <c>YYYY-MM-DD</c> the way the JavaScript Date constructor does, INCLUDING
    /// its day overflow: the grammar admits any day up to 31, and <c>MakeDay</c> then rolls the
    /// excess into the following month, so <c>2026-02-30</c> is 2 March 2026 and its weekday is
    /// Monday. Building the value with <c>AddDays</c> from the first of the month reproduces that;
    /// <c>TryParseExact</c> would reject it outright.
    /// </summary>
    /// <returns>
    /// False when the string is not that grammar at all, or when the rolled-over result leaves
    /// the range <see cref="DateTime"/> can hold — the ECMA expanded-year forms
    /// (<c>+002026-03-10</c>, <c>-002026-03-10</c>) and year 0000 among them. Those are the one
    /// residual gap and are recorded in docs/PORT-STATUS.md.
    /// </returns>
    public static bool TryParseIsoDateOnly(string value, out DateTime date)
    {
        date = default;

        var match = IsoDateOnly().Match(value);
        if (!match.Success) return false;

        var year = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture);

        if (year < 1) return false;

        date = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day - 1);

        return true;
    }

    /// <summary>
    /// True for a string <c>DateTime.TryParse</c> reads as a TIME with no date — <c>"13:45"</c>,
    /// <c>"1:45 PM"</c> — which it silently completes with today's date. The JavaScript Date
    /// constructor has no such form: <c>new Date('13:45')</c> is an Invalid Date. Without this
    /// test the port answers 200 with TODAY's appointments where Node answers its own 500.
    /// </summary>
    public static bool IsTimeOnly(string value)
        => !value.Any(c => c is '-' or '/' or '.')
           && value.Contains(':')
           && DateTime.TryParse(
               value, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var probe)
           && probe.Date == default;
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The appointment list, with three role branches decided inside the handler.
///
/// <para><b>Not cached.</b> Node wraps this route in <c>cacheMiddleware(15)</c> whose key is
/// <c>route:{userId}:{originalUrl}</c> and therefore EXCLUDES the <c>X-Clinic-Id</c> header this
/// handler branches on, so a staff user who switches clinics is served the previous clinic's
/// array for up to 15 seconds — and nothing in the router ever invalidates it, so a booking or a
/// confirmation is invisible for the same window. This port answers fresh every time: a fresher
/// response cannot break the client. Deliberate divergence, recorded in docs/PORT-STATUS.md.</para>
/// </summary>
/// <param name="Status">
/// Raw and UNVALIDATED in Node — assigned straight into the Prisma enum filter, so an
/// unrecognised value is rejected by the database and surfaces as
/// <c>500 {"error":"Failed to list appointments"}</c>. There is no 400 on this route.
/// </param>
/// <param name="Date">Any date-parseable string; an unparseable one is also a 500, not a 400.</param>
/// <param name="ClinicId">
/// Doubles as the staff-clinic resolver AND as an extra <c>where</c> filter applied after the
/// branch logic — see the handler's note on the overwrite.
/// </param>
/// <param name="ClinicHeaderId">
/// The RAW <c>X-Clinic-Id</c> header. <see cref="IClinicContext"/> cannot serve this endpoint:
/// it collapses header-then-query into one value, and this handler needs them SEPARATELY (the
/// header wins for the staff check, the query value then overwrites the resulting filter). The
/// controller must bind the header itself and pass it here.
/// </param>
public sealed record ApptReadListQuery(
    string? Status,
    string? Date,
    string? ClinicId,
    string? ClinicHeaderId) : IRequest<IReadOnlyList<object>>;

public sealed class ApptReadListHandler(
    ICurrentUser currentUser,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IAppLogger<ApptReadListHandler> logger)
    : IRequestHandler<ApptReadListQuery, IReadOnlyList<object>>
{
    /// <summary>The silent hard cap at appointments.js:401. The 201st row does not exist to the client.</summary>
    private const int MaxRows = 200;

    public Task<IReadOnlyList<object>> Handle(
        ApptReadListQuery request, CancellationToken cancellationToken = default)
        => ApptReadGuard.RunAsync(
            logger, "[Appointments] List error", "Failed to list appointments", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<object>> HandleCore(
        ApptReadListQuery request, CancellationToken cancellationToken)
    {
        var userId = ApptReadCaller.RequireUserId(currentUser);

        string? filterPhysicianId = null;
        string? filterClinicId = null;
        string? filterPatientUserId = null;

        // The fork is on the userType CLAIM, not on the database, so a userType changed in the DB
        // does not take effect until the token is reissued (appointments.js:355).
        if (currentUser.UserType == "PHYSICIAN")
        {
            var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

            // A PHYSICIAN with no profile row gets 200 [] — not 404, not 403, and no query runs
            // (appointments.js:362). One of the four different answers this module gives to
            // "no physician profile"; do not harmonise them.
            if (physician is null) return [];

            filterPhysicianId = physician.Id;
        }
        else
        {
            // The lowercased header wins over ?clinicId (appointments.js:366).
            var staffClinicId = ApptReadFilters.Truthy(request.ClinicHeaderId)
                                ?? ApptReadFilters.Truthy(request.ClinicId);

            if (staffClinicId is not null
                && await clinics.IsActiveStaffAsync(userId, staffClinicId, cancellationToken))
            {
                filterClinicId = staffClinicId;
            }
            else
            {
                // A non-physician who sends a clinic id but holds no active staff row silently
                // falls back to their OWN bookings — a 200 with their (usually empty) list,
                // never a 403 (appointments.js:374).
                filterPatientUserId = userId;
            }
        }

        var status = ParseStatus(request.Status);

        // AUTHORIZATION DEFECT, REPRODUCED. appointments.js:382 is an unconditional
        // `if (clinicId) where.clinicId = clinicId`, which OVERWRITES the staff-validated clinic
        // set a few lines earlier. A user with an active staff row at clinic A who sends
        // `X-Clinic-Id: A` and `?clinicId=B` passes the check on A and then receives clinic B's
        // appointments, patient-enriched and never re-validated. It also ADDS a clinic filter to
        // the physician branch. Flagged in docs/PORT-STATUS.md rather than fixed.
        if (ApptReadFilters.Truthy(request.ClinicId) is { } queryClinicId)
            filterClinicId = queryClinicId;

        var date = ParseDate(request.Date);

        var filter = new AppointmentFilter(
            ClinicId: filterClinicId,
            PhysicianId: filterPhysicianId,
            PatientUserId: filterPatientUserId,
            Status: status,
            Date: date);

        // Ordered by appointmentDate ONLY — startTime is not a tiebreaker here, unlike /today and
        // /queue, so same-day rows come back in database order (appointments.js:400).
        var rows = await appointments.ListAsync(filter, MaxRows, cancellationToken);

        if (rows.Count == 0) return [];

        var relations = await LoadRelationsAsync(rows, cancellationToken);

        // RESPONSE-SHAPE FORK (appointments.js:405). The enrichment condition is
        // `userType === 'PHYSICIAN' || where.clinicId`, and where.clinicId is set by the
        // overwrite above regardless of staff membership — so a plain PATIENT who passes
        // ?clinicId gets their own name and phone echoed back, and the `patientUser` KEY.
        var enrich = currentUser.UserType == "PHYSICIAN" || filterClinicId is not null;

        if (!enrich)
        {
            return
            [
                .. rows.Select(a => (object)AppointmentReadMapper.ToListPatientItem(
                    a,
                    relations.Clinics.GetValueOrDefault(a.ClinicId),
                    relations.Physicians.GetValueOrDefault(a.PhysicianId),
                    Subprofile(relations, a)))
            ];
        }

        string[] patientIds =
        [
            .. rows.Select(a => a.PatientUserId).Where(id => !string.IsNullOrEmpty(id)).Distinct()
        ];

        // One batched lookup, which is what Node does too (appointments.js:408) — this route is
        // the one place in the router that already avoids the N+1.
        var patientUsers = patientIds.Length == 0
            ? new Dictionary<string, UserSummary>(0)
            : await identity.GetUsersAsync(patientIds, cancellationToken);

        return
        [
            .. rows.Select(a => (object)AppointmentReadMapper.ToListItem(
                a,
                relations.Clinics.GetValueOrDefault(a.ClinicId),
                relations.Physicians.GetValueOrDefault(a.PhysicianId),
                Subprofile(relations, a),
                // `patientMap[a.patientUserId] || null`: a dangling id yields null and the row
                // still appears. There is no FK on patientUserId, so this happens.
                patientUsers.GetValueOrDefault(a.PatientUserId)))
        ];
    }

    private static SubprofileSummary? Subprofile(ApptReadListRelations relations, Appointment a)
        => a.SubprofileId is null ? null : relations.Subprofiles.GetValueOrDefault(a.SubprofileId);

    /// <summary>
    /// The three Prisma <c>include</c> relations, resolved once for the whole array. The calls are
    /// SEQUENTIAL on purpose: <see cref="IClinicDirectory"/> and the other directories are served
    /// by their own modules' request-scoped DbContexts, and EF Core throws on two concurrent
    /// operations against one context.
    /// </summary>
    private async Task<ApptReadListRelations> LoadRelationsAsync(
        IReadOnlyList<Appointment> rows, CancellationToken ct)
    {
        string[] clinicIds = [.. rows.Select(a => a.ClinicId).Distinct()];
        string[] physicianIds = [.. rows.Select(a => a.PhysicianId).Distinct()];
        string[] subprofileIds =
        [
            .. rows.Select(a => a.SubprofileId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Select(id => id!)
                   .Distinct()
        ];

        var resolvedClinics = await clinics.GetClinicsAsync(clinicIds, ct);
        var resolvedPhysicians = await identity.GetPhysiciansAsync(physicianIds, ct);

        var resolvedSubprofiles = subprofileIds.Length == 0
            ? new Dictionary<string, SubprofileSummary>(0)
            : await patients.GetSubprofilesAsync(subprofileIds, ct);

        return new ApptReadListRelations(resolvedClinics, resolvedPhysicians, resolvedSubprofiles);
    }

    /// <summary>
    /// appointments.js:381 assigns <c>?status</c> straight into the Prisma enum filter, so an
    /// unrecognised value is a database rejection caught by the outer handler —
    /// <c>500 {"error":"Failed to list appointments"}</c>, never a 400.
    /// </summary>
    private static AppointmentStatus? ParseStatus(string? status) => status switch
    {
        null or "" => null,
        "PENDING" => AppointmentStatus.PENDING,
        "CONFIRMED" => AppointmentStatus.CONFIRMED,
        "COMPLETED" => AppointmentStatus.COMPLETED,
        "CANCELLED" => AppointmentStatus.CANCELLED,
        "NO_SHOW" => AppointmentStatus.NO_SHOW,
        _ => throw ApptReadErrors.Node("Failed to list appointments", 500)
    };

    /// <summary>
    /// Node builds the window as <c>new Date(date)</c> — UTC midnight for a bare
    /// <c>YYYY-MM-DD</c> — then <c>setHours(0,0,0,0)</c> / <c>(23,59,59,999)</c> in SERVER-LOCAL
    /// time, which shifts the window to the neighbouring calendar day on any server whose offset
    /// is not 0. The store matches a whole calendar day instead, and the value is parsed as UTC so
    /// a bare date means that date rather than an offset-dependent one. Divergence recorded in
    /// docs/PORT-STATUS.md; an unparseable value stays a 500, exactly as in Node.
    /// </summary>
    private static DateTime? ParseDate(string? date)
    {
        if (string.IsNullOrEmpty(date)) return null;

        // The ECMA grammar first, with its day rollover, so a bare YYYY-MM-DD means that
        // calendar date and '2026-02-30' means 2 March — neither of which TryParse delivers.
        if (ApptReadJsDate.TryParseIsoDateOnly(date, out var iso)) return iso.Date;

        // `new Date('13:45')` is an Invalid Date; TryParse would return TODAY at 13:45 and the
        // route would answer 200 with today's appointments instead of Node's own 500.
        if (ApptReadJsDate.IsTimeOnly(date))
            throw ApptReadErrors.Node("Failed to list appointments", 500);

        // Everything else falls to the loose forms — 'March 10 2026', '2026/03/10', a full
        // timestamp — which V8's legacy parser also broadly accepts.
        //
        // RESIDUAL GAP, recorded in docs/PORT-STATUS.md: V8's legacy parser is wider still at the
        // degenerate end, where it invents components. `?date=2026` is 1 Jan 2026 to it and
        // `?date=10` is October 2001; both stay a 500 here rather than a 200 over some window the
        // caller plainly did not ask for.
        return DateTime.TryParse(
            date,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.Date
            : throw ApptReadErrors.Node("Failed to list appointments", 500);
    }
}

/// <summary>The three <c>include</c> relations of <c>GET /api/appointments</c>, batched per response.</summary>
internal sealed record ApptReadListRelations(
    IReadOnlyDictionary<string, ClinicSummary> Clinics,
    IReadOnlyDictionary<string, PhysicianSummary> Physicians,
    IReadOnlyDictionary<string, SubprofileSummary> Subprofiles);

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/today
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The physician's own schedule for today, as a BARE ARRAY.
///
/// <para>"Physician-only" by SILENT FALLTHROUGH: the gate is the existence of a PhysicianProfile
/// for the caller, not a userType check, and there is no 403 and no 404 anywhere on this route —
/// a patient or a receptionist gets <c>200 []</c> (appointments.js:515).</para>
///
/// <para><b>Not cached</b>, for the same reason as <c>GET /</c>: Node's <c>cacheMiddleware(15)</c>
/// has no invalidation, so a check-in or a confirmation is invisible to <c>/today</c> for up to
/// 15 seconds. This port answers fresh. See docs/PORT-STATUS.md.</para>
/// </summary>
public sealed record ApptReadTodayQuery : IRequest<IReadOnlyList<ApptReadTodayItem>>;

public sealed class ApptReadTodayHandler(
    ICurrentUser currentUser,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IAppLogger<ApptReadTodayHandler> logger)
    : IRequestHandler<ApptReadTodayQuery, IReadOnlyList<ApptReadTodayItem>>
{
    /// <summary>
    /// appointments.js:525. Only these two hold a place in today's list — COMPLETED, CANCELLED and
    /// NO_SHOW rows vanish from it once acted on, which is the opposite of <c>GET /queue</c>.
    /// </summary>
    private static readonly AppointmentStatus[] Statuses =
    [
        AppointmentStatus.PENDING,
        AppointmentStatus.CONFIRMED
    ];

    public Task<IReadOnlyList<ApptReadTodayItem>> Handle(
        ApptReadTodayQuery request, CancellationToken cancellationToken = default)
        => ApptReadGuard.RunAsync(
            logger, "[Appointments] Today error", "Failed to get today schedule", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<ApptReadTodayItem>> HandleCore(
        ApptReadTodayQuery request, CancellationToken cancellationToken)
    {
        var userId = ApptReadCaller.RequireUserId(currentUser);

        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);
        if (physician is null) return [];

        // Node's day is the SERVER's timezone day (`new Date(y, m, d)`), not the physician's and
        // not the client's. Appointment dates are stored as UTC midnight, so the window is built
        // in UTC here — mixing a local-midnight bound with UTC-stored dates would misclassify
        // every row by the server's offset. Same choice as the Visits module.
        var today = DateTime.UtcNow.Date;

        // Ordered by the startTime STRING (appointments.js:531), so an unpadded "9:00" sorts
        // after "10:00". The store owns that ordering.
        var rows = await appointments.ListForDayAsync(
            clinicId: null,
            physicianId: physician.Id,
            date: today,
            statuses: Statuses,
            ct: cancellationToken);

        if (rows.Count == 0) return [];

        string[] clinicIds = [.. rows.Select(a => a.ClinicId).Distinct()];
        string[] subprofileIds =
        [
            .. rows.Select(a => a.SubprofileId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Select(id => id!)
                   .Distinct()
        ];
        string[] patientIds =
        [
            .. rows.Select(a => a.PatientUserId).Where(id => !string.IsNullOrEmpty(id)).Distinct()
        ];

        var resolvedClinics = await clinics.GetClinicsAsync(clinicIds, cancellationToken);

        // The `subprofile` include (appointments.js:529) that the chunk file omits: without it the
        // client loses the family-member label it renders for a child's appointment.
        var resolvedSubprofiles = subprofileIds.Length == 0
            ? new Dictionary<string, SubprofileSummary>(0)
            : await patients.GetSubprofilesAsync(subprofileIds, cancellationToken);

        var resolvedPatients = patientIds.Length == 0
            ? new Dictionary<string, UserSummary>(0)
            : await identity.GetUsersAsync(patientIds, cancellationToken);

        return
        [
            .. rows.Select(a => AppointmentReadMapper.ToTodayItem(
                a,
                resolvedClinics.GetValueOrDefault(a.ClinicId),
                a.SubprofileId is null ? null : resolvedSubprofiles.GetValueOrDefault(a.SubprofileId),
                // null for a deleted patient account; the array element still appears.
                resolvedPatients.GetValueOrDefault(a.PatientUserId)))
        ];
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/queue
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Today's clinic-wide patient queue for staff and assistants.
///
/// <para><b>This endpoint does NOT read the waiting-room module.</b> The brief assumed it did;
/// appointments.js:1236-1310 shows it does not. It queries the clinic's own appointments for the
/// day, derives <c>checkedIn</c> / <c>checkedInAt</c> / <c>room</c> by parsing the
/// <c>CHECKIN:</c> marker out of <c>appointments.notes</c>, and overlays the day's visits. Every
/// one of those sources exists here, so the response is fully real data with no fabrication and
/// no no-op involved — <see cref="IWaitingQueueWriter"/> deliberately has no read method. What
/// IS lost while that writer is a no-op is the physician's separate waiting-room screen, which
/// this endpoint never consulted.</para>
///
/// <para>Not cached in Node either — fresh on every call, unlike <c>GET /</c> and
/// <c>GET /today</c>.</para>
/// </summary>
/// <param name="ClinicId">Required: a falsy value is <c>400 {"error":"clinicId required"}</c>.</param>
public sealed record ApptReadQueueQuery(string? ClinicId) : IRequest<ApptReadQueueResponse>;

public sealed class ApptReadQueueHandler(
    ICurrentUser currentUser,
    IAppointmentStore appointments,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IVisitDirectory visits,
    IAppLogger<ApptReadQueueHandler> logger)
    : IRequestHandler<ApptReadQueueQuery, ApptReadQueueResponse>
{
    /// <summary>
    /// appointments.js:1240 — everything except CANCELLED, so cancelled appointments disappear
    /// from the queue and from every counter.
    /// </summary>
    private static readonly AppointmentStatus[] Statuses =
    [
        AppointmentStatus.PENDING,
        AppointmentStatus.CONFIRMED,
        AppointmentStatus.COMPLETED,
        AppointmentStatus.NO_SHOW
    ];

    public Task<ApptReadQueueResponse> Handle(
        ApptReadQueueQuery request, CancellationToken cancellationToken = default)
        => ApptReadGuard.RunAsync(
            logger, "[Appointments] Queue error", "Failed to load queue", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ApptReadQueueResponse> HandleCore(
        ApptReadQueueQuery request, CancellationToken cancellationToken)
    {
        var userId = ApptReadCaller.RequireUserId(currentUser);

        var clinicId = ApptReadFilters.Truthy(request.ClinicId)
                       ?? throw ApptReadErrors.Node("clinicId required");

        // The physician branch is clinic OWNERSHIP, not membership: Node includes the profile's
        // `clinics` relation filtered to this id and tests `clinics.length > 0`
        // (appointments.js:1218-1222). A physician who merely works at the clinic falls through to
        // the staff check and gets 403 unless they also hold an active staff row. Reproduced, not
        // fixed (docs/PORT-STATUS.md).
        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

        var isPhysician = physician is not null
                          && await clinics.IsOwningPhysicianAsync(
                              clinicId, physician.Id, cancellationToken);

        // Node only runs the staff query when the physician branch failed.
        var isStaff = !isPhysician
                      && await clinics.IsActiveStaffAsync(userId, clinicId, cancellationToken);

        if (!isPhysician && !isStaff) throw new ForbiddenException("Not staff at this clinic");

        var today = DateTime.UtcNow.Date;

        // NO physicianId filter (appointments.js:1236-1241): every physician's appointments at
        // this clinic are returned to any staff member. Ordered by the startTime string.
        var rows = await appointments.ListForDayAsync(
            clinicId: clinicId,
            physicianId: null,
            date: today,
            statuses: Statuses,
            ct: cancellationToken);

        if (rows.Count == 0)
        {
            return new ApptReadQueueResponse([], new ApptReadQueueSummary(0, 0, 0, 0, 0));
        }

        string[] physicianIds = [.. rows.Select(a => a.PhysicianId).Distinct()];
        string[] subprofileIds =
        [
            .. rows.Select(a => a.SubprofileId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Select(id => id!)
                   .Distinct()
        ];
        string[] patientIds =
        [
            .. rows.Select(a => a.PatientUserId).Where(id => !string.IsNullOrEmpty(id)).Distinct()
        ];

        // The FULL profile, because the Node include has no `select` — see
        // ApptReadQueuePhysician for what that exposes.
        var resolvedPhysicians = await identity.GetPhysicianDetailsAsync(
            physicianIds, cancellationToken);

        var resolvedSubprofiles = subprofileIds.Length == 0
            ? new Dictionary<string, SubprofileSummary>(0)
            : await patients.GetSubprofilesAsync(subprofileIds, cancellationToken);

        // Node issues one findUnique per appointment inside Promise.all (appointments.js:1249).
        // Batched here; the row ORDER is unaffected, so output order still follows the startTime
        // sort, which is what the client renders.
        var resolvedPatients = patientIds.Length == 0
            ? new Dictionary<string, UserSummary>(0)
            : await identity.GetUsersAsync(patientIds, cancellationToken);

        var dayVisits = await visits.ListForClinicDayAsync(
            clinicId, today, today.AddDays(1), cancellationToken);

        // Keyed by patientUserId ONLY, LAST row wins (appointments.js:1284-1287). The overlay is
        // not scoped to the appointment and applies no status, physician or soft-delete filter, so
        // a walk-in visit attaches to an unrelated appointment of the same patient, and a
        // cancelled or archived visit still overlays. In Node the winner is non-deterministic
        // because that findMany has no orderBy; the port's directory orders by visitDate, which
        // makes "last wins" mean the day's latest visit.
        var visitByPatient = new Dictionary<string, VisitQueueRow>(dayVisits.Count);
        foreach (var visit in dayVisits) visitByPatient[visit.PatientUserId] = visit;

        List<ApptReadQueueItem> queue = new(rows.Count);

        foreach (var a in rows)
        {
            var (checkedIn, checkedInAt, room) = ParseCheckIn(a.Notes);

            queue.Add(AppointmentReadMapper.ToQueueItem(
                a,
                a.SubprofileId is null ? null : resolvedSubprofiles.GetValueOrDefault(a.SubprofileId),
                resolvedPhysicians.GetValueOrDefault(a.PhysicianId),
                resolvedPatients.GetValueOrDefault(a.PatientUserId),
                checkedIn,
                checkedInAt,
                room,
                visitByPatient.GetValueOrDefault(a.PatientUserId)));
        }

        // The predicates are copied literally from appointments.js:1302-1308, overlapping buckets
        // and all: `pending` counts a NO_SHOW that was never checked in, so that row is in BOTH
        // `pending` and `noShow`, and total != arrived + pending + completed.
        var summary = new ApptReadQueueSummary(
            Total: queue.Count,
            Arrived: queue.Count(q => q.CheckedIn),
            Pending: queue.Count(q => !q.CheckedIn && q.Status != nameof(AppointmentStatus.COMPLETED)),
            Completed: queue.Count(q => q.Status == nameof(AppointmentStatus.COMPLETED)),
            NoShow: queue.Count(q => q.Status == nameof(AppointmentStatus.NO_SHOW)));

        return new ApptReadQueueResponse(queue, summary);
    }

    /// <summary>
    /// Recovers the check-in state that <c>PUT /{id}/check-in</c> smuggled into the free-text
    /// <c>notes</c> column (appointments.js:1260-1268).
    ///
    /// <para>The trigger is a plain <c>notes.includes('CHECKIN:')</c> and the payload is
    /// <c>notes.split('CHECKIN:').pop()</c> — everything after the LAST marker, so on a repeat
    /// check-in the newest wins. Both timestamps and the room are lifted out as RAW JSON values:
    /// <c>checkedInAt</c> was written by the check-in route as <c>new Date().toISOString()</c> and
    /// is never re-serialized here, so its format need not match the Date-derived ISO fields
    /// beside it, and a numeric <c>room</c> stays numeric.</para>
    ///
    /// <para>The partial-success path is exact: when the payload is not valid JSON, the Node catch
    /// leaves <c>checkedIn = true</c> with <c>checkedInAt</c> and <c>room</c> still null — "checked
    /// in at an unknown time".</para>
    ///
    /// <para>One micro-divergence, deliberately not chased: if the payload parses to a JSON
    /// primitive or array, Node reads <c>undefined</c> for both fields and <c>JSON.stringify</c>
    /// then DROPS the two keys, whereas this port emits them as null. Reproducing that would need
    /// a second 30-property row record, and reaching it needs hand-edited notes.</para>
    /// </summary>
    private static (bool CheckedIn, JsonNode? CheckedInAt, JsonNode? Room) ParseCheckIn(string? notes)
    {
        if (notes is null || !notes.Contains(Appointment.CheckInMarker, StringComparison.Ordinal))
            return (false, null, null);

        var start = notes.LastIndexOf(Appointment.CheckInMarker, StringComparison.Ordinal)
                    + Appointment.CheckInMarker.Length;

        try
        {
            // JSON.parse rejects trailing content, and so does JsonNode.Parse.
            if (JsonNode.Parse(notes[start..]) is JsonObject payload)
            {
                return (true,
                        payload["checkedInAt"]?.DeepClone(),
                        payload["room"]?.DeepClone());
            }

            // JSON.parse("null") makes the property read throw, which lands in the same catch.
            return (true, null, null);
        }
        catch (JsonException)
        {
            return (true, null, null);
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/available
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A physician's recurring TimeSlot template for the weekday of a given date, each row flagged
/// <c>isBooked</c>, as a BARE ARRAY (never null).
///
/// <para>It does NOT expand a working window into concrete slots and it does NOT drop booked
/// ones: appointments.js:301-334 reads the stored weekly <c>time_slots</c> rows for that weekday,
/// de-duplicates them by <c>startTime</c>, and appends a boolean. There is no time arithmetic on
/// the wire at all.</para>
///
/// <para><b>Both "not allowed" outcomes answer <c>200 []</c></b> rather than an error, and the
/// client relies on it — it just renders "no slots". They are: no PhysicianProfile for the
/// requested <c>?physicianUserId</c>, and a booking-disabled clinic whose slots the caller may
/// not see. Only the missing-parameter case is a 400, and only a malformed date is a 500.</para>
/// </summary>
/// <param name="PhysicianUserId">
/// The USER id, not the PhysicianProfile id. Required.
/// </param>
/// <param name="ClinicId">
/// Optional. When absent, slots from ALL of the physician's clinics are merged — AND the
/// <c>allowPatientBooking</c> gate is skipped entirely, so a booking-disabled clinic's slots go
/// to any authenticated caller. That hole is the contract.
/// </param>
/// <param name="Date">
/// <c>YYYY-MM-DD</c>. Required. A malformed value is <c>500 {"error":"Failed to check
/// availability"}</c> in Node, not a 400.
/// </param>
public sealed record ApptReadAvailableQuery(
    string? PhysicianUserId,
    string? ClinicId,
    string? Date) : IRequest<IReadOnlyList<ApptReadAvailableSlot>>;

public sealed class ApptReadAvailableHandler(
    ICurrentUser currentUser,
    IAppointmentStore appointments,
    ITimeSlotStore timeSlots,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IAppLogger<ApptReadAvailableHandler> logger)
    : IRequestHandler<ApptReadAvailableQuery, IReadOnlyList<ApptReadAvailableSlot>>
{
    public Task<IReadOnlyList<ApptReadAvailableSlot>> Handle(
        ApptReadAvailableQuery request, CancellationToken cancellationToken = default)
        => ApptReadGuard.RunAsync(
            logger, "[Appointments] Available error", "Failed to check availability", cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<ApptReadAvailableSlot>> HandleCore(
        ApptReadAvailableQuery request, CancellationToken cancellationToken)
    {
        var userId = ApptReadCaller.RequireUserId(currentUser);

        var physicianUserId = ApptReadFilters.Truthy(request.PhysicianUserId);
        var rawDate = ApptReadFilters.Truthy(request.Date);

        // Both guards are one JS truthiness test on the pair (appointments.js:262).
        if (physicianUserId is null || rawDate is null)
            throw ApptReadErrors.Node("physicianUserId and date are required");

        var physician = await identity.GetPhysicianByUserIdAsync(physicianUserId, cancellationToken);

        // SILENT 200 []: no profile for the requested physician is not a 404.
        if (physician is null) return [];

        var clinicId = ApptReadFilters.Truthy(request.ClinicId);

        if (clinicId is not null)
        {
            var clinic = await clinics.GetClinicAsync(clinicId, cancellationToken);

            // `if (clinic && !clinic.allowPatientBooking)`: a clinic that cannot be read at all
            // skips the gate rather than failing it (appointments.js:277).
            if (clinic is not null && !clinic.AllowPatientBooking)
            {
                // The owner test compares the CALLER's user id to the query string, not to the
                // resolved profile (appointments.js:280) — a string compare against untrusted
                // input, reproduced as written.
                var isOwner = userId == physicianUserId;

                var isStaff = !isOwner
                              && await clinics.IsActiveStaffAsync(
                                  userId, clinicId, cancellationToken);

                // SILENT 200 [] again — never a 403.
                if (!isOwner && !isStaff) return [];
            }
        }

        // `new Date(date + 'T12:00:00')` (appointments.js:294). The concatenated time is what
        // narrows the accepted input to a BARE `YYYY-MM-DD`: it forces V8 down the ECMA grammar,
        // where an unpadded '2026-3-10' and an already-timestamped '2026-03-10T12:00:00' are both
        // Invalid Dates, while an out-of-range day such as '2026-02-30' rolls over into March and
        // legitimately answers with THAT weekday's slots.
        //
        // Local NOON is deliberate on Node's side, to dodge DST and midnight rollover; the
        // weekday of a bare date is the same in every timezone once noon is used, so the plain
        // calendar date gives the identical dayOfWeek here. JavaScript's getDay() is 0 = Sunday
        // and .NET's DayOfWeek agrees member for member.
        if (!ApptReadJsDate.TryParseIsoDateOnly(rawDate, out var targetDate))
        {
            // Node reaches Prisma with dayOfWeek: NaN and answers 500, not 400.
            throw ApptReadErrors.Node("Failed to check availability", 500);
        }

        var dayOfWeek = (int)targetDate.DayOfWeek;

        var slots = await timeSlots.ListActiveForDayOfWeekAsync(
            physician.Id, dayOfWeek, clinicId, cancellationToken);

        if (slots.Count == 0) return [];

        // Booked-detection is by exact startTime STRING equality (appointments.js:320): endTime is
        // read from the database and never used, so an overlapping-but-not-identical booking does
        // NOT mark a slot booked, and "9:00" against a stored "09:00" reads as free. The query is
        // scoped to the physician and the day but NOT to the clinic, so a booking at the
        // physician's other clinic marks this clinic's slot booked — and only PENDING and
        // CONFIRMED hold a slot, so completing or cancelling frees it.
        var booked = await appointments.ListBookedAsync(
            physician.Id, targetDate.Date, cancellationToken);

        var bookedTimes = new HashSet<string>(booked.Select(b => b.StartTime), StringComparer.Ordinal);

        // De-duplication keeps the FIRST row per startTime in the store's startTime order. With no
        // ?clinicId that collapses same-time slots ACROSS clinics, so the surviving row's
        // clinicId and clinic name are arbitrary among ties.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<TimeSlot> unique = new(slots.Count);

        foreach (var slot in slots)
        {
            if (seen.Add(slot.StartTime)) unique.Add(slot);
        }

        string[] clinicIds = [.. unique.Select(s => s.ClinicId).Distinct()];
        var resolvedClinics = await clinics.GetClinicsAsync(clinicIds, cancellationToken);

        return
        [
            .. unique.Select(s => AppointmentReadMapper.ToAvailableSlot(
                s,
                resolvedClinics.GetValueOrDefault(s.ClinicId),
                bookedTimes.Contains(s.StartTime)))
        ];
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/appointments/ai-optimize
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Scheduling analytics over the physician's last 90 days, plus up to six AI insights derived
/// from them.
///
/// <para>The only GET in this router that 403s: a caller with no PhysicianProfile gets
/// <c>403 {"error":"Only physicians can access scheduling insights"}</c> where <c>GET /</c>,
/// <c>GET /today</c>, <c>GET /slots</c> and <c>GET /available</c> all answer <c>200 []</c> for
/// the same caller. Harmonising them would break one client or the other.</para>
///
/// <para>Two envelopes, and the difference is in the KEYS PRESENT, not in null values: under five
/// appointments in the window it answers <c>{ stats: null, insights: [], message }</c> with no
/// <c>aiProvider</c> key; otherwise <c>{ stats, insights, aiProvider }</c> with no
/// <c>message</c>. Hence <c>IRequest&lt;object&gt;</c>.</para>
///
/// <para>Stage 1 is pure computation over the rows and is the only part that can 500. Every AI
/// failure is swallowed into <c>insights: []</c> with the statistics intact and a 200.</para>
/// </summary>
public sealed record ApptReadOptimizeQuery : IRequest<object>;

public sealed class ApptReadOptimizeHandler(
    ICurrentUser currentUser,
    IAppointmentStore appointments,
    ITimeSlotStore timeSlots,
    IIdentityDirectory identity,
    IVisitDirectory visits,
    IAiGateway ai,
    IAppLogger<ApptReadOptimizeHandler> logger)
    : IRequestHandler<ApptReadOptimizeQuery, object>
{
    private const int WindowDays = 90;

    /// <summary>Fewer than this many rows in the window and the endpoint declines to analyse.</summary>
    private const int MinimumAppointments = 5;

    /// <summary>Hard-coded English, Sunday-first, matching appointments.js:1399. No localization.</summary>
    private static readonly string[] DayNames =
    [
        "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"
    ];

    private static readonly string[] InsightTypes =
    [
        "NO_SHOW", "SLOT_DURATION", "PEAK_HOURS", "UTILIZATION", "CANCELLATION", "THROUGHPUT"
    ];

    private static readonly string[] InsightImpacts = ["HIGH", "MODERATE", "LOW"];

    private static readonly Regex JsonFence =
        new("```json\n?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex PlainFence =
        new("```\n?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The statistics reach the model as <c>JSON.stringify(stats, null, 2)</c>, so the prompt —
    /// and therefore the gateway's prompt-cache key — depends on camelCase keys and two-space
    /// indentation. The default encoder is left in place because every value in <c>stats</c> is
    /// ASCII, so nothing gets escaped that JSON.stringify would not escape.
    /// </summary>
    private static readonly JsonSerializerOptions PromptJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Verbatim from appointments.js:1493-1512 — the model's behaviour depends on it.</summary>
    private const string SystemPrompt = """
        You are a clinic scheduling optimization consultant. Analyze the appointment statistics below and provide actionable scheduling insights.

        For each insight, provide a JSON object with:
        - type: one of "NO_SHOW", "SLOT_DURATION", "PEAK_HOURS", "UTILIZATION", "CANCELLATION", "THROUGHPUT"
        - title: Short title (max 80 chars)
        - description: Actionable recommendation (2-3 sentences max). Include specific numbers from the data.
        - impact: "HIGH", "MODERATE", or "LOW"

        Rules:
        - Generate 3-5 insights, ordered by impact (highest first)
        - Focus on actionable changes the physician can make TODAY
        - If no-show rate > 15%, this is HIGH impact
        - If avg visit duration is significantly shorter than slot duration, suggest adjusting
        - If utilization < 60%, suggest marketing/outreach or slot consolidation
        - If one day has significantly higher no-show rate, call it out specifically
        - Use the day-by-day and hour-by-hour data to find patterns
        - Be specific with numbers: "22% no-show rate on Wednesdays" not "high no-show rate"
        - Don't suggest things that require technology changes — focus on schedule adjustments

        Respond ONLY with a JSON array of insight objects. No markdown, no explanation.
        """;

    public Task<object> Handle(
        ApptReadOptimizeQuery request, CancellationToken cancellationToken = default)
        => ApptReadGuard.RunAsync(
            logger, "[Appointments] AI optimize error", "Failed to generate scheduling insights",
            cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<object> HandleCore(
        ApptReadOptimizeQuery request, CancellationToken cancellationToken)
    {
        var userId = ApptReadCaller.RequireUserId(currentUser);

        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

        if (physician is null)
            throw new ForbiddenException("Only physicians can access scheduling insights");

        // `setDate(getDate() - 90)` KEEPS THE CURRENT TIME OF DAY, so the window slides through
        // the day rather than snapping to midnight — and there is NO UPPER BOUND, so FUTURE
        // appointments are counted in every metric. "Last 90 days" in the message and the prompt
        // is a lie, and noShowRate is diluted by bookings that have not happened yet. Both are
        // the contract (appointments.js:1339-1346).
        var since = DateTime.UtcNow.AddDays(-WindowDays);

        // No status filter and no clinic filter: cancellations and no-shows are exactly what is
        // being analysed, and soft-deleted rows are not excluded either.
        var rows = await appointments.ListForPhysicianSinceAsync(
            physician.Id, since, cancellationToken);

        // The threshold counts ALL statuses, cancelled and future ones included.
        if (rows.Count < MinimumAppointments)
        {
            return new ApptReadOptimizeNoDataResponse(
                Stats: null,
                Insights: [],
                Message: "Not enough appointment data for analysis. "
                         + "Need at least 5 appointments in the last 90 days.");
        }

        var completedVisits = await visits.ListCompletedDurationsAsync(
            physician.Id, since, cancellationToken);

        // No clinic scoping (appointments.js:1382-1385): a physician with several clinics has
        // their capacity and duration averages pooled across all of them.
        var activeSlots = await timeSlots.ListActiveAsync(
            physician.Id, clinicId: null, ct: cancellationToken);

        var stats = BuildStats(rows, completedVisits, activeSlots);

        var insights = new List<ApptReadOptimizeInsight>();
        string? aiProvider = null;

        try
        {
            var result = await ai.ChatAsync(
                new AiChatRequest(
                    System: SystemPrompt,
                    User: "SCHEDULING STATISTICS (Last 90 days):\n"
                          + JsonSerializer.Serialize(stats, PromptJson),
                    Temperature: 0.2,
                    MaxTokens: 1200,
                    UseCache: true,
                    Agent: "scheduler",
                    UserId: userId),
                cancellationToken);

            // ASSIGNED BEFORE THE PARSE (appointments.js:1522), which is what makes the two
            // failure modes distinguishable: a call that succeeded but returned unparseable text
            // reports the REAL provider beside an empty insights array, while a call that threw
            // reports aiProvider: null.
            aiProvider = result.Provider;

            insights.AddRange(ParseInsights(result.Text));
        }
        // The Node catch is bare and swallows everything, including the lazy `require` of the
        // gateway. With no provider configured this is the path that actually runs, so the
        // endpoint's real answer today is the statistics with insights: [] and aiProvider: null.
        catch (Exception aiError) when (aiError is not OperationCanceledException)
        {
            logger.Error("AI optimize error", aiError, new { UserId = userId });
        }

        return new ApptReadOptimizeResponse(stats, insights, aiProvider);
    }

    // ── Stage 1: the statistics ──────────────────────────────────────────────

    /// <summary>
    /// appointments.js:1389-1482, key order and arithmetic preserved literally. Only
    /// <c>status</c>, <c>appointmentDate</c>, <c>startTime</c> and <c>appointmentType</c> are
    /// used; the other five columns Node selects feed no metric.
    /// </summary>
    private static ApptReadOptimizeStats BuildStats(
        IReadOnlyList<Appointment> rows,
        IReadOnlyList<VisitDuration> completedVisits,
        IReadOnlyList<TimeSlot> activeSlots)
    {
        var totalAppts = rows.Count;
        var noShows = rows.Count(a => a.Status == AppointmentStatus.NO_SHOW);
        var cancelled = rows.Count(a => a.Status == AppointmentStatus.CANCELLED);
        var completed = rows.Count(a => a.Status == AppointmentStatus.COMPLETED);

        var noShowRate = totalAppts > 0 ? Fixed3((double)noShows / totalAppts) : 0;
        var cancelRate = totalAppts > 0 ? Fixed3((double)cancelled / totalAppts) : 0;
        var completionRate = totalAppts > 0 ? Fixed3((double)completed / totalAppts) : 0;

        // byDay's keys are NUMBERS in Node, and V8 enumerates integer-like keys in ASCENDING
        // order — so the intermediate is built ordered by dayIndex and only then stable-sorted by
        // total descending, which leaves ties in ascending dayIndex order. A plain Dictionary
        // gives no such guarantee, hence the explicit OrderBy.
        var byDay = new Dictionary<int, DayBucket>();

        foreach (var a in rows)
        {
            // Node reads getDay() in SERVER-LOCAL time; appointment dates are stored as UTC
            // midnight, so the weekday is taken from the stored value directly.
            var day = (int)a.AppointmentDate.DayOfWeek;

            var bucket = byDay.GetValueOrDefault(day);
            byDay[day] = new DayBucket(
                bucket.Total + 1,
                bucket.NoShow + (a.Status == AppointmentStatus.NO_SHOW ? 1 : 0),
                bucket.Cancelled + (a.Status == AppointmentStatus.CANCELLED ? 1 : 0));
        }

        IReadOnlyList<ApptReadOptimizeDayStat> dayStats =
        [
            .. byDay.OrderBy(e => e.Key)
                    .Select(e => new ApptReadOptimizeDayStat(
                        DayNames[e.Key],
                        e.Key,
                        e.Value.Total,
                        e.Value.Total > 0 ? Fixed3((double)e.Value.NoShow / e.Value.Total) : 0,
                        e.Value.Total > 0 ? Fixed3((double)e.Value.Cancelled / e.Value.Total) : 0))
                    // Stable, so equal totals keep the ascending dayIndex order above.
                    .OrderByDescending(d => d.Total)
        ];

        // byHour's keys are the hour STRINGS, and "09" is not a canonical array index, so V8
        // enumerates unpadded keys like "10" first in numeric order and leaves "09" in insertion
        // order. That ordering is only observable through a peakHour TIE, and it is reproduced
        // rather than guessed at.
        var byHour = new Dictionary<string, int>(StringComparer.Ordinal);
        List<string> hourOrder = [];

        foreach (var a in rows)
        {
            var hour = HourKey(a.StartTime);

            if (byHour.TryGetValue(hour, out var count))
            {
                byHour[hour] = count + 1;
            }
            else
            {
                byHour[hour] = 1;
                hourOrder.Add(hour);
            }
        }

        var enumeratedHours = JsKeyOrder(hourOrder);

        // Highest count wins; a tie keeps the first key in enumeration order because
        // Array.prototype.sort and OrderByDescending are both stable. '09' is the fallback, which
        // the >= 5 guard makes unreachable.
        var peakHour = enumeratedHours.Count == 0
            ? "09"
            : enumeratedHours.OrderByDescending(h => byHour[h]).First();

        IReadOnlyList<ApptReadOptimizeHourCount> hourDistribution =
        [
            .. enumeratedHours
                .Select(h => new ApptReadOptimizeHourCount($"{h}:00", byHour[h]))
                // Node sorts with localeCompare on the string; ORDINAL here, which is identical
                // for the zero-padded values the writers actually store.
                .OrderBy(h => h.Hour, StringComparer.Ordinal)
        ];

        int? avgVisitDurationMin = null;

        if (completedVisits.Count > 0)
        {
            // Outliers are filtered on an EXCLUSIVE (0, 300) minute band, so a same-instant
            // completion and anything over five hours are both discarded.
            double[] durations =
            [
                .. completedVisits
                    .Select(v => (v.CompletedAt - v.VisitDate).TotalMinutes)
                    .Where(d => d is > 0 and < 300)
            ];

            if (durations.Length > 0) avgVisitDurationMin = JsRound(durations.Average());
        }

        var busiestDay = dayStats.Count > 0 ? dayStats[0].Day : "N/A";

        // Distinct calendar days, which Node buckets with toDateString() in server-local time.
        var uniqueDays = rows.Select(a => a.AppointmentDate.Date).Distinct().Count();
        var avgDailyPatients = uniqueDays > 0 ? Fixed1((double)totalAppts / uniqueDays) : 0;

        // `activeSlots.length * Math.ceil(90 / 7)` — the CURRENT weekly template treated as if it
        // had been in force for all 13 weeks, counted once per week. A rough proxy, copied
        // literally including the hard-coded 13.
        var totalWeeklySlotsCapacity = activeSlots.Count;
        var weeksInRange = (int)Math.Ceiling(WindowDays / 7.0);
        var totalCapacity = totalWeeklySlotsCapacity * weeksInRange;

        var slotUtilization = totalCapacity > 0
            ? Fixed3((double)totalAppts / totalCapacity)
            : 0;

        // `sl.slotDuration || 30`: a stored 0 is falsy and becomes 30. The literal 30 is also the
        // whole answer when there are no active slots at all.
        var avgSlotDuration = activeSlots.Count > 0
            ? JsRound(activeSlots.Average(s => (double)(s.SlotDuration == 0 ? 30 : s.SlotDuration)))
            : 30;

        // SPARSE, and in first-appearance order: a type that never occurs has no key rather than
        // a zero. JsonObject rather than a Dictionary so that order is guaranteed on the wire.
        var typeBreakdown = new JsonObject();

        foreach (var a in rows)
        {
            var type = a.AppointmentType.ToString();
            typeBreakdown[type] = typeBreakdown[type] is { } existing
                ? existing.GetValue<int>() + 1
                : 1;
        }

        return new ApptReadOptimizeStats(
            TotalAppointments: totalAppts,
            Completed: completed,
            NoShows: noShows,
            Cancelled: cancelled,
            NoShowRate: noShowRate,
            CancelRate: cancelRate,
            CompletionRate: completionRate,
            AvgVisitDurationMin: avgVisitDurationMin,
            AvgSlotDuration: avgSlotDuration,
            PeakHour: $"{peakHour}:00",
            BusiestDay: busiestDay,
            AvgDailyPatients: avgDailyPatients,
            // The cap is applied AFTER the rounding (appointments.js:1476), and a saturated value
            // serializes as the integer 1.
            SlotUtilization: Math.Min(slotUtilization, 1),
            DayStats: dayStats,
            HourDistribution: hourDistribution,
            TypeBreakdown: typeBreakdown,
            TotalActiveSlotsPerWeek: totalWeeklySlotsCapacity,
            AnalyzedDays: uniqueDays);
    }

    /// <summary>One weekday's tallies while <c>byDay</c> is being built.</summary>
    private readonly record struct DayBucket(int Total, int NoShow, int Cancelled);

    /// <summary>
    /// <c>startTime.split(':')[0] || '00'</c>. The hour KEY is taken verbatim, so an unpadded
    /// stored <c>"9:00"</c> yields <c>"9"</c> and therefore a <c>peakHour</c> of <c>"9:00"</c>.
    /// </summary>
    private static string HourKey(string startTime)
    {
        var separator = startTime.IndexOf(':');
        var hour = separator >= 0 ? startTime[..separator] : startTime;

        return hour.Length == 0 ? "00" : hour;
    }

    /// <summary>
    /// V8's property enumeration order: keys that are canonical array indices first, in ascending
    /// numeric order, then every other key in insertion order. <c>"09"</c> is NOT canonical (only
    /// <c>"9"</c> is), which is why zero-padded hours keep their insertion order.
    /// </summary>
    private static List<string> JsKeyOrder(IReadOnlyList<string> insertionOrder)
    {
        List<(uint Index, string Key)> indexed = [];
        List<string> named = [];

        foreach (var key in insertionOrder)
        {
            if (IsArrayIndex(key, out var index)) indexed.Add((index, key));
            else named.Add(key);
        }

        indexed.Sort((left, right) => left.Index.CompareTo(right.Index));

        return [.. indexed.Select(pair => pair.Key), .. named];
    }

    private static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;

        if (key.Length is 0 or > 10) return false;
        if (key.Length > 1 && key[0] == '0') return false;

        foreach (var c in key)
        {
            if (c is < '0' or > '9') return false;
        }

        return uint.TryParse(key, CultureInfo.InvariantCulture, out index)
               && index != uint.MaxValue;
    }

    /// <summary>
    /// <c>parseFloat(x.toFixed(3))</c>, then let the serializer emit the shortest round-trippable
    /// form so 0.100 ships as <c>0.1</c> and an exact zero ships as <c>0</c>.
    /// </summary>
    private static double Fixed3(double value) => JsToFixed(value, 3);

    /// <summary><c>toFixed(1)</c> — ONE decimal, unlike every other figure, so 3.0 ships as <c>3</c>.</summary>
    private static double Fixed1(double value) => JsToFixed(value, 1);

    /// <summary>
    /// <c>parseFloat(x.toFixed(digits))</c>, computed the way ECMA-262 20.1.3.3 specifies it:
    /// pick the integer <c>n</c> minimising <c>|n / 10^digits - x|</c> over the EXACT binary value
    /// of <paramref name="value"/>, ties going to the larger <c>n</c>, then parse
    /// <c>n / 10^digits</c> back as a double the way <c>parseFloat</c> does.
    ///
    /// <para>This is deliberately NOT <c>Math.Round(double, int, MidpointRounding)</c>, which
    /// scales by a power of ten, rounds the scaled DOUBLE and divides — two roundings, and a
    /// different answer whenever the scaled value lands on the wrong side of a half. The rates
    /// here are small integer ratios, which is exactly where that bites: 3/80 is the double
    /// 0.037499999999999998612…, so <c>toFixed(3)</c> is 0.037 while
    /// <c>Math.Round(3d/80, 3, AwayFromZero)</c> is 0.038; 43/20 is 2.1499999999999999, so
    /// <c>toFixed(1)</c> is 2.1 while <c>Math.Round</c> gives 2.2. Rounding the printed G17
    /// expansion is not equivalent either — it disagrees with the exact value on 43/400 and
    /// dozens of similar ratios — so the numerator is derived from the mantissa and exponent
    /// instead, where no intermediate rounding can occur.</para>
    /// </summary>
    private static double JsToFixed(double value, int digits)
    {
        // `Number.prototype.toFixed` spells NaN and the infinities out, and parseFloat reads each
        // straight back; and at 1e21 or above it defers to ToString(x), which also round-trips.
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) >= 1e21) return value;

        var negative = value < 0;
        var magnitude = Math.Abs(value);

        // magnitude == mantissa * 2^exponent, exactly.
        var bits = BitConverter.DoubleToInt64Bits(magnitude);
        var biasedExponent = (int)((bits >> 52) & 0x7FF);
        var rawMantissa = bits & 0xF_FFFF_FFFF_FFFFL;

        var mantissa = biasedExponent == 0
            ? (BigInteger)rawMantissa
            : rawMantissa | (1L << 52);
        var exponent = biasedExponent == 0 ? -1074 : biasedExponent - 1075;

        // n = round-half-up(magnitude * 10^digits), as the exact rational numerator / denominator.
        var scale = BigInteger.Pow(10, digits);
        var numerator = mantissa * scale;
        var denominator = BigInteger.One;

        if (exponent >= 0) numerator <<= exponent;
        else denominator <<= -exponent;

        var n = (2 * numerator + denominator) / (2 * denominator);

        // Re-read "n / 10^digits" as parseFloat would, so the result is the nearest double to the
        // decimal string rather than the result of a second floating-point division.
        var text = n.ToString(CultureInfo.InvariantCulture).PadLeft(digits + 1, '0');
        var parsed = double.Parse(
            digits == 0 ? text : string.Concat(text.AsSpan(0, text.Length - digits), ".", text.AsSpan(text.Length - digits)),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);

        return negative ? -parsed : parsed;
    }

    /// <summary>
    /// <c>Math.round</c>, which rounds a half TOWARDS POSITIVE INFINITY rather than to even as
    /// .NET's default does. Every value it is applied to here is positive.
    /// </summary>
    private static int JsRound(double value) => (int)Math.Floor(value + 0.5);

    // ── Stage 2: the model's answer ──────────────────────────────────────────

    /// <summary>
    /// De-fences and sanitizes the model's reply (appointments.js:1525-1546). A parse failure, a
    /// non-array result and an entry missing <c>type</c>, <c>title</c> or <c>description</c> all
    /// degrade quietly: the first two to an empty list, the third by dropping that entry. The
    /// ordering is whatever the model returned — the prompt's "ordered by impact" is not enforced
    /// in code.
    /// </summary>
    private static IEnumerable<ApptReadOptimizeInsight> ParseInsights(string text)
    {
        var cleaned = PlainFence.Replace(JsonFence.Replace(text, string.Empty), string.Empty).Trim();

        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(cleaned);
        }
        catch (JsonException)
        {
            // The INNER catch: insights become [] while aiProvider keeps the real provider.
            return [];
        }

        if (parsed is not JsonArray array) return [];

        return
        [
            .. array
                .Where(item => item is JsonObject o
                            && ApptReadJsValues.IsTruthy(o["type"])
                            && ApptReadJsValues.IsTruthy(o["title"])
                            && ApptReadJsValues.IsTruthy(o["description"]))
                .Select(item =>
                {
                    var o = (JsonObject)item!;
                    var type = ApptReadJsValues.AsString(o["type"]);
                    var impact = ApptReadJsValues.AsString(o["impact"]);

                    return new ApptReadOptimizeInsight(
                        Type: type is not null && InsightTypes.Contains(type, StringComparer.Ordinal)
                            ? type
                            : "THROUGHPUT",
                        Title: Truncate(ApptReadJsValues.Stringify(o["title"]), 120),
                        Description: Truncate(ApptReadJsValues.Stringify(o["description"]), 500),
                        Impact: impact is not null
                                && InsightImpacts.Contains(impact, StringComparer.Ordinal)
                            ? impact
                            : "MODERATE");
                })
                // The code's cap, not the prompt's: six, even though the prompt asks for 3-5.
                .Take(6)
        ];
    }

    /// <summary>
    /// <c>String.prototype.substring(0, n)</c> counts UTF-16 code units, and so does .NET string
    /// indexing, so a surrogate pair is split at exactly the same place.
    /// </summary>
    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length];
}

/// <summary>
/// The JavaScript value semantics the AI sanitizer depends on. The Node code gates on truthiness
/// and coerces with a bare <c>String(value)</c>, and both decide what ends up in the response.
/// </summary>
internal static class ApptReadJsValues
{
    /// <summary>Only <c>false</c>, <c>0</c>, <c>""</c>, <c>null</c> and absent are falsy.</summary>
    public static bool IsTruthy(JsonNode? value)
    {
        if (value is null) return false;

        return value.GetValueKind() switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => value.AsValue().TryGetValue<double>(out var d) && d != 0,
            JsonValueKind.String => Stringify(value).Length > 0,
            _ => true
        };
    }

    /// <summary>The node's string value, or null when it is not a JSON string.</summary>
    public static string? AsString(JsonNode? value)
        => value is not null
           && value.GetValueKind() == JsonValueKind.String
           && value.AsValue().TryGetValue<string>(out var s)
            ? s
            : null;

    /// <summary>
    /// <c>String(value)</c>: numbers in invariant form, booleans lowercase, an array comma-joined
    /// and any other object the literal "[object Object]" — which is what Node would put in an
    /// insight title if the model nested something there.
    /// </summary>
    public static string Stringify(JsonNode? value)
    {
        if (value is null) return string.Empty;

        switch (value.GetValueKind())
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return string.Empty;
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.String:
                return value.AsValue().TryGetValue<string>(out var s) ? s ?? string.Empty : string.Empty;
            case JsonValueKind.Number:
                return value.AsValue().TryGetValue<double>(out var d)
                    ? d.ToString("R", CultureInfo.InvariantCulture)
                    : value.ToJsonString();
            case JsonValueKind.Array:
                return string.Join(",", value.AsArray().Select(Stringify));
            default:
                return "[object Object]";
        }
    }
}
