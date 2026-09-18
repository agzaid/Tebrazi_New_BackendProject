using System.Text.Json.Nodes;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.SharedKernel.Pagination;
using Tebrazi.Visits.Application.Abstractions.Persistence;
using Tebrazi.Visits.Application.ApiModels.Responses;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Application.UseCases;

// ═════════════════════════════════════════════════════════════════════════════
//  Shared relation loading
//
//  Every list route embeds the same four relations plus the _count object. Resolving them per
//  row would be five N+1s, so each port is asked ONCE for the whole page.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>Everything a page of visits needs to project, resolved in one batch per port.</summary>
internal sealed record VisitReadRelationSet(
    IReadOnlyDictionary<string, ClinicSummary> Clinics,
    IReadOnlyDictionary<string, PhysicianSummary> Physicians,
    IReadOnlyDictionary<string, SubprofileSummary> Subprofiles,
    IReadOnlyDictionary<string, ClinicPatientSummary> Charts,
    IReadOnlyDictionary<string, int> PrescriptionCounts,
    IReadOnlyDictionary<string, int> InvestigationCounts)
{
    public ClinicSummary? Clinic(Visit v) => Clinics.GetValueOrDefault(v.ClinicId);

    public PhysicianSummary? Physician(Visit v) => Physicians.GetValueOrDefault(v.PhysicianId);

    public SubprofileSummary? Subprofile(Visit v)
        => v.SubprofileId is null ? null : Subprofiles.GetValueOrDefault(v.SubprofileId);

    /// <summary>
    /// The RESOLVED chart, which is what the Node branch tests — <c>!v.clinicPatient</c>, not
    /// <c>!v.clinicPatientId</c> (visits.js:156). A dangling chart id therefore behaves like an
    /// account-backed visit rather than producing a half-populated chart object.
    /// </summary>
    public ClinicPatientSummary? Chart(Visit v)
        => v.ClinicPatientId is null ? null : Charts.GetValueOrDefault(v.ClinicPatientId);

    /// <summary>A visit with no rows in either table is absent from both dictionaries, so 0.</summary>
    public VisitReadCounts Counts(Visit v)
        => new(PrescriptionCounts.GetValueOrDefault(v.Id), InvestigationCounts.GetValueOrDefault(v.Id));
}

/// <summary>
/// Builds the list-shaped rows for <c>GET /api/visits</c>, <c>GET /api/visits/patient/{id}</c>
/// and <c>GET /api/visits/inbox</c>. Constructed by the handlers from their own injected ports —
/// it holds no state of its own and is not in the container.
/// </summary>
internal sealed class VisitReadRowFactory(
    IClinicDirectory clinics,
    IIdentityDirectory identity,
    IPatientDirectory patients,
    IClinicPatientDirectory clinicPatients,
    IPrescriptionDirectory prescriptions,
    IInvestigationStore investigations)
{
    /// <summary>
    /// The physician key set, with <c>patientUser</c> appended. Its two variants have
    /// incompatible key sets, so the element type stays <c>object</c> — see
    /// <see cref="VisitReadListResponse"/>.
    /// </summary>
    public async Task<IReadOnlyList<object>> PhysicianRowsAsync(
        IReadOnlyList<Visit> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var relations = await LoadAsync(rows, ct);

        // One batched User lookup for the rows that are NOT chart-backed, matching the
        // deliberate N+1 fix at visits.js:141-152.
        string[] accountIds =
        [
            .. rows.Where(v => relations.Chart(v) is null)
                   .Select(v => v.PatientUserId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Distinct()
        ];

        var accounts = accountIds.Length == 0
            ? new Dictionary<string, UserSummary>(0)
            : await identity.GetUsersAsync(accountIds, ct);

        return
        [
            .. rows.Select(v =>
            {
                var chart = relations.Chart(v);

                object? patientUser = null;

                if (chart is not null)
                {
                    patientUser = new VisitReadListChartPatientUser(chart.Name, chart.Phone);
                }
                else if (accounts.GetValueOrDefault(v.PatientUserId) is { } account)
                {
                    patientUser = new VisitReadListAccountPatientUser(
                        account.Id, account.DisplayName, account.Email);
                }

                return (object)VisitReadMapper.ToPhysicianListItem(
                    v, relations.Clinic(v), relations.Physician(v), relations.Subprofile(v),
                    chart, relations.Counts(v), patientUser);
            })
        ];
    }

    /// <summary>The non-physician key set: seven keys absent, and no <c>patientUser</c> at all.</summary>
    public async Task<IReadOnlyList<object>> PatientRowsAsync(
        IReadOnlyList<Visit> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var relations = await LoadAsync(rows, ct);

        return
        [
            .. rows.Select(v => (object)VisitReadMapper.ToPatientListItem(
                v, relations.Clinic(v), relations.Physician(v), relations.Subprofile(v),
                relations.Chart(v), relations.Counts(v)))
        ];
    }

    public async Task<IReadOnlyList<VisitReadInboxItem>> InboxRowsAsync(
        IReadOnlyList<Visit> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var relations = await LoadAsync(rows, ct);

        return
        [
            .. rows.Select(v => VisitReadMapper.ToInboxItem(
                v, relations.Clinic(v), relations.Physician(v), relations.Subprofile(v),
                relations.Chart(v), relations.Counts(v)))
        ];
    }

    private async Task<VisitReadRelationSet> LoadAsync(IReadOnlyList<Visit> rows, CancellationToken ct)
    {
        string[] visitIds = [.. rows.Select(v => v.Id).Distinct()];
        string[] clinicIds = [.. rows.Select(v => v.ClinicId).Distinct()];
        string[] physicianIds = [.. rows.Select(v => v.PhysicianId).Distinct()];
        string[] subprofileIds = [.. Present(rows.Select(v => v.SubprofileId))];
        string[] chartIds = [.. Present(rows.Select(v => v.ClinicPatientId))];

        // Six sequential round trips, deliberately NOT started in parallel: IClinicDirectory and
        // IClinicPatientDirectory are both served by the Clinics module's request-scoped
        // DbContext, and EF Core throws on two concurrent operations against one context.
        var resolvedClinics = await clinics.GetClinicsAsync(clinicIds, ct);
        var resolvedPhysicians = await identity.GetPhysiciansAsync(physicianIds, ct);

        var resolvedSubprofiles = subprofileIds.Length == 0
            ? new Dictionary<string, SubprofileSummary>(0)
            : await patients.GetSubprofilesAsync(subprofileIds, ct);

        var resolvedCharts = chartIds.Length == 0
            ? new Dictionary<string, ClinicPatientSummary>(0)
            : await clinicPatients.GetManyAsync(chartIds, ct);

        var prescriptionCounts = await prescriptions.CountByVisitAsync(visitIds, ct);
        var investigationCounts = await investigations.CountForVisitsAsync(visitIds, ct);

        return new VisitReadRelationSet(
            resolvedClinics,
            resolvedPhysicians,
            resolvedSubprofiles,
            resolvedCharts,
            prescriptionCounts,
            investigationCounts);
    }

    private static IEnumerable<string> Present(IEnumerable<string?> ids)
        => ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct();
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The dual-persona visit list. Which branch runs is decided by the <c>userType</c> CLAIM, not by
/// the database — a userType changed in the DB does not take effect until the token is reissued
/// (visits.js:84).
/// </summary>
/// <param name="ClinicId">Physician branch only; ignored for everyone else.</param>
/// <param name="Status">
/// Physician branch only. Applied as EQUALITY when present, so <c>?status=ARCHIVED</c> really
/// does list archived visits — the code comment at visits.js:99 claiming otherwise is wrong, and
/// the code is the contract.
/// </param>
/// <param name="Page">Raw query string: the JavaScript <c>||</c> defaults have to see it unparsed.</param>
public sealed record VisitReadListQuery(
    string? ClinicId,
    string? Status,
    string? PatientUserId,
    string? SubprofileId,
    string? Page,
    string? Limit) : IRequest<VisitReadListResponse>;

public sealed class VisitReadListHandler(
    ICurrentUser currentUser,
    IVisitStore visitStore,
    IInvestigationStore investigationStore,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IClinicPatientDirectory clinicPatients,
    IPrescriptionDirectory prescriptions)
    : IRequestHandler<VisitReadListQuery, VisitReadListResponse>
{
    public async Task<VisitReadListResponse> Handle(
        VisitReadListQuery request, CancellationToken cancellationToken = default)
    {
        var userId = VisitReadCaller.RequireUserId(currentUser);
        var page = PageRequest.Parse(request.Page, request.Limit);

        var factory = new VisitReadRowFactory(
            clinics, identity, patients, clinicPatients, prescriptions, investigationStore);

        if (currentUser.UserType == "PHYSICIAN")
        {
            var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

            // No profile: an empty page with pages LITERALLY 0 beside the parsed page and limit,
            // before any visit query (visits.js:91).
            if (physician is null) return new VisitReadListResponse([], PaginationMeta.Empty(page));

            // Each filter goes through Truthy: visits.js:98/100/102 gate them on JS truthiness,
            // so ?clinicId= is IGNORED, while the store's `is not null` test would turn the ""
            // model binding hands us into `clinicId = ''` and an empty page.
            var filter = new PhysicianVisitFilter(
                physician.Id,
                VisitReadFilters.Truthy(request.ClinicId),
                VisitReadFilters.Truthy(request.PatientUserId),
                VisitReadFilters.Truthy(request.SubprofileId),
                ParseStatus(request.Status));

            var (items, total) = await visitStore.PageForPhysicianAsync(
                filter, page.Page, page.Limit, cancellationToken);

            return new VisitReadListResponse(
                await factory.PhysicianRowsAsync(items, cancellationToken),
                PaginationMeta.From(total, page));
        }

        // PATIENT, RECEPTIONIST and STAFF all land here and are filtered by their OWN user id, so
        // a receptionist sees their personal visits rather than the clinic's. That looks like a
        // bug and is the contract (visits.js:104-110).
        var (own, ownTotal) = await visitStore.PageForPatientAsync(
            userId, page.Page, page.Limit, cancellationToken);

        return new VisitReadListResponse(
            await factory.PatientRowsAsync(own, cancellationToken),
            PaginationMeta.From(ownTotal, page));
    }

    /// <summary>
    /// visits.js:101 assigns <c>?status</c> straight into the Prisma enum filter, so an
    /// unrecognised value is rejected by the database and surfaces as
    /// <c>500 {"error":"Failed to list visits", ...}</c>. The status code and the <c>error</c>
    /// string are reproduced; Node's second key carries the raw driver message, which this port
    /// does not leak (see docs/PORT-STATUS.md).
    /// </summary>
    private static VisitStatus? ParseStatus(string? status) => status switch
    {
        null or "" => null,
        "IN_PROGRESS" => VisitStatus.IN_PROGRESS,
        "COMPLETED" => VisitStatus.COMPLETED,
        "CANCELLED" => VisitStatus.CANCELLED,
        "ARCHIVED" => VisitStatus.ARCHIVED,
        _ => throw new BusinessException("Failed to list visits", "Failed to list visits", 500)
    };
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits/{id}
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One visit in full. Readable by the owning physician and by the patient, and by nobody else —
/// clinic staff get 403 even for a clinic they work at.
/// </summary>
public sealed record VisitReadDetailQuery(string VisitId) : IRequest<object>;

public sealed class VisitReadDetailHandler(
    ICurrentUser currentUser,
    IVisitStore visitStore,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IClinicPatientDirectory clinicPatients,
    IPrescriptionDirectory prescriptions)
    : IRequestHandler<VisitReadDetailQuery, object>
{
    public async Task<object> Handle(
        VisitReadDetailQuery request, CancellationToken cancellationToken = default)
    {
        var userId = VisitReadCaller.RequireUserId(currentUser);

        // No soft-delete and no status filter: this route is a bare findUnique, so ARCHIVED and
        // soft-deleted visits are still readable by id. That is how a patient keeps access to a
        // record the physician archived.
        var visit = await visitStore.GetWithInvestigationsAsync(request.VisitId, cancellationToken)
            ?? throw new NotFoundException("Visit not found");

        var callerProfile = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

        // ORDER IS LOAD-BEARING. A visit created from an unlinked chart stores the PHYSICIAN's own
        // user id as patientUserId (visits.js:215), so the physician satisfies BOTH tests; the
        // patient branch is guarded by `isPatient && !isPhysician`, so the physician view wins.
        var isPhysician = callerProfile is not null && visit.PhysicianId == callerProfile.Id;
        var isPatient = visit.PatientUserId == userId;

        if (!isPhysician && !isPatient) throw new ForbiddenException("Access denied");

        var clinic = await clinics.GetClinicAsync(visit.ClinicId, cancellationToken);

        var owningPhysician = isPhysician
            ? callerProfile
            : await identity.GetPhysicianAsync(visit.PhysicianId, cancellationToken);

        // The physician's own email is not on PhysicianSummary, and the Node select nests it
        // under physician.user, so the account is fetched separately.
        var owningUser = owningPhysician is null
            ? null
            : await identity.GetUserAsync(owningPhysician.UserId, cancellationToken);

        var subprofile = visit.SubprofileId is null
            ? null
            : await patients.GetSubprofileAsync(visit.SubprofileId, cancellationToken);

        var patientUser = await ResolvePatientUserAsync(visit, cancellationToken);

        var prescriptionRows = await prescriptions.ListByVisitAsync(visit.Id, cancellationToken);

        IReadOnlyList<VisitReadDetailPrescription> prescriptionItems =
            [.. prescriptionRows.Select(VisitReadMapper.ToDetailPrescription)];

        // Node leaves both arrays unordered. Sorted newest-first here so the same visit reports
        // its investigations in the same order as GET /{id}/investigations does.
        IReadOnlyList<VisitReadDetailInvestigation> investigationItems =
        [
            .. visit.Investigations
                    .OrderByDescending(i => i.RequestedAt)
                    .Select(VisitReadMapper.ToDetailInvestigation)
        ];

        var detailClinic = VisitReadMapper.ToDetailClinic(clinic);
        var detailPhysician = VisitReadMapper.ToDetailPhysician(owningPhysician, owningUser);
        var detailSubprofile = VisitReadMapper.ToDetailSubprofile(subprofile);

        if (isPatient && !isPhysician)
        {
            // sharedSections = null means share EVERYTHING (backward compatible). An empty array
            // is still an array and therefore hides all four sections; a non-array JSON value
            // shares all four AND is reported to the client as null.
            var shared = VisitReadMapper.ParseJson(visit.SharedSections) as JsonArray;

            var subjective = visit.Subjective;
            var objective = visit.Objective;
            var assessment = visit.Assessment;
            var plan = visit.Plan;

            if (shared is not null)
            {
                if (!Includes(shared, "subjective")) subjective = null;
                if (!Includes(shared, "objective")) objective = null;
                if (!Includes(shared, "assessment")) assessment = null;
                if (!Includes(shared, "plan")) plan = null;
            }

            return new VisitReadDetailPatientResponse(
                visit.Id, visit.OrganizationId, visit.ClinicId, visit.PhysicianId,
                visit.PatientUserId, visit.SubprofileId, visit.ClinicPatientId,
                visit.Status.ToString(),
                subjective, objective, assessment, plan,
                visit.ChiefComplaint, visit.Diagnosis,
                VisitReadMapper.ParseJson(visit.DiagnosisCodes),
                visit.FollowUpDate, visit.FollowUpNotes,
                shared,
                visit.PatientFeedback, visit.PatientFeedbackAt, visit.PatientDismissedAt,
                visit.VisitDate, VisitReadMapper.ParseJson(visit.SpecialtyData), visit.CompletedAt,
                visit.CreatedAt, visit.UpdatedAt ?? visit.CreatedAt, visit.DeletedAt,
                detailClinic, detailPhysician, detailSubprofile,
                prescriptionItems, investigationItems, patientUser, false);
        }

        return new VisitReadDetailPhysicianResponse(
            visit.Id, visit.OrganizationId, visit.ClinicId, visit.PhysicianId,
            visit.PatientUserId, visit.SubprofileId, visit.ClinicPatientId,
            visit.Status.ToString(),
            visit.AudioUrl, visit.RawTranscript, visit.RawNotes,
            visit.Subjective, visit.Objective, visit.Assessment, visit.Plan,
            visit.ChiefComplaint, visit.Diagnosis,
            VisitReadMapper.ParseJson(visit.DiagnosisCodes),
            visit.FollowUpDate, visit.FollowUpNotes,
            // Raw on this branch: only the patient view normalises a non-array to null.
            VisitReadMapper.ParseJson(visit.SharedSections),
            visit.PatientFeedback, visit.PatientFeedbackAt, visit.PatientDismissedAt,
            visit.VisitDate, VisitReadMapper.ParseJson(visit.SpecialtyData), visit.CompletedAt,
            visit.CreatedAt, visit.UpdatedAt ?? visit.CreatedAt, visit.DeletedAt,
            detailClinic, detailPhysician, detailSubprofile,
            prescriptionItems, investigationItems, patientUser, true);
    }

    /// <summary>
    /// The chart's <c>name</c> is RENAMED to <c>displayName</c> — there is no <c>name</c> key on
    /// the wire — and the branch is chosen by <c>clinicPatientId</c>, so a dangling id yields
    /// null rather than falling back to the account (visits.js:328-339).
    /// </summary>
    private async Task<VisitReadDetailPatientUser?> ResolvePatientUserAsync(
        Visit visit, CancellationToken ct)
    {
        // Truthiness again (visits.js:328): the branch is `if (visit.clinicPatientId)`, so an
        // empty id falls through to the account rather than looking up a chart that cannot exist.
        if (!string.IsNullOrEmpty(visit.ClinicPatientId))
        {
            var chart = await clinicPatients.GetAsync(visit.ClinicPatientId, ct);
            return chart is null ? null : new VisitReadDetailPatientUser(chart.Name, chart.Email, chart.Phone);
        }

        var account = await identity.GetUserAsync(visit.PatientUserId, ct);
        return account is null ? null : new VisitReadDetailPatientUser(account.DisplayName, account.Email, account.Phone);
    }

    /// <summary>
    /// <c>Array.prototype.includes</c> over a free-form JSON array: only an exact string element
    /// matches, so a stored <c>[{"section":"plan"}]</c> shares nothing.
    /// </summary>
    private static bool Includes(JsonArray sections, string section)
        => sections.Any(node => node is JsonValue value
                             && value.TryGetValue<string>(out var name)
                             && name == section);
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits/patient/{patientUserId}
//
//  No such route exists in the Node backend — the client calls it and receives the global
//  404. Implemented here rather than reproducing that 404 (docs/PORT-STATUS.md), and it returns
//  a BARE ARRAY because client/src/components/PatientTimeline.jsx:26 does `r.data || []` and
//  maps over it; the {data, pagination} envelope would arrive as an object and throw.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One patient's visit history, newest first and unpaginated. The persona branch is the same as
/// <c>GET /api/visits</c>: a physician gets THEIR OWN visits with that patient, and the patient
/// themselves gets their own completed record with the SOAP note stripped.
/// </summary>
/// <param name="SubprofileId">Optional; narrows the history to one dependant.</param>
public sealed record VisitReadPatientHistoryQuery(string PatientUserId, string? SubprofileId)
    : IRequest<IReadOnlyList<object>>;

public sealed class VisitReadPatientHistoryHandler(
    ICurrentUser currentUser,
    IVisitStore visitStore,
    IInvestigationStore investigationStore,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IClinicPatientDirectory clinicPatients,
    IPrescriptionDirectory prescriptions)
    : IRequestHandler<VisitReadPatientHistoryQuery, IReadOnlyList<object>>
{
    public async Task<IReadOnlyList<object>> Handle(
        VisitReadPatientHistoryQuery request, CancellationToken cancellationToken = default)
    {
        var userId = VisitReadCaller.RequireUserId(currentUser);

        var factory = new VisitReadRowFactory(
            clinics, identity, patients, clinicPatients, prescriptions, investigationStore);

        // Truthy for the same reason as GET /: ?subprofileId= must not narrow to the empty id.
        var history = await visitStore.ListForPatientAsync(
            request.PatientUserId, VisitReadFilters.Truthy(request.SubprofileId), cancellationToken);

        if (currentUser.UserType == "PHYSICIAN")
        {
            var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);
            if (physician is null) return [];

            // The store returns the account's WHOLE history across every physician, which this
            // endpoint must not expose: narrowed to the caller's own visits so the result matches
            // GET /api/visits?patientUserId=<id>, ARCHIVED excluded exactly as that default is.
            IReadOnlyList<Visit> own =
            [
                .. history.Where(v => v.PhysicianId == physician.Id
                                   && v.Status != VisitStatus.ARCHIVED)
            ];

            return await factory.PhysicianRowsAsync(own, cancellationToken);
        }

        if (userId != request.PatientUserId) throw new ForbiddenException("Access denied");

        // Their own record, filtered as the patient branch of GET /api/visits filters it.
        IReadOnlyList<Visit> visible =
        [
            .. history.Where(v => (v.Status == VisitStatus.COMPLETED || v.Status == VisitStatus.ARCHIVED)
                               && v.PatientDismissedAt is null)
        ];

        return await factory.PatientRowsAsync(visible, cancellationToken);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits/inbox
//
//  Unreachable in Node: GET /:id is registered ~1,000 lines earlier and swallows the literal
//  segment, so the client's call answers 404 {"error":"Visit not found"} and its
//  last-visit-per-doctor map stays empty. In ASP.NET a literal beats a parameter, so mapping it
//  brings the feature to life.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The caller's own completed visits, newest first, as a BARE ARRAY —
/// client/src/pages/ConnectionsPage.jsx:214 iterates the body directly.
/// </summary>
public sealed record VisitReadInboxQuery(string? Page, string? Limit)
    : IRequest<IReadOnlyList<VisitReadInboxItem>>;

public sealed class VisitReadInboxHandler(
    ICurrentUser currentUser,
    IVisitStore visitStore,
    IInvestigationStore investigationStore,
    IIdentityDirectory identity,
    IClinicDirectory clinics,
    IPatientDirectory patients,
    IClinicPatientDirectory clinicPatients,
    IPrescriptionDirectory prescriptions)
    : IRequestHandler<VisitReadInboxQuery, IReadOnlyList<VisitReadInboxItem>>
{
    public async Task<IReadOnlyList<VisitReadInboxItem>> Handle(
        VisitReadInboxQuery request, CancellationToken cancellationToken = default)
    {
        var userId = VisitReadCaller.RequireUserId(currentUser);
        var page = PageRequest.Parse(request.Page, request.Limit);

        var (items, _) = await visitStore.PageInboxAsync(
            userId, page.Page, page.Limit, cancellationToken);

        var factory = new VisitReadRowFactory(
            clinics, identity, patients, clinicPatients, prescriptions, investigationStore);

        return await factory.InboxRowsAsync(items, cancellationToken);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  GET /api/visits/follow-ups-due
//
//  Also unreachable in Node for the same route-order reason. The handler body exists at
//  visits.js:1244-1327 and its intended shape is ported here. Two of its behaviours are
//  deliberately NOT ported: the FOLLOW_UP_REMINDER notification burst that a plain GET fires,
//  and cacheMiddleware(30). See docs/PORT-STATUS.md.
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The physician dashboard's follow-up queue: up to twenty COMPLETED visits whose follow-up date
/// has arrived or falls within seven days, oldest first.
/// </summary>
public sealed record VisitReadFollowUpsDueQuery : IRequest<IReadOnlyList<VisitReadFollowUpDueItem>>;

public sealed class VisitReadFollowUpsDueHandler(
    ICurrentUser currentUser,
    IVisitStore visitStore,
    IIdentityDirectory identity,
    IPatientDirectory patients,
    IClinicPatientDirectory clinicPatients)
    : IRequestHandler<VisitReadFollowUpsDueQuery, IReadOnlyList<VisitReadFollowUpDueItem>>
{
    private const int MaxRows = 20;

    public async Task<IReadOnlyList<VisitReadFollowUpDueItem>> Handle(
        VisitReadFollowUpsDueQuery request, CancellationToken cancellationToken = default)
    {
        var userId = VisitReadCaller.RequireUserId(currentUser);

        var physician = await identity.GetPhysicianByUserIdAsync(userId, cancellationToken);

        // A caller with no physician profile gets an empty array, never a 403 — and no
        // userType check is applied (visits.js:1248).
        if (physician is null) return [];

        // Node builds these boundaries in SERVER-LOCAL time. Every DateTime in this port is
        // UTC-normalised on the way out of the database, so UTC midnight is used instead: mixing
        // a local-midnight bound with UTC-stored dates would misclassify rows by the offset.
        var today = DateTime.UtcNow.Date;
        var weekAhead = today.AddDays(7);

        var due = await visitStore.ListFollowUpsDueAsync(physician.Id, weekAhead, cancellationToken);

        // The store's filter is `status != ARCHIVED`; Node's is `status = COMPLETED`. Narrowed
        // here before the cap, so the twenty rows are the same twenty Node would take.
        //
        // There is NO lower bound on followUpDate — combined with oldest-first ordering and a
        // hard cap of 20, a physician with a long overdue backlog never sees this week's
        // follow-ups. That is the Node behaviour and it is kept.
        Visit[] rows =
        [
            .. due.Where(v => v.Status == VisitStatus.COMPLETED && v.FollowUpDate.HasValue)
                  .Take(MaxRows)
        ];

        if (rows.Length == 0) return [];

        string[] chartIds =
        [
            .. rows.Select(v => v.ClinicPatientId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Select(id => id!)
                   .Distinct()
        ];

        var charts = chartIds.Length == 0
            ? new Dictionary<string, ClinicPatientSummary>(0)
            : await clinicPatients.GetManyAsync(chartIds, cancellationToken);

        // `v.clinicPatient?.name` is a TRUTHINESS test, so a chart with an empty name falls
        // through to the account lookup (visits.js:1270).
        string? ChartName(Visit v)
            => v.ClinicPatientId is not null
               && charts.GetValueOrDefault(v.ClinicPatientId) is { Name: { Length: > 0 } name }
                ? name
                : null;

        string[] accountIds =
        [
            .. rows.Where(v => ChartName(v) is null)
                   .Select(v => v.PatientUserId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Distinct()
        ];

        // Node issues one User lookup per row inside Promise.all; batched here, which changes
        // nothing observable.
        var accounts = accountIds.Length == 0
            ? new Dictionary<string, UserSummary>(0)
            : await identity.GetUsersAsync(accountIds, cancellationToken);

        string[] subprofileIds =
        [
            .. rows.Select(v => v.SubprofileId)
                   .Where(id => !string.IsNullOrEmpty(id))
                   .Select(id => id!)
                   .Distinct()
        ];

        var subprofiles = subprofileIds.Length == 0
            ? new Dictionary<string, SubprofileSummary>(0)
            : await patients.GetSubprofilesAsync(subprofileIds, cancellationToken);

        return
        [
            .. rows.Select(v =>
            {
                var followUpDate = v.FollowUpDate!.Value;

                // Strictly BEFORE midnight today, so a follow-up earlier the same day is TODAY
                // and not overdue. The two flags are mutually exclusive by construction.
                var isOverdue = followUpDate < today;
                var isToday = followUpDate.Date == today;

                var patientName = ChartName(v)
                    ?? (accounts.GetValueOrDefault(v.PatientUserId) is { DisplayName: { Length: > 0 } display }
                        ? display
                        : "Patient");

                var subprofileName = v.SubprofileId is not null
                    ? subprofiles.GetValueOrDefault(v.SubprofileId)?.Name
                    : null;

                return new VisitReadFollowUpDueItem(
                    v.Id,
                    patientName,
                    v.PatientUserId,
                    subprofileName,
                    v.ChiefComplaint,
                    followUpDate,
                    v.FollowUpNotes,
                    v.VisitDate,
                    isOverdue,
                    isToday,
                    isOverdue ? "OVERDUE" : isToday ? "TODAY" : "UPCOMING");
            })
        ];
    }
}

// ═════════════════════════════════════════════════════════════════════════════

/// <summary>Query-filter helpers shared by the five read endpoints.</summary>
internal static class VisitReadFilters
{
    /// <summary>
    /// JavaScript truthiness for an optional query filter. Every filter on <c>GET /api/visits</c>
    /// is gated on <c>if (value)</c> (visits.js:98, :100, :102), so <c>?clinicId=</c> is not a
    /// filter at all — it is an absent one. ASP.NET model binding hands a present-but-valueless
    /// query key over as <c>""</c>, and the stores test <c>is not null</c>, so without this the
    /// port would filter on the empty string and return nothing where Node returns everything.
    ///
    /// Empty ONLY, deliberately: <c>" "</c> is truthy in JavaScript, so a whitespace filter must
    /// still reach the query and match nothing. <c>IsNullOrWhiteSpace</c> would be wrong here.
    /// </summary>
    public static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// The one caller check these five read endpoints share. <c>router.use(authCheck)</c> is
/// path-agnostic in Node, so a request with no usable token never reaches a handler.
/// </summary>
internal static class VisitReadCaller
{
    /// <summary>
    /// <c>401 {"error":"No token provided"}</c> — the literal Node body, which is why this is a
    /// <see cref="BusinessException"/> and not <c>UnauthorizedException</c> (that one emits
    /// <c>{"error":"Unauthorized", ...}</c>). Unreachable behind <c>[Authorize]</c>, and kept so
    /// the handler cannot silently read a null user id as a filter value.
    /// </summary>
    public static string RequireUserId(ICurrentUser currentUser)
        => string.IsNullOrEmpty(currentUser.UserId)
            ? throw new BusinessException("No token provided", "No token provided", 401)
            : currentUser.UserId;
}
