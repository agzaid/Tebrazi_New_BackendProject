using Tebrazi.Prescriptions.Domain.Entities;
using Tebrazi.SharedKernel.Abstractions.Persistence;

namespace Tebrazi.Prescriptions.Application.Abstractions.Persistence;

/// <summary>The Prescriptions module's unit of work. Handlers inject THIS, never the concrete context.</summary>
public interface IPrescriptionsDbContext : IDbContext;

/// <summary>
/// The filter behind <c>GET /api/prescriptions</c>.
///
/// Note there is no patient id here, and that is not an omission: a prescription reaches its
/// patient only through its visit. A patient-scoped list therefore resolves visit ids from the
/// Visits port first and passes them in <see cref="VisitIds"/>.
///
/// <para>A NULL member is not applied, matching how the Node handler builds its <c>where</c> key
/// by key. Pass null, not <c>""</c>, for an absent filter: ASP.NET binds a present-but-valueless
/// query key (<c>?visitId=</c>) to the empty string, and every test below is <c>is not null</c>,
/// so <c>""</c> would filter on the empty id and return nothing where Node returns everything.
/// Node gates each filter on JS truthiness, so map <c>""</c> to null with
/// <c>RxJs.Truthy(...)</c> in the handler.</para>
/// </summary>
/// <param name="PhysicianId">
/// The AUTHOR's physician-profile id (<c>prescriptions.physician_id</c>), not a user id.
/// </param>
/// <param name="VisitId">
/// One visit, for <c>?visitId=</c>. Applied as exact equality with no uuid validation, matching
/// the Node route.
/// </param>
/// <param name="VisitIds">
/// A SET of visits, for the patient branch and the interaction check. An EMPTY collection is a
/// real filter meaning "no visits matched" and returns nothing — treating it as "unfiltered"
/// would leak every prescription in the table.
/// </param>
/// <param name="SubprofileId">The dependant the prescription was written for.</param>
/// <param name="Status">One status, exact equality.</param>
/// <param name="StatusIn">
/// A set of statuses, for the <c>status: { in: [...] }</c> filters. An EMPTY collection matches
/// nothing, exactly as <see cref="VisitIds"/> does — it is a real filter, not an absent one.
/// </param>
/// <param name="RefillStatus">
/// The free-text refill state ("PENDING" / "APPROVED" / "DENIED"). Not used by any of the 17
/// endpoints — none of them lists by refill state.
/// </param>
/// <param name="From">
/// Inclusive lower bound on <c>created_at</c>. Not used by any of the 17 endpoints.
/// </param>
/// <param name="To">
/// Inclusive upper bound on <c>created_at</c>. Not used by any of the 17 endpoints.
/// </param>
/// <param name="IsDeleted">
/// Soft-delete narrowing: null applies NO filter (deleted and live rows alike), false restricts
/// to live rows, true to deleted ones.
///
/// <para><b>Every one of the 17 endpoints leaves this null.</b> Not one query in
/// <c>prescriptions.js</c> mentions <c>deletedAt</c> — a soft-deleted prescription is listed,
/// counted by <c>/summary</c>, printed by <c>/pdf</c>, signed, sent, dispensed, refilled and
/// edited exactly like a live one, with its <c>deletedAt</c> visible in the payload. The knob
/// exists so a future caller can ask for the other behaviour explicitly, and so that the ABSENCE
/// of a filter is a visible decision rather than an oversight. Do not add a context-wide query
/// filter to get it: that would also change <see cref="IPrescriptionStore.CountByStatusAsync"/>
/// and the rows the Visits module reads back through <c>IPrescriptionDirectory</c>.</para>
/// </param>
public sealed record PrescriptionFilter(
    string? PhysicianId = null,
    string? VisitId = null,
    IReadOnlyCollection<string>? VisitIds = null,
    string? SubprofileId = null,
    PrescriptionStatus? Status = null,
    IReadOnlyCollection<PrescriptionStatus>? StatusIn = null,
    string? RefillStatus = null,
    DateTime? From = null,
    DateTime? To = null,
    bool? IsDeleted = null);

/// <summary>
/// Reads and writes over <c>prescriptions</c>.
///
/// <para><b>No method here filters <c>DeletedAt</c> unless the caller asks for it through
/// <see cref="PrescriptionFilter.IsDeleted"/>.</b> That is the Node behaviour for all 17 routes,
/// and the per-method notes say so individually.</para>
/// </summary>
public interface IPrescriptionStore
{
    /// <summary>
    /// Tracked, for a handler that is about to modify the prescription. Does NOT filter
    /// <c>DeletedAt</c> — <c>/sign</c>, <c>/send</c>, <c>/dispense</c>, <c>PUT /{id}</c>, the
    /// refill pair and the medication pair all load with a bare <c>findUnique({ where: { id } })</c>.
    /// </summary>
    Task<Prescription?> GetForUpdateAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Untracked single read, for <c>GET /{id}</c> and <c>GET /{id}/pdf</c>. Does NOT filter
    /// <c>DeletedAt</c> (prescriptions.js:193, :878), so a soft-deleted prescription still
    /// renders a full document.
    /// </summary>
    Task<Prescription?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// A page ordered by <c>created_at</c> DESC. <b>Not used by any of the 17 endpoints</b> —
    /// <c>GET /api/prescriptions</c> returns a bare JSON array with no pagination envelope at
    /// all. Reach for <see cref="ListAsync"/> instead.
    /// </summary>
    Task<(IReadOnlyList<Prescription> Items, int TotalCount)> PageAsync(
        PrescriptionFilter filter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// The whole filtered set, ordered by <c>created_at</c> DESC — which is
    /// <c>GET /api/prescriptions</c>'s own <c>orderBy: { createdAt: 'desc' }</c>
    /// (prescriptions.js:66). No <c>DeletedAt</c> filter is applied unless
    /// <see cref="PrescriptionFilter.IsDeleted"/> is set.
    /// </summary>
    Task<IReadOnlyList<Prescription>> ListAsync(
        PrescriptionFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Every prescription on one visit, newest first. Used by the Visits module's read, not by
    /// any prescriptions route. No <c>DeletedAt</c> filter.
    /// </summary>
    Task<IReadOnlyList<Prescription>> ListForVisitAsync(string visitId, CancellationToken ct = default);

    /// <summary>Loaded in one query for a set of visits, so a list response is not N+1.</summary>
    Task<IReadOnlyDictionary<string, List<Prescription>>> ListForVisitsAsync(
        IReadOnlyCollection<string> visitIds, CancellationToken ct = default);

    /// <summary>
    /// The cross-prescription drug sweep behind <c>POST /api/prescriptions/check-interactions</c>
    /// (prescriptions.js:768-774): every prescription on any of the patient's visits whose status
    /// is in <paramref name="statuses"/>. The route passes SIGNED, CONFIRMED, SENT and DISPENSED
    /// — note SIGNED is INCLUDED here, so a never-sent prescription contributes its drugs, unlike
    /// the patient list filter which excludes it. No <c>DeletedAt</c> filter.
    ///
    /// <para>Ordered by <c>created_at</c> ASC, and that is a deliberate divergence:
    /// <b>Node's <c>findMany</c> has no <c>orderBy</c> at all</b>, so PostgreSQL hands back
    /// physical order, which for this append-only table is effectively insertion order. The
    /// order is OBSERVABLE — it decides the sequence of <c>drugsChecked</c> in the response — so
    /// this method commits to the closest deterministic equivalent rather than leaving it to the
    /// query planner. It is separate from <see cref="ListAsync"/> for exactly that reason:
    /// <see cref="ListAsync"/> is DESC because its endpoint asks for DESC.</para>
    /// </summary>
    /// <param name="visitIds">
    /// The patient's visit ids from <c>IVisitDirectory.ListVisitIdsForPatientAsync</c>. Empty
    /// means the patient has no visits and must return nothing.
    /// </param>
    /// <param name="statuses">
    /// The statuses to include. Empty matches nothing, consistent with
    /// <see cref="PrescriptionFilter.StatusIn"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Prescription>> ListForInteractionCheckAsync(
        IReadOnlyCollection<string> visitIds,
        IReadOnlyCollection<PrescriptionStatus> statuses,
        CancellationToken ct = default);

    /// <summary>
    /// Counts by status for one physician, for <c>GET /api/prescriptions/summary</c>. One GROUP
    /// BY, and NO <c>DeletedAt</c> filter — the Node counts include soft-deleted rows
    /// (prescriptions.js:150-154).
    ///
    /// <para>All three of the endpoint's numbers come out of this one dictionary, which is why
    /// there is no separate count method:</para>
    /// <list type="bullet">
    /// <item><c>signed</c> = CONFIRMED + SIGNED. The key is called "signed" but the Node filter
    /// is <c>status: { in: ['CONFIRMED', 'SIGNED'] }</c> — <b>two statuses</b>, because creation
    /// writes SIGNED while <c>/sign</c> writes CONFIRMED and both belong in the bucket.</item>
    /// <item><c>sent</c> = SENT.</item>
    /// <item><c>total</c> = the SUM OF EVERY VALUE, not <c>signed + sent</c>. It counts all
    /// statuses including DRAFT and DISPENSED, which appear in neither bucket.</item>
    /// </list>
    ///
    /// <para>A status with no rows is ABSENT from the dictionary rather than present as 0 — read
    /// it with <c>GetValueOrDefault</c>. Keys are <see cref="PrescriptionStatus"/> member names
    /// ("DRAFT", "SIGNED", "CONFIRMED", "SENT", "DISPENSED").</para>
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(
        string physicianId, CancellationToken ct = default);

    void Add(Prescription prescription);

    /// <summary>
    /// Forces the next <c>SaveChangesAsync</c> to issue an UPDATE for this prescription even when
    /// no property was changed, so <c>updated_at</c> is stamped.
    ///
    /// <para>Needed wherever Node's <c>prisma.prescription.update</c> is UNCONDITIONAL but the .NET
    /// transition can be a no-op, because Prisma's <c>@updatedAt</c> fires on every UPDATE while
    /// EF's change tracker writes nothing when no property actually changed. There are four such
    /// endpoints:</para>
    ///
    /// <list type="bullet">
    /// <item><c>PUT /api/prescriptions/{id}</c> — an EMPTY body <c>{}</c>, or a patch whose value
    /// equals the stored value, still reaches <c>prisma.update({ data: … })</c>
    /// (prescriptions.js:264-267).</item>
    /// <item><c>PUT /api/prescriptions/{id}/dispense</c> — <c>data: { status: 'DISPENSED' }</c>
    /// runs with no precondition (prescriptions.js:459-462), so re-dispensing an already-DISPENSED
    /// row is a no-op transition in EF and a real UPDATE in Prisma.</item>
    /// <item><c>PUT /api/prescriptions/{id}/stop-medication</c> and
    /// <c>/resume-medication</c> — the rebuilt medications array can serialize identically
    /// (prescriptions.js:677, :737).</item>
    /// </list>
    ///
    /// <para>It marks the whole entity Modified, so every column is rewritten with its current
    /// value. Calling it on an entity that DOES have real changes is harmless — the UPDATE happens
    /// either way and the audit stamp is applied once.</para>
    /// </summary>
    void MarkModified(Prescription prescription);
}

/// <summary>
/// The drug-interaction audit trail: written by
/// <c>POST /api/prescriptions/check-interactions</c> and read back by
/// <c>GET /api/prescriptions/interaction-history</c>.
///
/// <para>The read is IN SCOPE even though the React client never calls it — it is the only reader
/// of <see cref="InteractionAlert"/> anywhere, and porting it deliberately is what keeps the
/// entity from being write-only dead weight.</para>
/// </summary>
public interface IInteractionAlertStore
{
    void Add(InteractionAlert alert);

    /// <summary>
    /// The physician's own alert history, newest first
    /// (<c>orderBy: { checkedAt: 'desc' }</c>, prescriptions.js:176-180).
    ///
    /// <para>Filters on <c>physician_id</c> only. There is no patient-scoped variant because the
    /// route returns <c>200 []</c> for any caller without a physician profile — i.e. for every
    /// patient — WITHOUT issuing a query, even though patient-authored rows do exist in the table
    /// (prescriptions.js:171). Do not "fix" that into a patient read.</para>
    /// </summary>
    /// <param name="physicianId">The caller's physician-PROFILE id.</param>
    /// <param name="take">
    /// Prisma's <c>take</c>, from <c>parseInt(req.query.limit) || 20</c> — so 0 and unparseable
    /// both arrive here as 20, and the value is NOT clamped or capped.
    ///
    /// <para><b>A NEGATIVE value is meaningful, not an error.</b> Prisma reads <c>take: -5</c> as
    /// reverse-take: the LAST five rows of the requested ordering, i.e. the five OLDEST alerts,
    /// still returned newest-first among themselves. An <c>int</c> binder with a clamp would
    /// diverge on every negative input, so the implementation reproduces it by ordering ascending,
    /// taking <c>|take|</c>, and reversing the result.</para>
    ///
    /// <para>Zero returns an empty list, matching Prisma. It is unreachable from the query string
    /// because <c>|| 20</c> catches it first.</para>
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching alerts, empty list when there are none. Never null.</returns>
    Task<IReadOnlyList<InteractionAlert>> ListForPhysicianAsync(
        string physicianId, int take, CancellationToken ct = default);
}
