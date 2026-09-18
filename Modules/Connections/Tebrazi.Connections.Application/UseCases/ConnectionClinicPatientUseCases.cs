using System.Text.Json;
using Tebrazi.Connections.Application.ApiModels.Responses;
using Tebrazi.Connections.Application.Services;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Connections.Application.UseCases;

// ═════════════════════════════════════════════════════════════════════════════
//  The WALK-IN CHART group of server/src/routes/connections.js:
//
//      POST   /api/connections/clinic-patients        (L747-L807) -> 201
//      GET    /api/connections/clinic-patients        (L813-L849) -> 200, a BARE ARRAY
//      DELETE /api/connections/clinic-patients/{id}   (L860-L893) -> 200
//
//  A "clinic patient" is a chart a physician keeps for somebody who has no Tebrazi account. No
//  user row, no password, no login - and, importantly, NO CONNECTION. None of these three routes
//  touches `doctor_patient_connections`: creating a chart does not open an edge, and deleting one
//  does not close any. `POST /link-account` (L900, out of scope) is what later marries a chart to
//  a real account, and it is the only route in the file that does.
//
//  Facts that hold for all three, stated once rather than three times:
//
//  * `clinic_patients` BELONGS TO THE CLINICS MODULE. It has an entity, an EF configuration, a
//    store and a migration there. This file reaches it only through `IClinicPatientDirectory`,
//    never through a second entity and never through ClinicsDbContext - see
//    docs/connections-surface.md 11.3. The four members used below (GetOwnedRecordAsync,
//    ListActiveForPhysicianAsync, CreateAsync, DeleteAsync) were added to that port by the
//    Foundation pass specifically for these three routes.
//
//  * NO SOFT DELETE ANYWHERE. `deletedAt` does not occur in the 1,818 lines of connections.js and
//    `model ClinicPatient` (schema.prisma:569-611) declares no such column. The DELETE route is a
//    HARD `prisma.clinicPatient.delete` (L885) inside a transaction that first hard-deletes the
//    chart's visits and appointments. `isActive` is NOT a soft-delete flag being reused: the
//    delete route never writes it, the create route hard-codes it `true`, and nothing in
//    connections.js ever sets it false. It is a filter on the list query and nothing more.
//
//  * THE STAFF FALLBACK IS THE SAME THREE STEPS AND TWO DIFFERENT FAILURE MAPS. Create answers
//    400/403/404 with its OWN "Clinic not found" literal; List swallows every failure into
//    `200 []`; Delete has no staff fallback at all and gates on physician_user_id alone, so a
//    receptionist always gets its 404. ConnStaffResolver performs the steps and reports where they
//    stopped; the mapping stays in each handler because it is on the wire.
//
//  * `X-Clinic-Id` PRECEDENCE IS QUERY/BODY FIRST, HEADER SECOND, ON BOTH ROUTES THAT READ IT -
//    `req.body.clinicId || req.headers['x-clinic-id']` (L768) and
//    `req.query.clinicId || req.headers['x-clinic-id']` (L822). That is the OPPOSITE of
//    IClinicContext, which resolves header-then-query (HttpClinicContext.cs:22-37) and cannot
//    serve these two. The raw header is therefore carried on the request record, separately from
//    the query/body value, exactly as ApptReadListQuery does it
//    (AppointmentsController.cs:249-276). Injecting IClinicContext here would silently swap the
//    precedence for any caller who sends both, and the axios interceptor sends the header on
//    EVERY request once a clinic is active (client/src/services/api.js:31).
//
//  * ONLY THE STAFF PATH READS THE HEADER. On the create route's PHYSICIAN path the clinic id is
//    `req.body.clinicId` alone (L755); the header is consulted inside the else-branch only (L768).
//    So a physician who sends `X-Clinic-Id: A` and no body clinicId files the chart under their
//    FIRST clinic, not under A. Reproduced.
//
//  * EACH ROUTE HAS ITS OWN NAMED 500 and the generic middleware body reproduces none of them, so
//    each handler is wrapped end to end by ConnClinicPatientPersistence.RunAsync: Handle delegates
//    to HandleCore, an AppException passes through untouched so the deliberate 400/403/404 keep
//    their bodies, and everything else is logged the way Node logs it and re-thrown as that
//    route's literal.
//
//  * THE NODE TYPE ERRORS ARE PART OF THE CONTRACT. connections.js calls `.trim()` on body values
//    it never type-checks and hands raw values to Prisma columns, so a wrongly-typed field is a
//    TypeError or a PrismaClientValidationError inside the route's own try - i.e. that route's
//    named 500, not a 400. ConnClinicPatientBody below raises InvalidOperationException at exactly
//    the points Node raises those, and the file guard converts it. This is also why the body binds
//    as JsonElement members rather than `string?`: a `string?` member would make the MVC binder
//    answer `400 {"error":"Validation failed","message":...}`, a body no Connections route
//    produces.
//
//  ROUTE ORDER, for whoever writes the controller: `clinic-patients` is a LITERAL segment and must
//  be registered before any `connections/{id}` route, or `GET /clinic-patients` binds as
//  id="clinic-patients". `DELETE /clinic-patients/{id}` must likewise precede `DELETE /{id}`.
//  The create action needs
//  `[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ConnClinicPatientCreateBody? body` -
//  Express hands an empty POST `{}` and answers 400 "Patient name is required", while plain
//  `[FromBody] X?` would answer the binder's 400 instead. And `GET /clinic-patients?search=` is
//  sent by AssistantDashboardPage.jsx:232 and IGNORED by Node - do not add a search parameter.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The route-level guard for this file, mapping any unexpected failure onto the route's OWN 500
/// body.
///
/// <para>Each Node handler is wrapped in a try/catch whose only outcome is a named literal -
/// <c>{"error":"Failed to create patient file"}</c>, <c>"Failed to load clinic patients"</c>,
/// <c>"Failed to delete patient file"</c>. Letting an EF or port exception escape would emit
/// <c>ExceptionHandlingMiddleware</c>'s generic <c>{"error":"Internal Server Error"}</c> instead,
/// and the React client branches on <c>err.response.data.error</c>
/// (ConnectionsPage.jsx:385).</para>
///
/// <para>The Node try covers the WHOLE handler, so a failure in the staff resolution, in the
/// cross-module visit count or in the chart write all answer the same named 500. That is why this
/// wraps <c>HandleCore</c> end to end rather than one statement.</para>
///
/// <para>Declared in this file with this group's prefix rather than shared: five handler files
/// compile into one namespace and each needs its own log message and failure literal.</para>
/// </summary>
internal static class ConnClinicPatientPersistence
{
    /// <summary>
    /// Runs <paramref name="body"/> under the route's catch-all.
    /// </summary>
    /// <typeparam name="THandler">The calling handler, for the logger category.</typeparam>
    /// <typeparam name="TResponse">The route's response type.</typeparam>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="logMessage">
    /// The Node log line, verbatim (e.g. "[Connections] Create clinic patient error").
    /// </param>
    /// <param name="failureError">The route's own 500 literal, verbatim.</param>
    /// <param name="cancellationToken">
    /// A client disconnect must not be logged and re-thrown as a server error.
    /// </param>
    /// <param name="body">The handler body.</param>
    /// <returns>The handler's response.</returns>
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
            throw ConnErrors.ServerError(failureError);
        }
    }
}

/// <summary>
/// The body coercions <c>POST /api/connections/clinic-patients</c> applies (connections.js:750,
/// 785-798), each one reproducing what JavaScript does to a value of the wrong type rather than
/// what a typed binder would do.
///
/// <para>Every method that cannot produce a value throws <see cref="InvalidOperationException"/>,
/// which the file guard turns into <c>500 {"error":"Failed to create patient file"}</c> - the same
/// answer Node gives, because in Node the failure is a TypeError or a
/// PrismaClientValidationError raised inside the route's own try.</para>
/// </summary>
internal static class ConnClinicPatientBody
{
    /// <summary>
    /// <c>if (!name || !name.trim()) return 400</c> (connections.js:751), then
    /// <c>name: name.trim()</c> (:789).
    ///
    /// <para>Three outcomes, and the order is the contract: a FALSY value (absent, null, false,
    /// 0 or "") is the 400; a TRUTHY NON-STRING reaches <c>.trim()</c>, which is a TypeError and
    /// therefore the route's 500; a whitespace-only string trims to "" and is the 400.</para>
    /// </summary>
    /// <param name="name">The bound <c>name</c> value.</param>
    /// <returns>The trimmed name.</returns>
    public static string RequireName(JsonElement name)
    {
        if (!ConnJs.IsTruthy(name)) throw ConnErrors.BadRequest("Patient name is required");

        if (ConnJs.AsString(name) is not { } text)
        {
            throw new InvalidOperationException(
                "POST /api/connections/clinic-patients received a truthy non-string `name`; Node "
                + "calls name.trim() on it (connections.js:751), which is a TypeError and the "
                + "route's own 500.");
        }

        return ConnJs.TrimToNull(text) ?? throw ConnErrors.BadRequest("Patient name is required");
    }

    /// <summary>
    /// <c>value?.trim() || null</c> - the shape applied to <c>phone</c>, <c>email</c> and
    /// <c>notes</c> (connections.js:790, 791, 797).
    ///
    /// <para>Optional chaining short-circuits on null and undefined ONLY, so a number, a boolean,
    /// an object or an array all reach <c>.trim()</c> and raise a TypeError - the route's 500,
    /// not a silent null.</para>
    /// </summary>
    /// <param name="value">The bound value.</param>
    /// <param name="field">The body key, for the exception text.</param>
    /// <returns>The trimmed value, or null.</returns>
    public static string? OptionalTrimmed(JsonElement value, string field)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;

        if (ConnJs.AsString(value) is not { } text)
        {
            throw new InvalidOperationException(
                $"POST /api/connections/clinic-patients received a non-string `{field}`; Node "
                + $"evaluates {field}?.trim(), which is a TypeError for every kind but null and "
                + "undefined, and therefore the route's own 500.");
        }

        return ConnJs.TrimToNull(text);
    }

    /// <summary>
    /// <c>value || null</c> - the shape applied to <c>clinicId</c> (connections.js:755),
    /// <c>gender</c> (:793) and <c>bloodType</c> (:794). No trimming: <c>"  O+  "</c> is stored
    /// with its spaces.
    ///
    /// <para>A falsy value becomes null. A truthy NON-string is passed straight to a Prisma
    /// <c>String</c>, <c>Gender</c> or <c>where</c> argument, every one of which rejects it, so it
    /// is the route's 500.</para>
    /// </summary>
    /// <param name="value">The bound value.</param>
    /// <param name="field">The body key, for the exception text.</param>
    /// <returns>The value, or null.</returns>
    public static string? TruthyString(JsonElement value, string field)
    {
        if (!ConnJs.IsTruthy(value)) return null;

        return ConnJs.AsString(value)
            ?? throw new InvalidOperationException(
                $"POST /api/connections/clinic-patients received a truthy non-string `{field}`; "
                + "Node passes it unconverted to Prisma, which rejects it into the route's own "
                + "500.");
    }

    /// <summary>
    /// <c>Array.isArray(value) ? value : []</c> - the shape applied to <c>allergies</c> and
    /// <c>chronicConditions</c> (connections.js:795-796).
    ///
    /// <para>Anything that is not an array - a string, an object, null, an absent key - becomes
    /// the EMPTY array rather than an error. A non-string ELEMENT is different: the column is
    /// <c>String[]</c>, so Prisma rejects it and the route answers its 500.</para>
    /// </summary>
    /// <param name="value">The bound value.</param>
    /// <param name="field">The body key, for the exception text.</param>
    /// <returns>The list, empty when the value was not an array.</returns>
    public static IReadOnlyList<string> StringArray(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Array) return [];

        var items = new List<string>(value.GetArrayLength());
        foreach (var element in value.EnumerateArray())
        {
            items.Add(
                ConnJs.AsString(element)
                ?? throw new InvalidOperationException(
                    $"POST /api/connections/clinic-patients received a non-string element in "
                    + $"`{field}`; the column is String[] and Prisma rejects it into the route's "
                    + "own 500."));
        }

        return items;
    }

    /// <summary>
    /// <c>dateOfBirth ? new Date(dateOfBirth) : null</c> (connections.js:792).
    ///
    /// <para>Falsy is null. A string is parsed, and an UNPARSEABLE one is an Invalid Date, which
    /// Prisma refuses - the route's 500, never a silently absent date. A number is epoch
    /// milliseconds, which <c>new Date(number)</c> accepts and this reproduces.</para>
    ///
    /// <para><b>One-value divergence, recorded rather than modelled:</b> JavaScript coerces
    /// <c>new Date(true)</c> to 1 ms after the epoch, so a literal <c>true</c> stores
    /// 1970-01-01T00:00:00.001Z in Node and is the route's 500 here. Objects and arrays coerce to
    /// strings that do not parse, so those agree.</para>
    /// </summary>
    /// <param name="value">The bound value.</param>
    /// <returns>The parsed instant, or null.</returns>
    public static DateTime? OptionalDate(JsonElement value)
    {
        if (!ConnJs.IsTruthy(value)) return null;

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var epochMilliseconds)
            && Math.Abs(epochMilliseconds) <= 8.64e15)
        {
            return DateTimeOffset
                .FromUnixTimeMilliseconds((long)epochMilliseconds)
                .UtcDateTime;
        }

        if (ConnJs.AsString(value) is { } text && ConnDates.TryParse(text, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            "POST /api/connections/clinic-patients received a `dateOfBirth` that new Date() turns "
            + "into an Invalid Date; Prisma rejects it into the route's own 500.");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  POST /api/connections/clinic-patients
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The ten body keys <c>POST /api/connections/clinic-patients</c> destructures
/// (connections.js:750), in that order.
///
/// <para>Every member is a non-nullable <see cref="JsonElement"/> so the handler sees what Node
/// sees. <see cref="JsonValueKind.Undefined"/> is an absent key and
/// <see cref="JsonValueKind.Null"/> an explicit null - a distinction <c>notes?.trim()</c> does not
/// need but <c>JsonElement?</c> could not make either way - and a wrongly-typed value reaches the
/// handler's own coercion instead of the MVC binder's
/// <c>400 {"error":"Validation failed"}</c>, which is a body no Connections route produces.</para>
///
/// <para><b>`nationalId` is deliberately absent.</b> ConnectionsPage.jsx:381 puts it in the
/// payload and Node never destructures it, so it is dropped on the floor and the created chart's
/// <c>nationalId</c> comes back null. Adding the member here would start storing a value Node does
/// not store.</para>
/// </summary>
/// <param name="Name">Required. Falsy is 400, truthy non-string is 500, blank-after-trim is 400.</param>
/// <param name="Phone"><c>phone?.trim() || null</c>.</param>
/// <param name="Email"><c>email?.trim() || null</c>. Not validated, not de-duplicated.</param>
/// <param name="DateOfBirth"><c>dateOfBirth ? new Date(dateOfBirth) : null</c>.</param>
/// <param name="Gender">
/// <c>gender || null</c>, written raw into a Prisma <c>Gender</c> enum column. The client sends
/// <c>""</c> when the field is untouched (ConnectionsPage.jsx:382), which is falsy and therefore
/// null.
/// </param>
/// <param name="BloodType"><c>bloodType || null</c>. Free text, untrimmed.</param>
/// <param name="Allergies"><c>Array.isArray(allergies) ? allergies : []</c>.</param>
/// <param name="ChronicConditions">Same rule as <paramref name="Allergies"/>.</param>
/// <param name="Notes"><c>notes?.trim() || null</c>.</param>
/// <param name="ClinicId">
/// <c>clinicId || null</c>. On the PHYSICIAN path this is the only source of a clinic id - the
/// <c>X-Clinic-Id</c> header is not consulted there. On the staff path it is the first half of
/// <c>body.clinicId || X-Clinic-Id</c>.
/// </param>
public sealed record ConnClinicPatientCreateBody(
    JsonElement Name,
    JsonElement Phone,
    JsonElement Email,
    JsonElement DateOfBirth,
    JsonElement Gender,
    JsonElement BloodType,
    JsonElement Allergies,
    JsonElement ChronicConditions,
    JsonElement Notes,
    JsonElement ClinicId);

/// <param name="CallerUserId"><c>req.user.id</c>. A WRITE handler, so the caller arrives on the
/// request rather than through ICurrentUser.</param>
/// <param name="CallerUserType">
/// <c>req.user.userType</c>. The branch is <c>=== 'PHYSICIAN'</c>, so RECEPTIONIST, STAFF, PATIENT,
/// an unknown value and a missing claim all take the staff path. Kept as a raw string: the claim
/// can carry a value the UserType enum does not have.
/// </param>
/// <param name="ClinicHeaderId">
/// The RAW <c>X-Clinic-Id</c> header, bound separately from <paramref name="Body"/>'s
/// <c>clinicId</c> because Node's precedence is body-first and IClinicContext's is header-first.
/// </param>
/// <param name="Body">
/// The parsed body, nullable because Express hands the handler <c>{}</c> for an empty POST and the
/// route then answers <c>400 "Patient name is required"</c>. The action parameter needs
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>.
/// </param>
public sealed record ConnClinicPatientCreateCommand(
    string CallerUserId,
    string? CallerUserType,
    string? ClinicHeaderId,
    ConnClinicPatientCreateBody? Body) : IRequest<ConnClinicPatientCreateResponse>;

/// <summary>
/// Port of <c>POST /api/connections/clinic-patients</c> (connections.js:747-807). Creates a
/// physician-owned chart for a walk-in patient and answers <b>201</b>
/// <c>{ success, patient, message }</c>.
///
/// <para><b>It does NOT create a user account and it does NOT open a connection.</b> Compare
/// <c>POST /create-patient</c> (L617), which provisions a real user, a patient profile, a SELF
/// subprofile and an ACCEPTED edge, and notifies. This route writes ONE row.</para>
///
/// <para><b>There is no duplicate check.</b> Unlike <c>create-patient</c>, which answers
/// <c>409 "A patient with this email or phone already exists..."</c>, this route will happily write
/// a second chart with the same name, phone and email.</para>
///
/// <para><b>The error order is: name, then clinic resolution.</b> A staff caller with no clinic id
/// AND no name gets the name's 400, because the name gate is the route's first statement
/// (L751).</para>
///
/// <para><b>The body coercions run AFTER the resolution, and that is observable.</b> Node builds
/// the <c>data</c> object inside the <c>create</c> call (L786-L798), so a staff caller who is not
/// authorized AND sends a numeric <c>phone</c> gets the 403, not the TypeError's 500. Doing the
/// coercions up front would swap those.</para>
///
/// <para><b>A staff caller whose clinic has no physician is a 500, not a 404.</b> Node dereferences
/// <c>clinic.physician.userId</c> unguarded at L781 after having already returned 404 for a
/// missing CLINIC at L780, so a dangling physician FK is a TypeError inside the route's own
/// catch. ConnStaffOutcome.NoClinicPhysician is therefore re-thrown rather than mapped, and note
/// the 404 literal here is <c>"Clinic not found"</c> - <c>POST /create-patient</c>'s sibling check
/// says <c>"Clinic physician not found"</c> instead.</para>
/// </summary>
public sealed class ConnClinicPatientCreateHandler(
    IClinicPatientDirectory clinicPatients,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    ConnStaffResolver staffResolver,
    IAppLogger<ConnClinicPatientCreateHandler> logger)
    : IRequestHandler<ConnClinicPatientCreateCommand, ConnClinicPatientCreateResponse>
{
    private const string LogMessage = "[Connections] Create clinic patient error";
    private const string FailureError = "Failed to create patient file";
    private const string PhysicianUserType = "PHYSICIAN";

    public Task<ConnClinicPatientCreateResponse> Handle(
        ConnClinicPatientCreateCommand request, CancellationToken cancellationToken = default)
        => ConnClinicPatientPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnClinicPatientCreateResponse> HandleCore(
        ConnClinicPatientCreateCommand request, CancellationToken cancellationToken)
    {
        var body = request.Body;

        // `if (!name || !name.trim())` (L751) - the route's FIRST statement after the destructure,
        // so it precedes every clinic check. An absent body is Express's `{}`: name is undefined,
        // which is falsy, which is this 400.
        if (body is null) throw ConnErrors.BadRequest("Patient name is required");

        var name = ConnClinicPatientBody.RequireName(body.Name);

        // `let resolvedClinicId = clinicId || null` (L755). Evaluated here because the staff path
        // reads it immediately; a truthy non-string raises the route's 500 at the same point Node
        // does - the clinicStaff query on the staff path, the create on the physician path, both
        // of which produce the identical body.
        var resolvedClinicId = ConnClinicPatientBody.TruthyString(body.ClinicId, "clinicId");
        string resolvedPhysicianUserId;

        if (string.Equals(request.CallerUserType, PhysicianUserType, StringComparison.Ordinal))
        {
            resolvedPhysicianUserId = request.CallerUserId;

            // `physician?.clinics?.[0]?.id || null` (L759-L764). NOT an error when it misses: a
            // physician with no profile row, or with a profile and no clinic, files the chart with
            // clinic_id NULL. The X-Clinic-Id header is deliberately NOT consulted on this path.
            if (resolvedClinicId is null)
            {
                var physician = await identity.GetPhysicianByUserIdAsync(
                    request.CallerUserId, cancellationToken);

                if (physician is not null)
                {
                    // ClinicSummary.PhysicianId is a PROFILE id, which is what `clinics` hangs off
                    // in Prisma, so the lookup takes physician.Id and not the user id.
                    resolvedClinicId = ConnJs.Truthy(
                        await clinics.GetFirstClinicIdForPhysicianAsync(
                            physician.Id, cancellationToken));
                }
            }
        }
        else
        {
            // `const staffClinicId = resolvedClinicId || req.headers['x-clinic-id']` (L768) - body
            // first, header second, the opposite of IClinicContext.
            var staffClinicId = resolvedClinicId ?? ConnJs.Truthy(request.ClinicHeaderId);
            var resolution = await staffResolver.ResolveAsync(
                request.CallerUserId, staffClinicId, cancellationToken);

            resolvedPhysicianUserId = resolution.Outcome switch
            {
                ConnStaffOutcome.Resolved => resolution.PhysicianUserId!,

                // L769
                ConnStaffOutcome.NoClinicId
                    => throw ConnErrors.BadRequest("clinicId is required for staff"),

                // L774
                ConnStaffOutcome.NotStaff => throw ConnErrors.Forbidden("Not authorized"),

                // L780. NOTE the literal: create-patient's equivalent says "Clinic physician not
                // found" and this one says "Clinic not found".
                ConnStaffOutcome.ClinicNotFound => throw ConnErrors.NotFound("Clinic not found"),

                // L781 dereferences clinic.physician.userId with no guard, so this is a TypeError
                // in Node and therefore the route's own 500 - not a fourth status code.
                _ => throw new InvalidOperationException(
                    $"Clinic {staffClinicId} resolved but its physician profile did not; "
                    + "connections.js:781 dereferences clinic.physician.userId unguarded, so this "
                    + "is the route's 500.")
            };

            // `resolvedClinicId = staffClinicId` (L782) - the chart is filed under the clinic the
            // staff member was checked against, never under a clinic from the physician's list.
            resolvedClinicId = staffClinicId;
        }

        // The `data` object of L786-L798, built here because Node builds it here: after every
        // status check, so a wrongly-typed field cannot pre-empt a 400/403/404.
        var draft = new ClinicPatientDraft(
            PhysicianUserId: resolvedPhysicianUserId,
            Name: name,
            ClinicId: resolvedClinicId,
            Phone: ConnClinicPatientBody.OptionalTrimmed(body.Phone, "phone"),
            Email: ConnClinicPatientBody.OptionalTrimmed(body.Email, "email"),
            DateOfBirth: ConnClinicPatientBody.OptionalDate(body.DateOfBirth),
            Gender: ConnClinicPatientBody.TruthyString(body.Gender, "gender"),
            BloodType: ConnClinicPatientBody.TruthyString(body.BloodType, "bloodType"),
            Allergies: ConnClinicPatientBody.StringArray(body.Allergies, "allergies"),
            ChronicConditions: ConnClinicPatientBody.StringArray(
                body.ChronicConditions, "chronicConditions"),
            Notes: ConnClinicPatientBody.OptionalTrimmed(body.Notes, "notes"));

        ClinicPatientRecord chart;
        try
        {
            // Commits through the Clinics unit of work - `isActive: true` is set by the port.
            chart = await clinicPatients.CreateAsync(draft, cancellationToken);
        }
        catch (BusinessException exception)
        {
            // IClinicPatientDirectory.CreateAsync raises its own 500-shaped BusinessException for a
            // gender outside MALE/FEMALE/OTHER, reproducing Prisma's rejection of the enum column.
            // An AppException passes the file guard UNTOUCHED, so without this catch the client
            // would see {"error":"Invalid gender","message":...} where Node sends this route's own
            // bare literal. Node has no such distinction: every Prisma failure here is the same
            // catch at L805.
            logger.Error(LogMessage, exception);
            throw ConnErrors.ServerError(FailureError);
        }

        return new ConnClinicPatientCreateResponse(
            Success: true,
            Patient: new ConnClinicPatientCreatedChart(
                chart.Id,
                chart.PhysicianUserId,
                chart.ClinicId,
                chart.Name,
                chart.Phone,
                chart.Email,
                chart.DateOfBirth,
                chart.Gender,
                chart.NationalId,
                chart.BloodType,
                chart.Notes,
                chart.Allergies,
                chart.ChronicConditions,
                chart.LinkedUserId,
                chart.LinkedAt,
                chart.IsActive,
                chart.LastVisitDate,
                chart.CreatedAt,
                chart.UpdatedAt),

            // `${patient.name} added successfully` (L802) - the STORED name, i.e. trimmed.
            Message: $"{chart.Name} added successfully");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/connections/clinic-patients
// ═════════════════════════════════════════════════════════════════════════════

/// <param name="ClinicId">
/// <c>?clinicId=</c>. Read ONLY on the non-physician path, as the first half of
/// <c>req.query.clinicId || req.headers['x-clinic-id']</c> (L822). A physician's list is never
/// scoped by clinic at all.
/// </param>
/// <param name="ClinicHeaderId">
/// The RAW <c>X-Clinic-Id</c> header, bound separately from <paramref name="ClinicId"/>: Node
/// resolves query-then-header and IClinicContext resolves header-then-query, and the axios
/// interceptor sends the header on every request once a clinic is active, so a caller passing both
/// would be checked against the wrong clinic.
/// </param>
public sealed record ConnClinicPatientListQuery(
    string? ClinicId,
    string? ClinicHeaderId) : IRequest<IReadOnlyList<ConnClinicPatientListItem>>;

/// <summary>
/// Port of <c>GET /api/connections/clinic-patients</c> (connections.js:813-849). A BARE JSON
/// ARRAY of the caller's physician's ACTIVE charts, ordered by <c>name</c> ascending, each with
/// <c>_count.visits</c> appended.
///
/// <para><b>Every failure is <c>200 []</c>, never a 4xx.</b> No clinic id, not staff at that
/// clinic, no such clinic, a clinic with no physician - all four fall through to
/// <c>physicianUserId = null</c> and the early <c>return res.json([])</c> at L836. A patient
/// calling this endpoint gets <c>[]</c> for the same reason, because the branch key is
/// <c>userType === 'PHYSICIAN'</c> and nothing else. There is no authorization error to
/// report.</para>
///
/// <para><b>The physician branch is not clinic-scoped.</b> <c>?clinicId=</c> and
/// <c>X-Clinic-Id</c> are read on the staff path ONLY (L822); a physician always sees every active
/// chart they own, across every clinic. A receptionist who resolves that physician through clinic
/// A therefore sees clinic B's charts too - the resolution picks a physician, not a clinic.</para>
///
/// <para><b><c>?search=</c> is ignored.</b> AssistantDashboardPage.jsx:232 sends it and Node never
/// reads it, so the full array comes back and the client filters. Implementing it here would
/// change the row set.</para>
///
/// <para>The visit counts come from the Visits module through
/// <see cref="IVisitDirectory.CountByClinicPatientAsync"/>, because Prisma's <c>_count</c> crosses
/// a module boundary this port does not. A chart with no visits is absent from that dictionary and
/// reports <c>0</c>, which is exactly what <c>_count</c> reports.</para>
/// </summary>
public sealed class ConnClinicPatientListHandler(
    ICurrentUser currentUser,
    IClinicPatientDirectory clinicPatients,
    IVisitDirectory visits,
    ConnStaffResolver staffResolver,
    IAppLogger<ConnClinicPatientListHandler> logger)
    : IRequestHandler<ConnClinicPatientListQuery, IReadOnlyList<ConnClinicPatientListItem>>
{
    private const string LogMessage = "[Connections] List clinic patients error";
    private const string FailureError = "Failed to load clinic patients";
    private const string PhysicianUserType = "PHYSICIAN";

    public Task<IReadOnlyList<ConnClinicPatientListItem>> Handle(
        ConnClinicPatientListQuery request, CancellationToken cancellationToken = default)
        => ConnClinicPatientPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<IReadOnlyList<ConnClinicPatientListItem>> HandleCore(
        ConnClinicPatientListQuery request, CancellationToken cancellationToken)
    {
        // The authCheck body, kept so a null caller cannot be read as a filter value and answer
        // 200 with somebody else's charts. Unreachable behind [Authorize].
        var userId = ConnJs.Truthy(currentUser.UserId)
            ?? throw ConnErrors.Node("No token provided", 401);

        string? physicianUserId;
        if (string.Equals(currentUser.UserType, PhysicianUserType, StringComparison.Ordinal))
        {
            physicianUserId = userId;
        }
        else
        {
            // `req.query.clinicId || req.headers['x-clinic-id']` (L822) - query first.
            var staffClinicId = ConnJs.Truthy(request.ClinicId)
                ?? ConnJs.Truthy(request.ClinicHeaderId);

            var resolution = await staffResolver.ResolveAsync(
                userId, staffClinicId, cancellationToken);

            // Every outcome short of Resolved collapses to null here: L823's `if (staffClinicId)`,
            // L827's `if (staffRecord)` and L832's `clinic?.physician?.userId || null` are three
            // separate silent fall-throughs to the same place.
            physicianUserId = resolution.IsResolved ? resolution.PhysicianUserId : null;
        }

        // L836.
        if (physicianUserId is null) return [];

        var charts = await clinicPatients.ListActiveForPhysicianAsync(
            physicianUserId, cancellationToken);

        if (charts.Count == 0) return [];

        string[] chartIds = [.. charts.Select(chart => chart.Id)];
        var visitCounts = await visits.CountByClinicPatientAsync(chartIds, cancellationToken);

        return
        [
            .. charts.Select(chart => new ConnClinicPatientListItem(
                chart.Id,
                chart.PhysicianUserId,
                chart.ClinicId,
                chart.Name,
                chart.Phone,
                chart.Email,
                chart.DateOfBirth,
                chart.Gender,
                chart.NationalId,
                chart.BloodType,
                chart.Notes,
                chart.Allergies,
                chart.ChronicConditions,
                chart.LinkedUserId,
                chart.LinkedAt,
                chart.IsActive,
                chart.LastVisitDate,
                chart.CreatedAt,
                chart.UpdatedAt,
                new ConnClinicPatientVisitCounts(visitCounts.GetValueOrDefault(chart.Id))))
        ];
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  DELETE /api/connections/clinic-patients/{id}
// ═════════════════════════════════════════════════════════════════════════════

/// <param name="ClinicPatientId">The chart id from the route.</param>
/// <param name="CallerUserId">
/// <c>req.user.id</c>, and the ONLY authorization input this route has - there is no userType
/// branch and no staff fallback, so a receptionist can never delete a chart even at their own
/// clinic.
/// </param>
public sealed record ConnClinicPatientDeleteCommand(
    string ClinicPatientId,
    string CallerUserId) : IRequest<ConnClinicPatientDeleteResponse>;

/// <summary>
/// Port of <c>DELETE /api/connections/clinic-patients/{id}</c> (connections.js:860-893). A
/// <b>HARD</b> delete of the chart and of every visit and appointment attached to it, answering
/// <b>200</b> <c>{ success, message }</c>.
///
/// <para><b>Hard, not soft, and that is the whole model here.</b> There is no <c>deletedAt</c>
/// column on <c>clinic_patients</c> and the route does not set <c>isActive = false</c> either - it
/// calls <c>prisma.clinicPatient.delete</c> (L885). Note the contrast with
/// <c>DELETE /api/visits/{id}</c>, which only archives: the visits removed HERE are removed for
/// good, by <c>tx.visit.deleteMany</c> (L877), with no status and no <c>deletedAt</c>
/// predicate.</para>
///
/// <para><b>The ownership gate is <c>physician_user_id</c> alone</b> (L866-L868), so "no such
/// chart" and "somebody else's chart" are the same 404 and a receptionist always gets it.</para>
///
/// <para><b>The prescriptions and investigations of those visits are NOT removed</b>, matching
/// Node - the transaction touches four tables and nothing else, leaving those rows pointing at
/// visit ids that no longer exist in both backends. Appointment time slots are likewise not
/// released.</para>
///
/// <para><b>Node's ONE <c>$transaction</c> becomes THREE commits here</b>, because it spans four
/// tables in three .NET module contexts. The order is Node's - visits, then appointments, then the
/// chart - so a failure part-way leaves a chart whose visits are gone rather than orphan visits
/// with no chart. <c>tx.patientNote.deleteMany</c> (L881) and <c>tx.patientTag.deleteMany</c>
/// (L883) have NO counterpart: neither <c>patient_notes</c> nor <c>patient_tags</c> is ported to
/// any .NET context, so those two statements have nothing to run and this cascade will need two
/// more calls when that module lands. Recorded in docs/connections-surface.md 11.3.</para>
/// </summary>
public sealed class ConnClinicPatientDeleteHandler(
    IClinicPatientDirectory clinicPatients,
    IVisitClinicPatientWriter visitWriter,
    IAppointmentClinicPatientWriter appointmentWriter,
    IAppLogger<ConnClinicPatientDeleteHandler> logger)
    : IRequestHandler<ConnClinicPatientDeleteCommand, ConnClinicPatientDeleteResponse>
{
    private const string LogMessage = "[Connections] Delete clinic patient error";
    private const string FailureError = "Failed to delete patient file";

    public Task<ConnClinicPatientDeleteResponse> Handle(
        ConnClinicPatientDeleteCommand request, CancellationToken cancellationToken = default)
        => ConnClinicPatientPersistence.RunAsync(
            logger, LogMessage, FailureError, cancellationToken,
            () => HandleCore(request, cancellationToken));

    private async Task<ConnClinicPatientDeleteResponse> HandleCore(
        ConnClinicPatientDeleteCommand request, CancellationToken cancellationToken)
    {
        // `findFirst({ where: { id, physicianUserId: userId } })` (L866-L868). Loaded BEFORE the
        // deletion because the success message quotes its name, which is the only trace of the
        // row the response carries.
        var chart = await clinicPatients.GetOwnedRecordAsync(
                        request.ClinicPatientId, request.CallerUserId, cancellationToken)
                    ?? throw ConnErrors.NotFound("Patient file not found or not authorized");

        // The $transaction of L875-L886, in Node's own statement order.
        await visitWriter.DeleteForClinicPatientAsync(request.ClinicPatientId, cancellationToken);
        await appointmentWriter.DeleteForClinicPatientAsync(
            request.ClinicPatientId, cancellationToken);

        // L881 and L883 - patientNote and patientTag - have no ported table and therefore no call.

        if (!await clinicPatients.DeleteAsync(request.ClinicPatientId, cancellationToken))
        {
            // The chart was there a moment ago, so this is a concurrent delete. Node's
            // `tx.clinicPatient.delete` raises P2025 for a vanished row, which its catch turns
            // into the same named 500 the guard produces here - never a late 404.
            throw new InvalidOperationException(
                $"Clinic patient {request.ClinicPatientId} passed the ownership check and was "
                + "then gone before the delete; connections.js:885 raises P2025 into the route's "
                + "own 500.");
        }

        return new ConnClinicPatientDeleteResponse(
            Success: true,

            // `${patient.name} deleted successfully` (L888) - from the row loaded above.
            Message: $"{chart.Name} deleted successfully");
    }
}
