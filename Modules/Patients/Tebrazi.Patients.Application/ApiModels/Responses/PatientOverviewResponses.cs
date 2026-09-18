using System.Text.Json.Serialization;

namespace Tebrazi.Patients.Application.ApiModels.Responses;

// ═════════════════════════════════════════════════════════════════════════════
//  Response shapes for the three patient-facing aggregate endpoints:
//
//      GET /api/patients/dashboard              (patients.js:664-916)
//      GET /api/patients/health-summary         (patients.js:917-1143)
//      GET /api/patients/medications/reconciled (patients.js:536-663)
//
//  Each endpoint gets its own DTOs; nothing here is shared with PatientResponses.cs even where
//  the field lists look alike, because these three routes hand-build their bodies rather than
//  serializing a Prisma row.
//
//  TWO of them return HETEROGENEOUS arrays — `dashboard.medications` and
//  `reconciled.medications` each hold two different object shapes, one per source, and the
//  narrower shape genuinely OMITS the keys the wider one has. That is why those properties are
//  typed IReadOnlyList<object>: System.Text.Json serializes an `object`-declared element by its
//  RUNTIME type, so a prescribed entry emits its six keys and nothing more. A single merged DTO
//  would emit `"patientProfileId": null` on every prescribed entry, and the rule is that a key
//  Node omits must be ABSENT.
// ═════════════════════════════════════════════════════════════════════════════

// ── GET /api/patients/dashboard ──────────────────────────────────────────────

/// <summary>
/// The dashboard body. Node builds it in two places and the two DIFFER BY ONE KEY.
///
/// <para>The no-profile early return (patients.js:688-698) sends <c>profile: null</c>, six empty
/// arrays and a four-zero <c>stats</c> — and NO <c>upcomingFollowUps</c> key at all. The
/// populated return (patients.js:868-915) adds it. Hence
/// <see cref="UpcomingFollowUps"/> is nullable and ignored when null: null means "Node did not
/// send this key", not "Node sent an empty array". Every new patient's very first dashboard load
/// takes the early branch, so getting this wrong is not an edge case.</para>
///
/// <para>The early return is a <b>200</b>, not a 404.</para>
/// </summary>
public sealed record PatientDashboardResponse(
    PatientDashboardProfile? Profile,
    IReadOnlyList<PatientDashboardDoctor> Doctors,
    IReadOnlyList<object> Medications,
    IReadOnlyList<PatientDashboardAppointment> UpcomingAppointments,
    IReadOnlyList<PatientDashboardVisit> RecentVisits,
    IReadOnlyList<PatientDashboardFamilyMember> FamilyMembers,
    IReadOnlyList<PatientDashboardReminder> Reminders,
    PatientDashboardStats Stats,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PatientDashboardFollowUp>? UpcomingFollowUps);

/// <summary>
/// The eight keys of the dashboard's <c>profile</c> object (patients.js:869-877). Not the
/// patient profile row — <c>name</c> and <c>email</c> come from the USER, and the three counts
/// are collection lengths, not columns.
/// </summary>
public sealed record PatientDashboardProfile(
    string Name,
    string? Email,
    string? BloodType,
    string? Gender,
    DateTime? DateOfBirth,
    int AllergiesCount,
    int ConditionsCount,
    int MedicationsCount);

/// <summary>
/// A connected doctor (patients.js:723-730).
///
/// <para><b>Never populated by this port.</b> It is projected from
/// <c>DoctorPatientConnection</c>, and the Connections module is not ported — see the remarks on
/// <see cref="PatientDashboardResponse"/>'s handler. The record stands so the intended shape is
/// recorded rather than guessed at when Connections lands.</para>
/// </summary>
public sealed record PatientDashboardDoctor(
    string Id,
    string Name,
    string Specialty,
    bool Verified,
    DateTime ConnectedAt,
    string ForMember);

/// <summary>
/// A self-reported medication on the dashboard: Node spreads the WHOLE Prisma
/// <c>CurrentMedication</c> row and appends <c>source</c> (<c>{ ...m, source: 'SELF_REPORTED' }</c>,
/// patients.js:753-755), so all thirteen columns are on the wire — <c>updatedAt</c> and
/// <c>deletedAt</c> included, which the narrower <c>MedicationResponse</c> used by the CRUD
/// routes does not carry.
/// </summary>
public sealed record PatientDashboardSelfMedication(
    string Id,
    string? PatientProfileId,
    string? SubprofileId,
    string DrugName,
    string? Dosage,
    string? Frequency,
    string? PrescribedBy,
    DateTime? StartDate,
    DateTime? EndDate,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? DeletedAt,
    string Source);

/// <summary>
/// A prescribed medication on the dashboard (patients.js:758-765) — SIX keys, and deliberately
/// not the thirteen above. One prescription flattens into one entry per drug line in its opaque
/// <c>medications</c> JSON.
/// </summary>
public sealed record PatientDashboardPrescribedMedication(
    string Id,
    string DrugName,
    string? Dosage,
    string? Frequency,
    string Source,
    string PrescribedBy);

/// <summary>
/// An upcoming appointment (patients.js:891-900) — the shape
/// <c>client/src/pages/PatientDashboardPage.jsx:190-215</c> reads.
///
/// <para>These keys are NEW. The pre-fix Node mapping emitted <c>date</c> and <c>type</c> read
/// from <c>a.date</c> / <c>a.type</c>, neither of which is a column, and the query above it
/// filtered on <c>date</c> and status <c>'SCHEDULED'</c> — so it always threw into a
/// <c>.catch(() =&gt; [])</c> and the array was permanently empty. No client has ever received
/// an element, which is why changing the element shape breaks nothing.</para>
/// </summary>
public sealed record PatientDashboardAppointment(
    string Id,
    DateTime AppointmentDate,
    string StartTime,
    string EndTime,
    string? ClinicName,
    string DoctorName,
    string Type,
    string Status);

/// <summary>
/// A recent visit (patients.js:901-910). <c>Diagnosis</c> is a Postgres text ARRAY and reaches
/// the client as a JSON array, never a string.
/// </summary>
public sealed record PatientDashboardVisit(
    string Id,
    DateTime VisitDate,
    string DoctorName,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnosis,
    DateTime? FollowUpDate,
    string? FollowUpNotes,
    string ForMember);

/// <summary>A dependant summary card (patients.js:842-850).</summary>
public sealed record PatientDashboardFamilyMember(
    string Id,
    string Name,
    string Relation,
    string? Gender,
    int AllergiesCount,
    int ConditionsCount,
    int MedicationsCount,
    int VisitsCount);

/// <summary>
/// A reminder (patients.js:911-916).
///
/// <para><b>Never populated, and that is byte-exact rather than a shortfall.</b> The Node query
/// (patients.js:822-832) filters <c>isActive</c> and <c>dueDate</c>, and the Prisma
/// <c>Reminder</c> model (schema.prisma:1518-1543) has NEITHER — its fields are <c>status</c>
/// and <c>remindAt</c>. Every call raises a <c>PrismaClientValidationError</c> straight into the
/// trailing <c>.catch(() =&gt; [])</c>, so live Node returns <c>[]</c> here for every patient,
/// always. The mapping below it reads <c>r.dueDate</c>, which is not a column either.</para>
/// </summary>
public sealed record PatientDashboardReminder(
    string Id,
    string Title,
    DateTime? DueDate,
    string Type);

/// <summary>
/// The four counters (patients.js:852-857). Each has its OWN source: <c>TotalVisits</c> is a
/// dedicated <c>visit.count</c> over ALL statuses — not <c>recentVisits.Count</c>, which is
/// capped at 5 and filtered — and <c>ActiveMedications</c> is the length of the merged
/// self + prescribed array, not of either half.
/// </summary>
public sealed record PatientDashboardStats(
    int TotalDoctors,
    int TotalVisits,
    int ActiveMedications,
    int FamilyMembers);

/// <summary>
/// An outstanding follow-up (patients.js:903-915), derived from the SAME five recent visits —
/// Node does not re-query — keeping only those with a <c>followUpDate</c>, sorted ASCENDING by
/// that date, which reverses the parent list's ordering.
/// </summary>
public sealed record PatientDashboardFollowUp(
    string VisitId,
    DateTime FollowUpDate,
    string? FollowUpNotes,
    string DoctorName,
    string? ChiefComplaint,
    bool IsOverdue,
    string ForMember);

// ── GET /api/patients/health-summary ─────────────────────────────────────────

/// <summary>
/// The health summary (patients.js:1128-1138): one big Markdown string, the instant it was
/// generated, and five counters.
/// </summary>
/// <param name="Summary">
/// Markdown, newline-joined. Assembled line by line in the handler — the assembly IS the
/// contract, down to the blank lines between sections.
/// </param>
/// <param name="GeneratedAt">
/// <c>new Date().toISOString()</c> — a STRING in Node, and kept a string here so it serializes
/// with the same millisecond-precision "Z" form rather than through the DateTime converter.
/// </param>
/// <param name="Stats">The five counters.</param>
public sealed record PatientHealthSummaryResponse(
    string Summary,
    string GeneratedAt,
    PatientHealthSummaryStats Stats);

/// <summary>
/// The summary's counters (patients.js:1131-1137).
///
/// <para><c>Allergies</c> and <c>Conditions</c> count the SELF subprofile's records when one
/// exists and the account holder's own only when it does not — the same either/or the summary
/// text uses. <c>Medications</c> is the sum of three separately-sourced lists, and
/// <c>RecentVisits</c> the sum of platform visits and self-logged external ones.</para>
/// </summary>
public sealed record PatientHealthSummaryStats(
    int Allergies,
    int Conditions,
    int Medications,
    int RecentVisits,
    int FamilyMembers);

// ── GET /api/patients/medications/reconciled ─────────────────────────────────

/// <summary>
/// The reconciled medication list (patients.js:640-651): every entry from both sources in one
/// array, plus the tallies.
/// </summary>
/// <param name="Medications">
/// Heterogeneous — <see cref="ReconciledSelfMedication"/> and
/// <see cref="ReconciledPrescribedMedication"/> — sorted by <c>addedAt</c> DESCENDING across
/// both sources.
/// </param>
/// <param name="Summary">The four tallies.</param>
public sealed record ReconciledMedicationsResponse(
    IReadOnlyList<object> Medications,
    ReconciledMedicationsSummary Summary);

/// <summary>
/// A patient-managed entry (patients.js:585-598), plus the <c>isDuplicate</c> flag stamped on
/// afterwards (patients.js:636).
/// </summary>
/// <param name="Id">The <c>CurrentMedication</c> row id, unprefixed.</param>
/// <param name="DrugName">The column, always a string.</param>
/// <param name="Dosage">The column.</param>
/// <param name="Frequency">The column.</param>
/// <param name="Source">Always <c>"SELF_REPORTED"</c>.</param>
/// <param name="PrescribedBy">
/// Free text the PATIENT typed — <c>m.prescribedBy || null</c>, so an empty string becomes null.
/// It is not a resolved physician name, unlike the prescribed entry's field of the same name.
/// </param>
/// <param name="StartDate">The column.</param>
/// <param name="EndDate">The column.</param>
/// <param name="IsActive">Always true — the query filters on it.</param>
/// <param name="ForMember">The dependant's name, or <c>"Self"</c>.</param>
/// <param name="AddedAt">The row's <c>createdAt</c>, which is also the sort key.</param>
/// <param name="CanDelete">Always true: the patient owns this row.</param>
/// <param name="IsDuplicate">True when the same drug name also arrives from a prescription.</param>
public sealed record ReconciledSelfMedication(
    string Id,
    string DrugName,
    string? Dosage,
    string? Frequency,
    string Source,
    string? PrescribedBy,
    DateTime? StartDate,
    DateTime? EndDate,
    bool IsActive,
    string ForMember,
    DateTime AddedAt,
    bool CanDelete,
    bool IsDuplicate);

/// <summary>
/// A physician-prescribed entry (patients.js:604-618), one per drug line in a prescription's
/// opaque <c>medications</c> JSON, plus <c>isDuplicate</c>.
///
/// <para>Note the key set differs from the self-reported entry in BOTH directions: this one adds
/// <c>duration</c>, <c>instructions</c>, <c>visitDate</c>, <c>visitComplaint</c> and
/// <c>prescriptionId</c>, and omits <c>startDate</c>, <c>endDate</c> and <c>isActive</c>.</para>
/// </summary>
/// <param name="Id">
/// <c>rx-{prescriptionId}-{drugName}</c> — and the <c>{drugName}</c> half is the RAW JSON value
/// interpolated by JavaScript, NOT the fallback chain used for <see cref="DrugName"/>. A drug
/// line carrying only <c>name</c> therefore yields an id ending in the literal text
/// <c>undefined</c> while its <c>drugName</c> reads correctly. Reproduced deliberately: the id
/// is a client-side React key.
/// </param>
/// <param name="DrugName"><c>m.drugName || m.name || "Unknown"</c>, in that order.</param>
/// <param name="Dosage">From the drug line; empty becomes null.</param>
/// <param name="Frequency">From the drug line; empty becomes null.</param>
/// <param name="Duration">From the drug line; empty becomes null.</param>
/// <param name="Instructions">From the drug line; empty becomes null.</param>
/// <param name="Source">Always <c>"PRESCRIBED"</c>.</param>
/// <param name="PrescribedBy">The physician's display name, or <c>"Doctor"</c>.</param>
/// <param name="VisitDate">The prescribing visit's date.</param>
/// <param name="VisitComplaint">The prescribing visit's chief complaint; null when it has none.</param>
/// <param name="PrescriptionId">The prescription row id, for the refill and PDF routes.</param>
/// <param name="ForMember">The dependant's name, or <c>"Self"</c>.</param>
/// <param name="AddedAt">The PRESCRIPTION's <c>createdAt</c> — shared by every line it flattens into.</param>
/// <param name="CanDelete">Always false: the patient cannot delete a physician's prescription.</param>
/// <param name="IsDuplicate">True when the same drug name is also self-reported.</param>
public sealed record ReconciledPrescribedMedication(
    string Id,
    string DrugName,
    string? Dosage,
    string? Frequency,
    string? Duration,
    string? Instructions,
    string Source,
    string PrescribedBy,
    DateTime? VisitDate,
    string? VisitComplaint,
    string PrescriptionId,
    string ForMember,
    DateTime AddedAt,
    bool CanDelete,
    bool IsDuplicate);

/// <summary>
/// The tallies (patients.js:642-649). <c>Duplicates</c> counts ENTRIES flagged duplicate, not
/// distinct drug names — a drug held once self-reported and twice prescribed contributes 3.
/// </summary>
public sealed record ReconciledMedicationsSummary(
    int Total,
    int SelfReported,
    int Prescribed,
    int Duplicates);
