using System.Globalization;
using System.Text.Json;
using Tebrazi.Patients.Application.Abstractions.Persistence;
using Tebrazi.Patients.Application.ApiModels.Responses;
using Tebrazi.Patients.Application.Services;
using Tebrazi.Patients.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Patients.Application.UseCases;

// ═════════════════════════════════════════════════════════════════════════════
//  The three patient-facing AGGREGATE endpoints of server/src/routes/patients.js:
//
//      GET /api/patients/medications/reconciled  (L536-L663)
//      GET /api/patients/dashboard               (L664-L916)
//      GET /api/patients/health-summary          (L917-L1143)
//
//  They were deferred until Visits, Appointments and Prescriptions existed, because each of
//  them reads across all three. Facts that hold for all of them, stated once:
//
//  * All three are READS, so per the house rule each handler injects ICurrentUser and resolves
//    the caller itself rather than taking a user id on the request record.
//  * NO module DbContext but Patients' own is touched. Visits, Appointments and Prescriptions
//    are reached ONLY through IVisitDirectory / IAppointmentDirectory / IPrescriptionDirectory,
//    users through IIdentityDirectory and clinics through IClinicDirectory.
//  * The Patients context's soft-delete query filter STAYS ON for every read here, and that is
//    the faithful choice even though not one query in patients.js mentions `deletedAt`. Node
//    HARD-deletes allergies, conditions and medications (`prisma.allergy.delete`, L358), so the
//    column is never set on a live Node row and its "no filter" is indistinguishable from a
//    filter. This port soft-deletes instead, so the filter is what makes a removed medication
//    stay removed. IgnoreQueryFilters here would resurrect deleted rows into the dashboard
//    counts — the opposite of matching Node.
//  * The prescriptions' `medications` column is opaque JSON and is READ, never re-serialized:
//    each drug line is picked apart into the flat entries these routes emit, exactly as the Node
//    `.forEach(m => …)` does, and PatientOverviewJs reproduces the JavaScript truthiness that
//    governs every one of those fallbacks.
//  * Each route has its OWN named 500 literal, so each handler is wrapped end to end by
//    PatientOverviewGuard.RunAsync: Handle delegates to HandleCore, an AppException passes
//    through untouched so the deliberate 400/404 keep their bodies, and anything else is logged
//    the way Node logs it and rethrown as that route's 500.
//
//  ROUTE ORDER, for the controller: `dashboard`, `health-summary` and `medications/reconciled`
//  are literals and must be registered BEFORE `{userId}/family-members`, which is deliberately
//  last in PatientsController.
//
//  TWO THINGS ARE NOT REPRODUCED, both because the module they read does not exist yet, and
//  both are called out again at the code that emits their placeholder:
//
//    1. `dashboard.doctors[]` and `stats.totalDoctors` (patients.js:702-730) read
//       DoctorPatientConnection. The Connections module is "not started"
//       (docs/PORT-STATUS.md:23) and no port exposes it. Emitted as [] and 0. This is a REAL
//       gap — the Node query has no `.catch`, so live Node returns populated data here.
//    2. `dashboard.reminders[]` (patients.js:822-832) reads Reminder. That one is NOT a gap:
//       see the note at the emit site — the Node query is permanently broken and always
//       returns [].
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The error bodies of the three aggregate routes, as kernel exceptions.
///
/// <para>Every error body in patients.js is a bare <c>{ "error": "…" }</c> with no
/// <c>message</c> and no <c>details</c>, which is why nothing here reaches for
/// <see cref="ValidationException"/>: it would prepend a "Validation failed" label and push the
/// real text into a second key the React client does not read.</para>
/// </summary>
internal static class PatientOverviewErrors
{
    /// <summary>
    /// A bare <c>{ "error": message }</c> at an arbitrary status.
    /// <see cref="BusinessException"/> drops the <c>message</c> key when it equals the error
    /// label, which is what produces the single-key body.
    /// </summary>
    /// <param name="message">The exact literal from the Node route.</param>
    /// <param name="statusCode">The exact status from the Node route.</param>
    /// <returns>The exception to throw.</returns>
    public static BusinessException Node(string message, int statusCode)
        => new(message, message, statusCode);

    /// <summary>400 with a bare <c>{ "error": message }</c>.</summary>
    /// <param name="message">The exact literal from the Node route.</param>
    /// <returns>The exception to throw.</returns>
    public static BusinessException BadRequest(string message) => Node(message, 400);

    /// <summary>404 with a bare <c>{ "error": message }</c>.</summary>
    /// <param name="message">The exact literal from the Node route.</param>
    /// <returns>The exception to throw.</returns>
    public static NotFoundException NotFound(string message) => new(message);

    /// <summary>
    /// 500 with a bare <c>{ "error": message }</c> — each route's OWN failure literal, which the
    /// middleware's generic <c>{"error":"Internal Server Error"}</c> would not reproduce.
    /// </summary>
    /// <param name="message">The exact literal from the Node route's catch block.</param>
    /// <returns>The exception to throw.</returns>
    public static BusinessException ServerError(string message) => Node(message, 500);
}

/// <summary>
/// The route-level guard for this file, mapping any unexpected failure onto the route's own 500
/// body.
///
/// <para>Each Node handler is wrapped in a try/catch whose only outcome is a named literal —
/// <c>"Failed to load patient dashboard"</c>, <c>"Failed to generate health summary"</c>,
/// <c>"Failed to load reconciled medications"</c>. The try covers the WHOLE handler, so a
/// failure in the profile lookup, in a cross-module port read or in the summary renderer all
/// answer the same 500 as a failure in the primary query. That is why this wraps
/// <c>HandleCore</c> end to end rather than one statement.</para>
///
/// <para>It also carries the TypeErrors. Node dereferences <c>profile.user.displayName</c> on
/// two of these routes; a user that cannot be resolved is an
/// <see cref="InvalidOperationException"/> here, which lands on the same logged 500 Node
/// produces rather than a body with a blank name.</para>
/// </summary>
internal static class PatientOverviewGuard
{
    /// <summary>
    /// Runs <paramref name="body"/> under the route's catch-all.
    /// </summary>
    /// <typeparam name="THandler">The calling handler, for the logger category.</typeparam>
    /// <typeparam name="TResponse">The route's response type.</typeparam>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="logMessage">The Node log line, verbatim (e.g. "[Patients] Dashboard error").</param>
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
            throw PatientOverviewErrors.ServerError(failureError);
        }
    }

    /// <summary>
    /// The caller id. <c>401 {"error":"No token provided"}</c> is the literal <c>authCheck</c>
    /// body, which is why this is a <see cref="BusinessException"/> rather than
    /// <see cref="UnauthorizedException"/> (that one emits a two-key body no patients route
    /// produces). Unreachable behind <c>[Authorize]</c>, and kept so a handler cannot read a null
    /// id as a filter value and answer 200 with somebody else's records.
    /// </summary>
    /// <param name="currentUser">The ambient caller.</param>
    /// <returns>The caller's user id.</returns>
    public static string RequireUserId(ICurrentUser currentUser)
        => string.IsNullOrEmpty(currentUser.UserId)
            ? throw PatientOverviewErrors.Node("No token provided", 401)
            : currentUser.UserId;
}

/// <summary>
/// The JavaScript semantics these three routes depend on. Every helper here exists because a
/// straight C# translation of the Node expression would be subtly different.
/// </summary>
internal static class PatientOverviewJs
{
    /// <summary>
    /// JavaScript truthiness for a string: the empty string is FALSY, so <c>x || 'Doctor'</c>
    /// falls back on <c>""</c> and not only on null.
    /// </summary>
    /// <param name="value">The candidate.</param>
    /// <returns>The value when truthy, otherwise null.</returns>
    public static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// The drug lines inside a prescription's opaque <c>medications</c> column, as
    /// <c>Array.isArray(rx.medications) ? rx.medications : []</c> does: a non-array value — an
    /// object, a string, SQL null — yields no lines rather than an error.
    ///
    /// <para>Elements are cloned so they outlive the <see cref="JsonDocument"/>. Non-object
    /// elements are kept, not skipped: Node iterates them too and every property read on them
    /// comes back <c>undefined</c>, which is what <see cref="ReadString"/> returns for them.</para>
    /// </summary>
    /// <param name="medicationsJson">The stored column, verbatim.</param>
    /// <returns>The drug lines, in stored order.</returns>
    public static IReadOnlyList<JsonElement> DrugLines(string? medicationsJson)
    {
        if (string.IsNullOrWhiteSpace(medicationsJson)) return [];

        try
        {
            using var document = JsonDocument.Parse(medicationsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var lines = new List<JsonElement>();
            foreach (var element in document.RootElement.EnumerateArray())
                lines.Add(element.Clone());

            return lines;
        }
        catch (JsonException)
        {
            // Node would never reach a parse error — Prisma hands it a parsed value — so this is
            // the port's own guard against a column that was written outside the application.
            return [];
        }
    }

    /// <summary>
    /// A drug line's property as a TRUTHY string, for the <c>m.dosage || null</c> family.
    ///
    /// <para>Only a JSON string counts. A number or a boolean would be truthy in JavaScript and
    /// would then reach <c>drugName.toLowerCase()</c>, which throws a TypeError into the route's
    /// catch and answers the route's 500 — so treating a non-string as absent is the one place
    /// this port is deliberately kinder than Node, and it is unreachable from any client that
    /// writes a prescription through the API.</para>
    /// </summary>
    /// <param name="line">The drug line.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The string when present, non-empty and a string; otherwise null.</returns>
    public static string? ReadString(JsonElement line, string name)
        => line.ValueKind == JsonValueKind.Object
            && line.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? Truthy(value.GetString())
                : null;

    /// <summary>
    /// True when a drug line's property is TRUTHY in JavaScript — the <c>!m.stoppedAt</c> test
    /// on the health summary. Any object, array, non-empty string, non-zero number or
    /// <c>true</c> counts; an absent key, null, false, 0 and "" do not.
    /// </summary>
    /// <param name="line">The drug line.</param>
    /// <param name="name">The property name.</param>
    /// <returns>Whether the property is truthy.</returns>
    public static bool IsTruthy(JsonElement line, string name)
    {
        if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty(name, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.String => value.GetString() is { Length: > 0 },
            JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
            _ => true
        };
    }

    /// <summary>
    /// A drug line's property as JavaScript would splice it into a template literal.
    ///
    /// <para>Needed for the reconciled entry's id, <c>`rx-${rx.id}-${m.drugName}`</c>, which
    /// interpolates the RAW value rather than the <c>|| 'Unknown'</c> fallback used for the
    /// <c>drugName</c> field. A line carrying only <c>name</c> therefore produces an id ending
    /// in the literal five characters <c>undefined</c> — a quirk, and a reproduced one, because
    /// the id is a React key and changing it would change client behaviour.</para>
    /// </summary>
    /// <param name="line">The drug line.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The interpolated text.</returns>
    public static string Interpolate(JsonElement line, string name)
    {
        if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty(name, out var value))
            return "undefined";

        return value.ValueKind switch
        {
            JsonValueKind.Undefined => "undefined",
            JsonValueKind.Null => "null",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => value.GetString() ?? string.Empty,

            // String(number) in V8 is the shortest round-trip form, which is what "R" produces
            // for every value a prescription actually carries.
            JsonValueKind.Number => value.TryGetDouble(out var number)
                ? number.ToString("R", CultureInfo.InvariantCulture)
                : value.GetRawText(),

            // String([a,b]) joins with commas; String({}) is the literal "[object Object]".
            JsonValueKind.Array => string.Join(
                ",", value.EnumerateArray().Select(ElementToString)),
            _ => "[object Object]"
        };
    }

    /// <summary>
    /// The Node <c>stripDr</c> (patients.js:1046), which removes a leading
    /// <c>/^Dr\.?\s*/i</c> so the templates below never print "Dr. Dr Ahmed".
    ///
    /// <para>Hand-written rather than a <c>Regex</c> because the quirk has to survive: the
    /// pattern is NOT word-anchored, so "Drew Smith" loses its first two letters and becomes
    /// "ew Smith". A falsy name yields "Unknown".</para>
    /// </summary>
    /// <param name="name">The stored doctor name.</param>
    /// <returns>The name without its title, or "Unknown".</returns>
    public static string StripDr(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "Unknown";

        if (name.Length < 2 ||
            !(name[0] is 'D' or 'd') ||
            !(name[1] is 'R' or 'r'))
        {
            return name;
        }

        var index = 2;
        if (index < name.Length && name[index] == '.') index++;
        while (index < name.Length && char.IsWhiteSpace(name[index])) index++;

        return name[index..];
    }

    /// <summary>
    /// <c>toLocaleDateString('en-US', { year:'numeric', month:'long', day:'numeric' })</c> —
    /// "September 6, 2026".
    /// </summary>
    /// <param name="value">The instant to render.</param>
    /// <returns>The formatted date.</returns>
    public static string LongDate(DateTime value)
        => value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>toLocaleDateString('en-US', { year:'numeric', month:'short', day:'numeric' })</c> —
    /// "Sep 6, 2026".
    /// </summary>
    /// <param name="value">The instant to render.</param>
    /// <returns>The formatted date.</returns>
    public static string ShortDate(DateTime value)
        => value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>new Date().toISOString()</c> — always exactly three fractional digits and a literal
    /// "Z", which the default DateTime converter would not guarantee.
    /// </summary>
    /// <param name="value">The instant to render.</param>
    /// <returns>The ISO-8601 text.</returns>
    public static string IsoString(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string ElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.TryGetDouble(out var number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : element.GetRawText(),
        JsonValueKind.Array => string.Join(",", element.EnumerateArray().Select(ElementToString)),
        _ => "[object Object]"
    };
}

/// <summary>
/// The account-wide picture the three routes each start from: the profile, every dependant, and
/// the health records of both, partitioned the way Node's <c>include</c> tree partitions them.
///
/// <para>Node gets the account holder's rows and each dependant's rows from ONE nested include.
/// The Patients stores read the union in one query per record type, so this splits the union
/// back into the two collections each route reads. A row belongs to exactly one owner — the Node
/// writers store <c>patientProfileId: subprofileId ? null : profile.id</c> (patients.js:324) —
/// so the split is clean.</para>
/// </summary>
internal sealed class PatientAccount
{
    private readonly Dictionary<string, List<Allergy>> allergiesBySubprofile;
    private readonly Dictionary<string, List<ChronicCondition>> conditionsBySubprofile;
    private readonly Dictionary<string, List<CurrentMedication>> medicationsBySubprofile;
    private readonly Dictionary<string, string> subprofileNames;

    /// <summary>
    /// Partitions the three unions once. The lookups below are then free, which matters because
    /// <see cref="MemberName"/> is called per visit, per prescription and per drug line.
    /// </summary>
    /// <param name="profile">The account holder's profile row.</param>
    /// <param name="subprofiles">Every dependant, deactivated ones included.</param>
    /// <param name="allergies">The union of the account holder's and the dependants' allergies.</param>
    /// <param name="conditions">The same union for conditions, unfiltered by <c>isActive</c>.</param>
    /// <param name="medications">The same union for medications, unfiltered by <c>isActive</c>.</param>
    public PatientAccount(
        PatientProfile profile,
        IReadOnlyList<FamilySubprofile> subprofiles,
        IReadOnlyList<Allergy> allergies,
        IReadOnlyList<ChronicCondition> conditions,
        IReadOnlyList<CurrentMedication> medications)
    {
        Profile = profile;
        Subprofiles = subprofiles;
        Medications = medications;

        OwnAllergies = [.. allergies.Where(a => a.PatientProfileId == profile.Id)];
        OwnConditions = [.. conditions.Where(c => c.PatientProfileId == profile.Id)];
        OwnMedications = [.. medications.Where(m => m.PatientProfileId == profile.Id)];

        allergiesBySubprofile = Partition(allergies, a => a.SubprofileId);
        conditionsBySubprofile = Partition(conditions, c => c.SubprofileId);
        medicationsBySubprofile = Partition(medications, m => m.SubprofileId);

        subprofileNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var subprofile in subprofiles) subprofileNames[subprofile.Id] = subprofile.Name;
    }

    /// <summary>The account holder's profile row.</summary>
    public PatientProfile Profile { get; }

    /// <summary>Every dependant on the account, deactivated ones included.</summary>
    public IReadOnlyList<FamilySubprofile> Subprofiles { get; }

    /// <summary>The whole union of medications, both owners, unfiltered by <c>isActive</c>.</summary>
    public IReadOnlyList<CurrentMedication> Medications { get; }

    /// <summary>The account holder's OWN allergies — Node's <c>profile.allergies</c>.</summary>
    public IReadOnlyList<Allergy> OwnAllergies { get; }

    /// <summary>The account holder's OWN conditions, unfiltered by <c>isActive</c>.</summary>
    public IReadOnlyList<ChronicCondition> OwnConditions { get; }

    /// <summary>The account holder's OWN medications, unfiltered by <c>isActive</c>.</summary>
    public IReadOnlyList<CurrentMedication> OwnMedications { get; }

    /// <summary>One dependant's allergies — Node's <c>subprofile.allergies</c>.</summary>
    /// <param name="subprofileId">The dependant.</param>
    /// <returns>Their allergies, empty when they have none.</returns>
    public IReadOnlyList<Allergy> AllergiesOf(string subprofileId)
        => allergiesBySubprofile.TryGetValue(subprofileId, out var rows) ? rows : [];

    /// <summary>One dependant's conditions, unfiltered by <c>isActive</c>.</summary>
    /// <param name="subprofileId">The dependant.</param>
    /// <returns>Their conditions, empty when they have none.</returns>
    public IReadOnlyList<ChronicCondition> ConditionsOf(string subprofileId)
        => conditionsBySubprofile.TryGetValue(subprofileId, out var rows) ? rows : [];

    /// <summary>One dependant's medications, unfiltered by <c>isActive</c>.</summary>
    /// <param name="subprofileId">The dependant.</param>
    /// <returns>Their medications, empty when they have none.</returns>
    public IReadOnlyList<CurrentMedication> MedicationsOf(string subprofileId)
        => medicationsBySubprofile.TryGetValue(subprofileId, out var rows) ? rows : [];

    /// <summary>
    /// The Node <c>subprofile?.name || 'Self'</c>, for a row that may name no dependant or one
    /// this account cannot see.
    ///
    /// <para>Resolved over ALL dependants including deactivated ones: a visit or a prescription
    /// reaches its dependant through the relation, which no <c>isActive</c> filter touches.</para>
    /// </summary>
    /// <param name="subprofileId">The dependant id on the row, possibly null.</param>
    /// <returns>The dependant's name, or "Self".</returns>
    public string MemberName(string? subprofileId)
        => subprofileId is not null && subprofileNames.TryGetValue(subprofileId, out var name)
            ? PatientOverviewJs.Truthy(name) ?? "Self"
            : "Self";

    /// <summary>
    /// Groups the union's dependant-owned rows by owner, preserving the store's ordering within
    /// each group. Rows with no dependant belong to the account holder and are skipped.
    /// </summary>
    private static Dictionary<string, List<T>> Partition<T>(
        IReadOnlyList<T> rows, Func<T, string?> ownerOf)
    {
        var groups = new Dictionary<string, List<T>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (ownerOf(row) is not { } owner) continue;

            if (!groups.TryGetValue(owner, out var group))
                groups[owner] = group = [];

            group.Add(row);
        }

        return groups;
    }
}

// ── GET /api/patients/dashboard ──────────────────────────────────────────────

/// <summary>
/// Everything the patient home screen needs, in one call. Takes no parameters — the caller comes
/// from <see cref="ICurrentUser"/>.
/// </summary>
public sealed record GetPatientDashboardQuery : IRequest<PatientDashboardResponse>;

/// <summary>
/// Port of <c>GET /api/patients/dashboard</c> (patients.js:664-916).
///
/// <para><b>A patient with no profile row gets 200, not 404</b> (patients.js:688-698): a fully
/// shaped empty body with <c>profile: null</c>, six empty arrays, four zeroed counters, and NO
/// <c>upcomingFollowUps</c> key. Every new patient's first load takes that branch, and the
/// profile is NOT created on the way through — this route uses <c>findUnique</c>, unlike
/// <c>GET /profile</c> which upserts.</para>
/// </summary>
public sealed class GetPatientDashboardHandler(
    PatientProfileResolver resolver,
    IHealthRecordStore records,
    IFamilySubprofileStore subprofiles,
    IIdentityDirectory identity,
    IConnectionDirectory connections,
    IClinicDirectory clinics,
    IVisitDirectory visits,
    IAppointmentDirectory appointments,
    IPrescriptionDirectory prescriptions,
    ICurrentUser currentUser,
    IAppLogger<GetPatientDashboardHandler> logger)
    : IRequestHandler<GetPatientDashboardQuery, PatientDashboardResponse>
{
    /// <summary>The prescription statuses the dashboard's medication strip shows (patients.js:746).</summary>
    private static readonly string[] DispensableStatuses = ["CONFIRMED", "SENT", "DISPENSED"];

    /// <summary>Patients see only finished visits, never IN_PROGRESS ones (patients.js:806).</summary>
    private static readonly string[] PatientVisibleStatuses = ["COMPLETED", "ARCHIVED"];

    public Task<PatientDashboardResponse> Handle(
        GetPatientDashboardQuery request, CancellationToken cancellationToken = default)
        => PatientOverviewGuard.RunAsync(
            logger, "[Patients] Dashboard error", "Failed to load patient dashboard",
            cancellationToken, () => HandleCore(cancellationToken));

    private async Task<PatientDashboardResponse> HandleCore(CancellationToken cancellationToken)
    {
        var userId = PatientOverviewGuard.RequireUserId(currentUser);
        var now = DateTime.UtcNow;

        var profile = await resolver.FindAsync(userId, cancellationToken);

        // patients.js:688-698 — the early return. Note the SHAPE: `medications` and the other
        // five arrays are present and empty, `stats` is present with four zeroes, and
        // `upcomingFollowUps` is ABSENT, which is what the null below encodes.
        if (profile is null)
        {
            return new PatientDashboardResponse(
                Profile: null,
                Doctors: [],
                Medications: [],
                UpcomingAppointments: [],
                RecentVisits: [],
                FamilyMembers: [],
                Reminders: [],
                Stats: new PatientDashboardStats(0, 0, 0, 0),
                UpcomingFollowUps: null);
        }

        // The dashboard's include filters subprofiles to isActive (patients.js:673), and that
        // filtered set drives both `allSubprofileIds` and `familyMembers`. ALL dependants are
        // still loaded, because a visit may name one that has since been deactivated and
        // `v.subprofile?.name` would still resolve it.
        var allSubprofiles = await subprofiles.ListAllAsync(profile.Id, cancellationToken);
        var activeSubprofiles = allSubprofiles.Where(s => s.IsActive).ToList();
        var activeSubprofileIds = activeSubprofiles.Select(s => s.Id).ToArray();

        var account = new PatientAccount(
            profile,
            allSubprofiles,
            await records.ListAllergiesForAccountAsync(profile.Id, activeSubprofileIds, ct: cancellationToken),
            await records.ListConditionsForAccountAsync(profile.Id, activeSubprofileIds, ct: cancellationToken),
            // createdAt DESC is deliberate here: the medication strip below is the Node query at
            // patients.js:730-741, which pins `orderBy: { createdAt: 'desc' }` before its take 10.
            // The allergy and condition sets above feed COUNTS only, so their order is inert.
            await records.ListMedicationsForAccountAsync(profile.Id, activeSubprofileIds, ct: cancellationToken));

        // patients.js:730-741 — active only, createdAt DESC, take 10. The store already returns
        // the OR-set newest first, so the filter and the cap finish the query in memory.
        var selfMedications = account.Medications.Where(m => m.IsActive).Take(10).ToList();

        var visitIds = await visits.ListVisitIdsForPatientAsync(userId, null, cancellationToken);

        var prescribed = await prescriptions.ListByVisitIdsAsync(
            visitIds, DispensableStatuses, 10, cancellationToken);

        // patients.js:784-800 ends this read with `.catch(() => [])`, and the comment above it
        // (patients.js:776-783) states the intent: now that the query itself is correct, the
        // catch is "genuine graceful degradation for a transient failure rather than a permanent
        // mask". So a failing appointments read must still answer 200 with the rest of the
        // dashboard and an empty widget, NOT the route's 500. A client disconnect keeps
        // propagating, exactly as PatientOverviewGuard.RunAsync treats it.
        IReadOnlyList<PatientAppointmentRow> upcoming;
        try
        {
            upcoming = await appointments.ListUpcomingForPatientAsync(userId, 5, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Error("[Patients] Dashboard upcoming appointments", exception);
            upcoming = [];
        }

        var recentVisits = await visits.ListRecentForPatientAsync(
            userId, PatientVisibleStatuses, excludePatientDismissed: true, limit: 5,
            ct: cancellationToken);

        // One lookup for every physician any of the three lists names. Node issues three separate
        // includes; the values they resolve to are identical, and one round trip is not.
        var physicians = await identity.GetPhysiciansAsync(
            [.. prescribed.Select(p => p.PhysicianId)
                .Concat(upcoming.Select(a => a.PhysicianId))
                .Concat(recentVisits.Select(v => v.PhysicianId))
                .Distinct()],
            cancellationToken);

        var clinicsById = await clinics.GetClinicsAsync(
            [.. upcoming.Select(a => a.ClinicId).Distinct()], cancellationToken);

        var user = await identity.GetUserAsync(profile.UserId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Patient profile {profile.Id} references user {profile.UserId}, which could not "
                + "be resolved through IIdentityDirectory. Node dereferences "
                + "profile.user.displayName (patients.js:870) and raises a TypeError here.");

        var medications = BuildMedications(selfMedications, prescribed, physicians);

        var familyMembers = new List<PatientDashboardFamilyMember>();
        foreach (var member in activeSubprofiles.Where(s => s.Relation != SubprofileRelation.SELF))
        {
            // Node reads `_count: { select: { visits: true } }` on the include. No port exposes a
            // visit count per dependant, so it is derived from the ids the Visits port already
            // scopes by subprofile. One query per family member, and family members are few.
            var memberVisitIds = await visits.ListVisitIdsForPatientAsync(
                userId, member.Id, cancellationToken);

            familyMembers.Add(new PatientDashboardFamilyMember(
                member.Id,
                member.Name,
                member.Relation.ToString(),
                member.Gender?.ToString(),
                account.AllergiesOf(member.Id).Count,
                account.ConditionsOf(member.Id).Count(c => c.IsActive),
                account.MedicationsOf(member.Id).Count(m => m.IsActive),
                memberVisitIds.Count));
        }

        // patients.js:850-852 — its own count query over ALL statuses with no dismissed clause
        // and no deletedAt clause, so it legitimately exceeds recentVisits.Count. Substituting
        // the list length would under-report every patient with more than five visits.
        var totalVisits = await visits.CountForPatientAsync(userId, cancellationToken);

        var dashboardVisits = recentVisits
            .Select(v => new PatientDashboardVisit(
                v.Id,
                v.VisitDate,
                DoctorName(physicians, v.PhysicianId),
                v.ChiefComplaint,
                v.Diagnosis,
                v.FollowUpDate,
                v.FollowUpNotes,
                account.MemberName(v.SubprofileId)))
            .ToList();

        // patients.js:702-730 — the ACCEPTED connections, newest first, via the published
        // IConnectionDirectory. Status-only filter per Node: one doctor connected for self and
        // two dependants is THREE rows, each its own card with its own forMember.
        var acceptedConnections = await connections.ListAcceptedDoctorsForPatientAsync(userId, cancellationToken);

        var doctors = new List<PatientDashboardDoctor>(acceptedConnections.Count);
        foreach (var connection in acceptedConnections)
        {
            // patients.js:719-723. Node's include carries `physicianUser: { id, displayName,
            // physicianProfile: { specialty, verified } }` and then dereferences
            // `c.physicianUser.id` with no guard — a dangling physicianUserId raises a TypeError
            // into the route's 500. Throwing here reproduces that; the guard wrapper answers
            // the same body.
            var physicianUser = await identity.GetUserAsync(connection.PhysicianUserId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"DoctorPatientConnection references user {connection.PhysicianUserId}, which could not "
                    + "be resolved through IIdentityDirectory. Node dereferences "
                    + "c.physicianUser.id (patients.js:719) and raises a TypeError here.");

            // patients.js:722-723 — `?.specialty || 'General'` and `?.verified || false` for a
            // user with no PhysicianProfile row.
            var physicianProfile = await identity.GetPhysicianByUserIdAsync(connection.PhysicianUserId, cancellationToken);

            doctors.Add(new PatientDashboardDoctor(
                Id: connection.PhysicianUserId,
                Name: physicianUser.DisplayName,
                Specialty: PatientOverviewJs.Truthy(physicianProfile?.Specialty) ?? "General",
                Verified: physicianProfile?.Verified ?? false,
                ConnectedAt: connection.ConnectedAt,
                ForMember: account.MemberName(connection.SubprofileId)));
        }

        var totalDoctors = acceptedConnections
            .Select(c => c.PhysicianUserId)
            .Distinct()
            .Count();

        return new PatientDashboardResponse(
            Profile: new PatientDashboardProfile(
                user.DisplayName,
                user.Email,
                profile.BloodType,
                profile.Gender?.ToString(),
                profile.DateOfBirth,
                account.OwnAllergies.Count,
                account.OwnConditions.Count(c => c.IsActive),
                account.OwnMedications.Count(m => m.IsActive)),

            // patients.js:702-730 — resolved above from IConnectionDirectory. Landed
            // 2026-09-18; the field had been empty since the module's first pass because
            // Connections was not yet ported, though `IConnectionDirectory` was published and
            // implemented on 2026-09-11. Note `ConnectedAt` is DateTime? — a nullable column,
            // and Node emits `connectedAt: null` for an accepted row that predates the stamp.
            Doctors: doctors,

            Medications: medications,

            // The FIXED patients.js:768-800: appointmentDate >= now, status PENDING or CONFIRMED,
            // appointmentDate ASC, take 5 — done inside the port — and the element shape the
            // client has always read. The old query filtered `date` and matched 'SCHEDULED', so
            // it threw into a .catch and this array was permanently empty.
            UpcomingAppointments: [.. upcoming.Select(a => new PatientDashboardAppointment(
                a.Id,
                a.AppointmentDate,
                a.StartTime,
                a.EndTime,
                clinicsById.TryGetValue(a.ClinicId, out var clinic)
                    ? PatientOverviewJs.Truthy(clinic.Name)
                    : null,
                DoctorName(physicians, a.PhysicianId),
                PatientOverviewJs.Truthy(a.AppointmentType) ?? "IN_PERSON",
                a.Status))],

            RecentVisits: dashboardVisits,
            FamilyMembers: familyMembers,

            // patients.js:822-838. NOT a porting gap: the Node query filters on `isActive` and
            // `dueDate`, and the Prisma Reminder model (schema.prisma:1518-1543) has neither —
            // its fields are `status` and `remindAt`. Every call raises a
            // PrismaClientValidationError into the trailing `.catch(() => [])`, so live Node
            // returns [] here for every patient on every request, and the mapping below it reads
            // `r.dueDate`, which is not a column either. [] IS the contract.
            Reminders: [],

            Stats: new PatientDashboardStats(
                // patients.js:855 — `new Set(connections.map(c => c.physicianUser.id)).size`,
                // computed above over the SAME list as doctors[], so both agree by construction.
                TotalDoctors: totalDoctors,
                TotalVisits: totalVisits,
                // The MERGED array's length — self-reported plus every flattened drug line — not
                // either half's, and capped in practice by the two takes above.
                ActiveMedications: medications.Count,
                FamilyMembers: familyMembers.Count),

            // patients.js:903-915 — derived from the SAME five visits, never re-queried, kept
            // only where a follow-up date exists, and re-sorted ASCENDING.
            UpcomingFollowUps: recentVisits
                .Where(v => v.FollowUpDate.HasValue)
                .Select(v => new PatientDashboardFollowUp(
                    v.Id,
                    v.FollowUpDate!.Value,
                    v.FollowUpNotes,
                    DoctorName(physicians, v.PhysicianId),
                    v.ChiefComplaint,
                    v.FollowUpDate!.Value < now,
                    account.MemberName(v.SubprofileId)))
                .OrderBy(f => f.FollowUpDate)
                .ToList());
    }

    /// <summary>
    /// The merged medication strip (patients.js:752-766). The two halves have DIFFERENT key sets
    /// — the self-reported half is a spread of the whole row, the prescribed half is six hand-
    /// picked keys — so they stay separate types in one <c>object</c> list.
    /// </summary>
    private static List<object> BuildMedications(
        IReadOnlyList<CurrentMedication> selfMedications,
        IReadOnlyList<PatientPrescriptionRow> prescribed,
        IReadOnlyDictionary<string, PhysicianSummary> physicians)
    {
        var medications = new List<object>();

        foreach (var medication in selfMedications)
        {
            medications.Add(new PatientDashboardSelfMedication(
                medication.Id,
                medication.PatientProfileId,
                medication.SubprofileId,
                medication.DrugName,
                medication.Dosage,
                medication.Frequency,
                medication.PrescribedBy,
                medication.StartDate,
                medication.EndDate,
                medication.IsActive,
                medication.CreatedAt,
                medication.UpdatedAt,
                // Always null: the context's soft-delete filter is on, which is how this port
                // reproduces Node's hard delete. See the file header.
                medication.DeletedAt,
                "SELF_REPORTED"));
        }

        foreach (var prescription in prescribed)
        {
            var doctorName = DoctorName(physicians, prescription.PhysicianId);

            foreach (var line in PatientOverviewJs.DrugLines(prescription.Medications))
            {
                medications.Add(new PatientDashboardPrescribedMedication(
                    // The raw interpolation, deliberately not the `|| 'Unknown'` value below.
                    $"rx-{prescription.Id}-{PatientOverviewJs.Interpolate(line, "drugName")}",
                    // NOTE: no `m.name` fallback here, unlike /medications/reconciled, which has
                    // one. The two routes differ and both are reproduced as written.
                    PatientOverviewJs.ReadString(line, "drugName") ?? "Unknown",
                    PatientOverviewJs.ReadString(line, "dosage"),
                    PatientOverviewJs.ReadString(line, "frequency"),
                    "PRESCRIBED",
                    doctorName));
            }
        }

        return medications;
    }

    /// <summary>
    /// <c>physician?.user?.displayName || 'Doctor'</c> — the dashboard's fallback, which is
    /// "Doctor" here and "Unknown" on the health summary.
    /// </summary>
    private static string DoctorName(
        IReadOnlyDictionary<string, PhysicianSummary> physicians, string physicianId)
        => physicians.TryGetValue(physicianId, out var physician)
            ? PatientOverviewJs.Truthy(physician.DisplayName) ?? "Doctor"
            : "Doctor";
}

// ── GET /api/patients/health-summary ─────────────────────────────────────────

/// <summary>
/// A print-ready Markdown summary of the patient's record. Takes no parameters.
/// </summary>
public sealed record GetPatientHealthSummaryQuery : IRequest<PatientHealthSummaryResponse>;

/// <summary>
/// Port of <c>GET /api/patients/health-summary</c> (patients.js:917-1143).
///
/// <para>The body is one Markdown string, so the LINE ASSEMBLY is the contract: which sections
/// appear, in what order, with which blank lines between them, and which lines are suppressed
/// when their field is empty. The section builders below follow patients.js statement for
/// statement.</para>
///
/// <para>Unlike the dashboard, an absent profile is a <b>404</b> here (patients.js:951).</para>
/// </summary>
public sealed class GetPatientHealthSummaryHandler(
    PatientProfileResolver resolver,
    IHealthRecordStore records,
    IFamilySubprofileStore subprofiles,
    IExternalCareStore externalCare,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IVisitDirectory visits,
    IPrescriptionDirectory prescriptions,
    ICurrentUser currentUser,
    IAppLogger<GetPatientHealthSummaryHandler> logger)
    : IRequestHandler<GetPatientHealthSummaryQuery, PatientHealthSummaryResponse>
{
    /// <summary>
    /// The summary shows IN_PROGRESS visits too (patients.js:956) — a consultation still under
    /// way is context a new doctor wants. The dashboard deliberately does not.
    /// </summary>
    private static readonly string[] SummaryVisitStatuses = ["COMPLETED", "IN_PROGRESS"];

    /// <summary>
    /// FOUR statuses, one more than the dashboard's three: a SIGNED prescription counts as
    /// current medication here even before it has been sent (patients.js:1012).
    /// </summary>
    private static readonly string[] ActiveRxStatuses = ["SIGNED", "CONFIRMED", "SENT", "DISPENSED"];

    public Task<PatientHealthSummaryResponse> Handle(
        GetPatientHealthSummaryQuery request, CancellationToken cancellationToken = default)
        => PatientOverviewGuard.RunAsync(
            logger, "[Patients] Health summary error", "Failed to generate health summary",
            cancellationToken, () => HandleCore(cancellationToken));

    private async Task<PatientHealthSummaryResponse> HandleCore(CancellationToken cancellationToken)
    {
        var userId = PatientOverviewGuard.RequireUserId(currentUser);

        // One instant for the "Generated:" line and for generatedAt. Node calls new Date() twice
        // and can straddle a millisecond; a single stamp is the same value the client sees.
        var now = DateTime.UtcNow;

        var profile = await resolver.FindAsync(userId, cancellationToken)
            ?? throw PatientOverviewErrors.NotFound("Profile not found");

        // NO isActive filter on the subprofiles include here (patients.js:945), unlike the
        // dashboard: a deactivated dependant still appears under "## Family Members".
        var allSubprofiles = await subprofiles.ListAllAsync(profile.Id, cancellationToken);
        var subprofileIds = allSubprofiles.Select(s => s.Id).ToArray();

        // And no isActive filter on conditions or medications either (patients.js:942-944),
        // unlike GET /profile. A resolved condition still belongs in a clinical summary.
        var account = new PatientAccount(
            profile,
            allSubprofiles,
            // newestFirst: false. Unlike /medications/reconciled (patients.js:558) and the
            // dashboard strip (patients.js:730), THIS route pins no order at all — the three
            // collections arrive on a bare nested include (patients.js:942-948), so Prisma emits
            // no ORDER BY and Postgres returns these append-only tables in insertion order,
            // oldest first. Every bullet under "## Allergies", "## Chronic Conditions", the
            // self-reported half of "## Current Medications", and the three comma-joined lists
            // under "## Family Members" is printed in that order, verbatim, into `summary`.
            await records.ListAllergiesForAccountAsync(
                profile.Id, subprofileIds, newestFirst: false, ct: cancellationToken),
            await records.ListConditionsForAccountAsync(
                profile.Id, subprofileIds, newestFirst: false, ct: cancellationToken),
            await records.ListMedicationsForAccountAsync(
                profile.Id, subprofileIds, newestFirst: false, ct: cancellationToken));

        var recentVisits = await visits.ListRecentForPatientAsync(
            userId, SummaryVisitStatuses, excludePatientDismissed: false, limit: 5,
            ct: cancellationToken);

        var visitPhysicians = await identity.GetPhysiciansAsync(
            [.. recentVisits.Select(v => v.PhysicianId).Distinct()], cancellationToken);

        var visitClinics = await clinics.GetClinicsAsync(
            [.. recentVisits.Select(v => v.ClinicId).Distinct()], cancellationToken);

        // patients.js:1082-1088 embeds the visit's prescriptions through a BARE
        // `prescriptions: true` — no status filter at all, so a DRAFT prescription's drugs are
        // printed. IPrescriptionDirectory.ListByVisitIdsAsync REQUIRES statuses and cannot
        // express that, so this uses the unfiltered by-visit read once per visit. Five visits,
        // five queries, and every one of them correct; reusing the status-filtered call would
        // silently drop the DRAFT lines Node prints.
        var visitPrescriptions = new Dictionary<string, IReadOnlyList<PrescriptionRow>>();
        foreach (var visit in recentVisits)
            visitPrescriptions[visit.Id] = await prescriptions.ListByVisitAsync(visit.Id, cancellationToken);

        // The "### Prescribed by Doctors" list is scoped to ALL the patient's visits, not the
        // five above (patients.js:1011).
        var allVisitIds = await visits.ListVisitIdsForPatientAsync(userId, null, cancellationToken);

        var activePrescriptions = await prescriptions.ListByVisitIdsAsync(
            allVisitIds, ActiveRxStatuses, 20, cancellationToken);

        var rxPhysicians = await identity.GetPhysiciansAsync(
            [.. activePrescriptions.Select(p => p.PhysicianId).Distinct()], cancellationToken);

        // take 10 for the medication harvest; the visit section then uses the first 5 of these
        // same rows (patients.js:1033-1037, :1101).
        var externalVisits = await externalCare.ListAllVisitsForPatientAsync(
            userId, 10, cancellationToken);

        var user = await identity.GetUserAsync(profile.UserId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Patient profile {profile.Id} references user {profile.UserId}, which could not "
                + "be resolved through IIdentityDirectory. Node dereferences "
                + "profile.user.displayName (patients.js:973) and raises a TypeError here.");

        // The SELF subprofile, when one exists, HOLDS the account holder's own records and the
        // profile-level collections are ignored entirely (patients.js:965-968). JavaScript's
        // `selfSub?.allergies || profile.allergies` falls through only when selfSub is absent —
        // an EMPTY array is truthy in JS — so this is a null test on the dependant, never a
        // count test on the list.
        var selfSubprofile = account.Subprofiles
            .FirstOrDefault(s => s.Relation == SubprofileRelation.SELF);

        var myAllergies = selfSubprofile is null
            ? account.OwnAllergies
            : account.AllergiesOf(selfSubprofile.Id);

        var myConditions = selfSubprofile is null
            ? account.OwnConditions
            : account.ConditionsOf(selfSubprofile.Id);

        var myMedications = selfSubprofile is null
            ? account.OwnMedications
            : account.MedicationsOf(selfSubprofile.Id);

        var familyMembers = account.Subprofiles
            .Where(s => s.Relation != SubprofileRelation.SELF)
            .ToList();

        var prescribedMeds = BuildPrescribedMeds(activePrescriptions, rxPhysicians);
        var externalMeds = BuildExternalMeds(externalVisits);
        var activeSelfMeds = myMedications.Where(m => m.IsActive).ToList();

        var lines = new List<string>();

        lines.Add($"# Health Summary for {user.DisplayName}");
        lines.Add($"Generated: {PatientOverviewJs.LongDate(now)}");
        lines.Add(string.Empty);

        AppendPersonalInformation(lines, profile, now);
        AppendAllergies(lines, myAllergies);
        AppendConditions(lines, myConditions);
        AppendMedications(lines, activeSelfMeds, prescribedMeds, externalMeds);

        AppendPlatformVisits(lines, recentVisits, visitPhysicians, visitClinics, visitPrescriptions);

        // Reuses the SAME rows already fetched — Node slices the take-10 list rather than
        // re-querying, so the summary shows at most five external visits while all ten
        // contributed their medications above.
        var summarisedExternalVisits = externalVisits.Take(5).ToList();
        AppendExternalVisits(lines, summarisedExternalVisits, recentVisits.Count);

        AppendFamilyMembers(lines, familyMembers, account);

        return new PatientHealthSummaryResponse(
            Summary: string.Join("\n", lines),
            GeneratedAt: PatientOverviewJs.IsoString(now),
            Stats: new PatientHealthSummaryStats(
                Allergies: myAllergies.Count,
                Conditions: myConditions.Count,
                // The sum of the three lists, NOT the length of any one of them.
                Medications: activeSelfMeds.Count + prescribedMeds.Count + externalMeds.Count,
                // Platform visits plus the five self-logged ones actually printed.
                RecentVisits: recentVisits.Count + summarisedExternalVisits.Count,
                FamilyMembers: familyMembers.Count));
    }

    /// <summary>
    /// The whole "## Personal Information" block, which is omitted outright when none of date of
    /// birth, gender and blood type is set (patients.js:983).
    /// </summary>
    private static void AppendPersonalInformation(
        List<string> lines, PatientProfile profile, DateTime now)
    {
        var bloodType = PatientOverviewJs.Truthy(profile.BloodType);

        if (profile.DateOfBirth is null && profile.Gender is null && bloodType is null) return;

        lines.Add("## Personal Information");

        if (profile.DateOfBirth is { } dateOfBirth)
        {
            // Math.floor((Date.now() - dob) / 31557600000) — 365.25 days in milliseconds, so it
            // is a Julian-year age and not a calendar one. Reproduced rather than corrected.
            var age = Math.Floor((now - dateOfBirth).TotalMilliseconds / 31557600000d);
            lines.Add($"- Age: {age.ToString("0", CultureInfo.InvariantCulture)} years");
        }

        if (profile.Gender is { } gender) lines.Add($"- Gender: {gender}");
        if (bloodType is not null) lines.Add($"- Blood Type: {bloodType}");

        if (PatientOverviewJs.Truthy(profile.EmergencyContact) is { } contact)
        {
            var phone = PatientOverviewJs.Truthy(profile.EmergencyPhone) ?? "N/A";
            lines.Add($"- Emergency Contact: {contact} ({phone})");
        }

        lines.Add(string.Empty);
    }

    /// <summary>The "## Allergies" block, which always renders — empty means one placeholder line.</summary>
    private static void AppendAllergies(List<string> lines, IReadOnlyList<Allergy> allergies)
    {
        lines.Add("## Allergies");

        if (allergies.Count > 0)
        {
            foreach (var allergy in allergies)
            {
                var severity = PatientOverviewJs.Truthy(allergy.Severity) is { } value
                    ? $" ({value})"
                    : string.Empty;

                var reaction = PatientOverviewJs.Truthy(allergy.Reaction) is { } text
                    ? $" — Reaction: {text}"
                    : string.Empty;

                lines.Add($"- {allergy.Allergen}{severity}{reaction}");
            }
        }
        else
        {
            lines.Add("- No known allergies");
        }

        lines.Add(string.Empty);
    }

    /// <summary>The "## Chronic Conditions" block. Diagnosed dates print as a YEAR only.</summary>
    private static void AppendConditions(List<string> lines, IReadOnlyList<ChronicCondition> conditions)
    {
        lines.Add("## Chronic Conditions");

        if (conditions.Count > 0)
        {
            foreach (var condition in conditions)
            {
                var since = condition.DiagnosedDate is { } diagnosed
                    ? $" (since {diagnosed.Year.ToString(CultureInfo.InvariantCulture)})"
                    : string.Empty;

                lines.Add($"- {condition.Condition}{since}");
            }
        }
        else
        {
            lines.Add("- No chronic conditions");
        }

        lines.Add(string.Empty);
    }

    /// <summary>
    /// "## Current Medications" and its two optional sub-sections.
    ///
    /// <para>The blank-line rules are load-bearing and asymmetric (patients.js:1057-1071): the
    /// prescribed sub-heading is preceded by a blank line only when self-reported medications
    /// were printed, and the external one only when EITHER of the previous two was. The
    /// "no current medications" line appears only when all three lists are empty.</para>
    /// </summary>
    private static void AppendMedications(
        List<string> lines,
        IReadOnlyList<CurrentMedication> selfMeds,
        IReadOnlyList<SummaryMedication> prescribedMeds,
        IReadOnlyList<SummaryExternalMedication> externalMeds)
    {
        lines.Add("## Current Medications");

        foreach (var medication in selfMeds)
            lines.Add($"- {medication.DrugName}{Detail(medication.Dosage, medication.Frequency)}");

        if (prescribedMeds.Count > 0)
        {
            if (selfMeds.Count > 0) lines.Add(string.Empty);
            lines.Add("### Prescribed by Doctors");

            foreach (var medication in prescribedMeds)
            {
                lines.Add(
                    $"- {medication.DrugName}{Detail(medication.Dosage, medication.Frequency)}"
                    + $" (by Dr. {medication.PrescribedBy})");
            }
        }

        if (externalMeds.Count > 0)
        {
            if (selfMeds.Count > 0 || prescribedMeds.Count > 0) lines.Add(string.Empty);
            lines.Add("### From External Visits (Self-Logged)");

            foreach (var medication in externalMeds)
            {
                lines.Add(
                    $"- {medication.DrugName}{Detail(medication.Dosage, medication.Frequency)}"
                    + $" (Dr. {PatientOverviewJs.StripDr(medication.Doctor)})");
            }
        }

        if (selfMeds.Count == 0 && prescribedMeds.Count == 0 && externalMeds.Count == 0)
            lines.Add("- No current medications");

        lines.Add(string.Empty);
    }

    /// <summary>
    /// The "## Recent Visits" block for platform visits. The whole block, heading included, is
    /// skipped when there are none — and the external-visit block below then prints the heading
    /// itself.
    /// </summary>
    private static void AppendPlatformVisits(
        List<string> lines,
        IReadOnlyList<PatientVisitRow> recentVisits,
        IReadOnlyDictionary<string, PhysicianSummary> physiciansById,
        IReadOnlyDictionary<string, ClinicSummary> clinicsById,
        IReadOnlyDictionary<string, IReadOnlyList<PrescriptionRow>> visitPrescriptions)
    {
        if (recentVisits.Count == 0) return;

        lines.Add("## Recent Visits");

        foreach (var visit in recentVisits)
        {
            physiciansById.TryGetValue(visit.PhysicianId, out var physician);

            // "Unknown" here, NOT the dashboard's "Doctor".
            var doctor = PatientOverviewJs.Truthy(physician?.DisplayName) ?? "Unknown";

            var specialty = PatientOverviewJs.Truthy(physician?.Specialty) is { } value
                ? $" ({value})"
                : string.Empty;

            // `v.clinic ? ` at ${v.clinic.name}` : ''` tests the RELATION, which is a required FK
            // in Node and therefore always present. Here it tests whether IClinicDirectory
            // resolved it, so the segment is dropped only if the clinic row has vanished.
            var clinic = clinicsById.TryGetValue(visit.ClinicId, out var resolved)
                ? $" at {resolved.Name}"
                : string.Empty;

            lines.Add($"### {PatientOverviewJs.ShortDate(visit.VisitDate)} — Dr. {doctor}{specialty}{clinic}");

            if (PatientOverviewJs.Truthy(visit.ChiefComplaint) is { } complaint)
                lines.Add($"- Chief Complaint: {complaint}");

            if (PatientOverviewJs.Truthy(visit.Subjective) is { } subjective)
                lines.Add($"- Subjective: {subjective}");

            if (PatientOverviewJs.Truthy(visit.Assessment) is { } assessment)
                lines.Add($"- Assessment: {assessment}");

            if (PatientOverviewJs.Truthy(visit.Plan) is { } plan)
                lines.Add($"- Plan: {plan}");

            if (visitPrescriptions.TryGetValue(visit.Id, out var rows) && rows.Count > 0)
            {
                var drugs = rows
                    .SelectMany(row => PatientOverviewJs.DrugLines(row.Medications))
                    .Select(line => PatientOverviewJs.ReadString(line, "drugName") ?? "Unknown")
                    .ToList();

                if (drugs.Count > 0) lines.Add($"- Prescriptions: {string.Join(", ", drugs)}");
            }

            lines.Add(string.Empty);
        }
    }

    /// <summary>
    /// The self-logged visits. They share the "## Recent Visits" heading with the platform
    /// visits, and print it themselves only when there were no platform visits at all.
    /// </summary>
    private static void AppendExternalVisits(
        List<string> lines, IReadOnlyList<ExternalVisit> externalVisits, int platformVisitCount)
    {
        if (externalVisits.Count == 0) return;

        if (platformVisitCount == 0) lines.Add("## Recent Visits");

        foreach (var visit in externalVisits)
        {
            var specialty = PatientOverviewJs.Truthy(visit.Specialty) is { } value
                ? $" ({value})"
                : string.Empty;

            var clinic = PatientOverviewJs.Truthy(visit.ClinicName) is { } name
                ? $" at {name}"
                : string.Empty;

            lines.Add(
                $"### {PatientOverviewJs.ShortDate(visit.VisitDate)} — "
                + $"Dr. {PatientOverviewJs.StripDr(visit.DoctorName)}{specialty}{clinic}");

            if (PatientOverviewJs.Truthy(visit.ChiefComplaint) is { } complaint)
                lines.Add($"- Chief Complaint: {complaint}");

            if (PatientOverviewJs.Truthy(visit.Diagnosis) is { } diagnosis)
                lines.Add($"- Diagnosis: {diagnosis}");

            if (PatientOverviewJs.Truthy(visit.Notes) is { } notes)
                lines.Add($"- Notes: {notes}");

            var drugLines = PatientOverviewJs.DrugLines(visit.Medications);

            if (drugLines.Count > 0)
            {
                // `m.drugName || 'Unknown'` with no truthiness filter afterwards — an unnamed
                // line still prints as "Unknown".
                var drugs = drugLines
                    .Select(line => PatientOverviewJs.ReadString(line, "drugName") ?? "Unknown");

                lines.Add($"- Medications: {string.Join(", ", drugs)}");
            }

            lines.Add(string.Empty);
        }
    }

    /// <summary>
    /// The "## Family Members" block. Note it adds NO trailing blank line, so the summary ends
    /// without one whenever the patient has dependants.
    /// </summary>
    private static void AppendFamilyMembers(
        List<string> lines, IReadOnlyList<FamilySubprofile> familyMembers, PatientAccount account)
    {
        if (familyMembers.Count == 0) return;

        lines.Add("## Family Members");

        foreach (var member in familyMembers)
        {
            lines.Add($"### {member.Name} ({member.Relation})");

            var allergies = account.AllergiesOf(member.Id);
            if (allergies.Count > 0)
                lines.Add($"- Allergies: {string.Join(", ", allergies.Select(a => a.Allergen))}");

            var conditions = account.ConditionsOf(member.Id);
            if (conditions.Count > 0)
                lines.Add($"- Conditions: {string.Join(", ", conditions.Select(c => c.Condition))}");

            var medications = account.MedicationsOf(member.Id);
            if (medications.Count > 0)
                lines.Add($"- Medications: {string.Join(", ", medications.Select(m => m.DrugName))}");
        }
    }

    /// <summary>
    /// The shared <c>{dosage}{ — frequency}</c> tail of every medication line. Both segments are
    /// dropped when empty, and the separator before the frequency is an EM DASH.
    /// </summary>
    private static string Detail(string? dosage, string? frequency)
    {
        var dosagePart = PatientOverviewJs.Truthy(dosage) is { } value ? $" {value}" : string.Empty;

        var frequencyPart = PatientOverviewJs.Truthy(frequency) is { } text
            ? $" — {text}"
            : string.Empty;

        return $"{dosagePart}{frequencyPart}";
    }

    /// <summary>
    /// Flattens the active prescriptions into drug lines, keeping only those with a drug name and
    /// WITHOUT a <c>stoppedAt</c> stamp (patients.js:1017-1026) — a stopped line is history, and
    /// this is the only one of the three routes that checks for it.
    /// </summary>
    private static List<SummaryMedication> BuildPrescribedMeds(
        IReadOnlyList<PatientPrescriptionRow> activePrescriptions,
        IReadOnlyDictionary<string, PhysicianSummary> physiciansById)
    {
        var medications = new List<SummaryMedication>();

        foreach (var prescription in activePrescriptions)
        {
            physiciansById.TryGetValue(prescription.PhysicianId, out var physician);
            var doctor = PatientOverviewJs.Truthy(physician?.DisplayName) ?? "Unknown";

            foreach (var line in PatientOverviewJs.DrugLines(prescription.Medications))
            {
                if (PatientOverviewJs.ReadString(line, "drugName") is not { } drugName) continue;
                if (PatientOverviewJs.IsTruthy(line, "stoppedAt")) continue;

                // `m.dosage || ''` — the empty STRING, not null, because these values only ever
                // feed the template above.
                medications.Add(new SummaryMedication(
                    drugName,
                    PatientOverviewJs.ReadString(line, "dosage") ?? string.Empty,
                    PatientOverviewJs.ReadString(line, "frequency") ?? string.Empty,
                    doctor));
            }
        }

        return medications;
    }

    /// <summary>
    /// The same flattening for self-logged external visits (patients.js:1047-1060). No
    /// <c>stoppedAt</c> check here — the Node loop does not have one.
    /// </summary>
    private static List<SummaryExternalMedication> BuildExternalMeds(
        IReadOnlyList<ExternalVisit> externalVisits)
    {
        var medications = new List<SummaryExternalMedication>();

        foreach (var visit in externalVisits)
        {
            foreach (var line in PatientOverviewJs.DrugLines(visit.Medications))
            {
                if (PatientOverviewJs.ReadString(line, "drugName") is not { } drugName) continue;

                medications.Add(new SummaryExternalMedication(
                    drugName,
                    PatientOverviewJs.ReadString(line, "dosage") ?? string.Empty,
                    PatientOverviewJs.ReadString(line, "frequency") ?? string.Empty,
                    PatientOverviewJs.Truthy(visit.DoctorName) ?? "Unknown"));
            }
        }

        return medications;
    }

    /// <summary>One flattened prescribed drug line, as the summary text needs it.</summary>
    private sealed record SummaryMedication(
        string DrugName, string Dosage, string Frequency, string PrescribedBy);

    /// <summary>
    /// One flattened self-logged drug line. Its doctor is free text the patient typed, which is
    /// why it goes through <c>stripDr</c> and the prescribed one does not.
    /// </summary>
    private sealed record SummaryExternalMedication(
        string DrugName, string Dosage, string Frequency, string Doctor);
}

// ── GET /api/patients/medications/reconciled ─────────────────────────────────

/// <summary>
/// The patient's medications from BOTH sources in one list, with duplicates flagged. Takes no
/// parameters.
/// </summary>
public sealed record GetReconciledMedicationsQuery : IRequest<ReconciledMedicationsResponse>;

/// <summary>
/// Port of <c>GET /api/patients/medications/reconciled</c> (patients.js:536-663).
///
/// <para>It is a DIFF, not a list. Both sources are emitted in full — nothing is merged away —
/// and a drug appearing on both sides is marked <c>isDuplicate</c> on EVERY entry that carries
/// its name, so the client can show the conflict rather than pick a winner. The matching rule is
/// the drug name lower-cased and trimmed; dosage, frequency and prescriber are not compared.</para>
///
/// <para>An absent profile is a <b>400</b> here (patients.js:546) — not the dashboard's 200 and
/// not the health summary's 404. All three differ and all three are reproduced.</para>
/// </summary>
public sealed class GetReconciledMedicationsHandler(
    PatientProfileResolver resolver,
    IHealthRecordStore records,
    IFamilySubprofileStore subprofiles,
    IIdentityDirectory identity,
    IVisitDirectory visits,
    IPrescriptionDirectory prescriptions,
    ICurrentUser currentUser,
    IAppLogger<GetReconciledMedicationsHandler> logger)
    : IRequestHandler<GetReconciledMedicationsQuery, ReconciledMedicationsResponse>
{
    /// <summary>The same three statuses the dashboard uses (patients.js:565).</summary>
    private static readonly string[] DispensableStatuses = ["CONFIRMED", "SENT", "DISPENSED"];

    public Task<ReconciledMedicationsResponse> Handle(
        GetReconciledMedicationsQuery request, CancellationToken cancellationToken = default)
        => PatientOverviewGuard.RunAsync(
            logger, "[Patients] Reconciled medications error", "Failed to load reconciled medications",
            cancellationToken, () => HandleCore(cancellationToken));

    private async Task<ReconciledMedicationsResponse> HandleCore(CancellationToken cancellationToken)
    {
        var userId = PatientOverviewGuard.RequireUserId(currentUser);

        var profile = await resolver.FindAsync(userId, cancellationToken)
            ?? throw PatientOverviewErrors.BadRequest("Patient profile not found");

        // ALL dependants, active or not (patients.js:542) — unlike the dashboard, which narrows
        // the same OR-filter to active ones. A deactivated child's medications still reconcile.
        var allSubprofiles = await subprofiles.ListAllAsync(profile.Id, cancellationToken);
        var subprofileIds = allSubprofiles.Select(s => s.Id).ToArray();

        // createdAt DESC, which THIS route genuinely pins (patients.js:558) — the reconciled list
        // is re-sorted by addedAt later, and this ordering is what breaks its ties.
        var account = new PatientAccount(profile, allSubprofiles, [], [],
            await records.ListMedicationsForAccountAsync(
                profile.Id, subprofileIds, ct: cancellationToken));

        var selfReported = account.Medications.Where(m => m.IsActive).ToList();

        var visitIds = await visits.ListVisitIdsForPatientAsync(userId, null, cancellationToken);

        // NO take on the Node query (patients.js:562-576), unlike the dashboard's 10 and the
        // health summary's 20 — the reconciliation view must see every prescription.
        var prescribed = await prescriptions.ListByVisitIdsAsync(
            visitIds, DispensableStatuses, int.MaxValue, cancellationToken);

        // `rx.visit.visitDate` and `rx.visit.chiefComplaint` come from the Visits port: a
        // prescription row has a visit_id and no visit navigation. One batched query for all of
        // them, never one per prescription.
        var visitsById = await visits.GetManyAsync(
            [.. prescribed.Select(p => p.VisitId).Distinct()], cancellationToken);

        var physicians = await identity.GetPhysiciansAsync(
            [.. prescribed.Select(p => p.PhysicianId).Distinct()], cancellationToken);

        var entries = new List<ReconciledEntry>();

        // Self-reported first, then prescribed — the insertion order matters because the sort
        // below is stable and entries sharing an addedAt keep it.
        foreach (var medication in selfReported)
        {
            entries.Add(new ReconciledEntry(
                medication.DrugName,
                medication.CreatedAt,
                new ReconciledSelfMedication(
                    medication.Id,
                    medication.DrugName,
                    medication.Dosage,
                    medication.Frequency,
                    "SELF_REPORTED",
                    // `m.prescribedBy || null` — free text the patient typed, emptied to null.
                    PatientOverviewJs.Truthy(medication.PrescribedBy),
                    medication.StartDate,
                    medication.EndDate,
                    medication.IsActive,
                    account.MemberName(medication.SubprofileId),
                    medication.CreatedAt,
                    CanDelete: true,
                    IsDuplicate: false)));
        }

        foreach (var prescription in prescribed)
        {
            physicians.TryGetValue(prescription.PhysicianId, out var physician);
            var doctorName = PatientOverviewJs.Truthy(physician?.DisplayName) ?? "Doctor";

            visitsById.TryGetValue(prescription.VisitId, out var visit);

            foreach (var line in PatientOverviewJs.DrugLines(prescription.Medications))
            {
                // Three-tier fallback here, unlike the dashboard's two: `m.name` is accepted when
                // `m.drugName` is missing.
                var drugName = PatientOverviewJs.ReadString(line, "drugName")
                    ?? PatientOverviewJs.ReadString(line, "name")
                    ?? "Unknown";

                entries.Add(new ReconciledEntry(
                    drugName,
                    prescription.CreatedAt,
                    new ReconciledPrescribedMedication(
                        // The RAW interpolation of drugName, not the fallback chain above it.
                        $"rx-{prescription.Id}-{PatientOverviewJs.Interpolate(line, "drugName")}",
                        drugName,
                        PatientOverviewJs.ReadString(line, "dosage"),
                        PatientOverviewJs.ReadString(line, "frequency"),
                        PatientOverviewJs.ReadString(line, "duration"),
                        PatientOverviewJs.ReadString(line, "instructions"),
                        "PRESCRIBED",
                        doctorName,
                        visit?.VisitDate,
                        visit?.ChiefComplaint,
                        prescription.Id,
                        account.MemberName(prescription.SubprofileId),
                        // The PRESCRIPTION's createdAt, shared by every line it flattens into,
                        // not the drug line's own.
                        prescription.CreatedAt,
                        CanDelete: false,
                        IsDuplicate: false)));
            }
        }

        // `reconciled.sort((a, b) => new Date(b.addedAt) - new Date(a.addedAt))`. JavaScript's
        // sort has been required to be stable since ES2019 and LINQ's OrderByDescending is
        // stable, so entries sharing an instant keep self-reported ahead of prescribed.
        var ordered = entries.OrderByDescending(e => e.AddedAt).ToList();

        // The duplicate rule: same drug name, case-insensitive and trimmed, present under BOTH
        // sources. Every entry carrying that name is flagged, including the second and third
        // prescribed entry for the same drug.
        var sourcesByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entry in ordered)
        {
            var key = DuplicateKey(entry.DrugName);

            if (!sourcesByName.TryGetValue(key, out var sources))
                sourcesByName[key] = sources = new HashSet<string>(StringComparer.Ordinal);

            sources.Add(entry.Source);
        }

        var medications = new List<object>(ordered.Count);
        var duplicates = 0;

        foreach (var entry in ordered)
        {
            var sources = sourcesByName[DuplicateKey(entry.DrugName)];
            var isDuplicate = sources.Contains("SELF_REPORTED") && sources.Contains("PRESCRIBED");

            if (isDuplicate) duplicates++;

            medications.Add(entry.Payload switch
            {
                ReconciledSelfMedication self => self with { IsDuplicate = isDuplicate },
                ReconciledPrescribedMedication rx => rx with { IsDuplicate = isDuplicate },
                _ => entry.Payload
            });
        }

        return new ReconciledMedicationsResponse(
            medications,
            new ReconciledMedicationsSummary(
                Total: medications.Count,
                SelfReported: ordered.Count(e => e.Source == "SELF_REPORTED"),
                Prescribed: ordered.Count(e => e.Source == "PRESCRIBED"),
                // Entries, not distinct drug names.
                Duplicates: duplicates));
    }

    /// <summary>
    /// <c>m.drugName.toLowerCase().trim()</c>. Invariant lower-casing, because JavaScript's
    /// <c>toLowerCase</c> is locale-independent and a Turkish server locale must not fold a
    /// dotted I differently from an English one.
    /// </summary>
    private static string DuplicateKey(string drugName) => drugName.ToLowerInvariant().Trim();

    /// <summary>
    /// One entry while it is still being assembled: the two keys the algorithm needs — the drug
    /// name for the duplicate map and the timestamp for the sort — beside the finished payload,
    /// which is one of two record types with different key sets.
    /// </summary>
    /// <param name="DrugName">The reconciled name, after its fallback chain.</param>
    /// <param name="AddedAt">The sort key.</param>
    /// <param name="Payload">The response object, with <c>isDuplicate</c> not yet computed.</param>
    private sealed record ReconciledEntry(string DrugName, DateTime AddedAt, object Payload)
    {
        /// <summary>Which half of the diff this came from.</summary>
        public string Source => Payload is ReconciledSelfMedication ? "SELF_REPORTED" : "PRESCRIBED";
    }
}
