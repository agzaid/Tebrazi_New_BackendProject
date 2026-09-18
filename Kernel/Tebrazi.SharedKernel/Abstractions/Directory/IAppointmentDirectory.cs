namespace Tebrazi.SharedKernel.Abstractions.Directory;

/// <summary>
/// The published read port onto <c>appointments</c>, supplied by Appointments.
///
/// Visits needs exactly one thing from it: <c>POST /api/visits</c> accepts an
/// <c>appointmentId</c> and copies that appointment's <c>subprofileId</c> onto the new visit, so
/// a visit started from a booking inherits the dependant the booking was for.
/// </summary>
public interface IAppointmentDirectory
{
    Task<AppointmentSummary?> GetAppointmentAsync(
        string appointmentId, CancellationToken ct = default);

    // ── Patient-facing reads (added for GET /api/patients/dashboard) ──

    /// <summary>
    /// Upcoming appointments for a patient, for <c>GET /api/patients/dashboard</c>
    /// (patients.js:768-792): <c>appointmentDate >= now</c>, status in
    /// <b>PENDING</b> or <b>CONFIRMED</b>, ordered <c>appointmentDate ASC</c>, take 5.
    ///
    /// <b>PENDING, not SCHEDULED.</b> The Node query used to match <c>'SCHEDULED'</c>, which is
    /// not a member of <c>AppointmentStatus</c> — the pending state is <c>PENDING</c> — and to
    /// filter and order on a column named <c>date</c> when the column is
    /// <c>appointmentDate</c>. A trailing <c>.catch(() =&gt; [])</c> swallowed the resulting
    /// validation error, so the array was permanently empty. That query has been fixed in
    /// patients.js and this port mirrors the FIXED version.
    ///
    /// No soft-delete predicate: appointments.js never filters <c>deletedAt</c>, and
    /// <c>AppointmentsDbContext</c> has no query filter.
    ///
    /// The row carries ids, not names. <c>PhysicianId</c> resolves to the doctor's display name
    /// through <see cref="IIdentityDirectory"/> and <c>ClinicId</c> to the clinic name through
    /// <see cref="IClinicDirectory"/>; <c>ClinicName</c> comes back null for the caller to fill.
    /// </summary>
    Task<IReadOnlyList<PatientAppointmentRow>> ListUpcomingForPatientAsync(
        string patientUserId, int limit, CancellationToken ct = default);
}

/// <summary>
/// The narrow WRITE port onto <c>appointments</c>, published by Appointments because Appointments
/// owns the table. It exists for one endpoint:
/// <c>DELETE /api/connections/clinic-patients/{id}</c> (connections.js:879), whose transaction
/// removes a walk-in chart's appointments before the chart itself.
///
/// <para>Kept separate from <see cref="IAppointmentDirectory"/> so that port stays read-only, and
/// deliberately narrow — it can delete by chart id and nothing else. It commits through the
/// APPOINTMENTS unit of work, so this is its own commit; see the atomicity note in
/// docs/connections-surface.md §10.</para>
/// </summary>
public interface IAppointmentClinicPatientWriter
{
    /// <summary>
    /// HARD-deletes every appointment attached to a walk-in chart —
    /// <c>tx.appointment.deleteMany({ where: { clinicPatientId: id } })</c>.
    ///
    /// <para>No status filter, so PENDING, CONFIRMED, COMPLETED and CANCELLED bookings all go. No
    /// <c>deletedAt</c> predicate either — <c>appointments.js</c> never writes that column.</para>
    ///
    /// <para><b>Time slots are NOT released.</b> The Node transaction deletes appointment rows and
    /// nothing else, so a slot marked booked by one of them stays booked in both backends.</para>
    /// </summary>
    /// <param name="clinicPatientId">The chart whose appointments are to be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were deleted. Zero is normal and not an error.</returns>
    Task<int> DeleteForClinicPatientAsync(string clinicPatientId, CancellationToken ct = default);
}

/// <param name="StartTime">"HH:mm" — a string in the schema, not a time.</param>
public sealed record AppointmentSummary(
    string Id,
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string? SubprofileId,
    string? ClinicPatientId,
    string Status,
    DateTime AppointmentDate,
    string StartTime,
    string EndTime,
    string AppointmentType);

/// <summary>
/// An appointment projected for the patient dashboard.
///
/// Every field here is one the dashboard's <c>upcomingAppointments[]</c> element emits:
/// <c>id, appointmentDate, startTime, endTime, clinicName, doctorName, type, status</c>
/// (patients.js:891-900) — the shape <c>PatientDashboardPage.jsx:190-215</c> has always read.
/// <c>doctorName</c> comes from <c>PhysicianId</c> through <see cref="IIdentityDirectory"/> and
/// falls back to "Doctor"; <c>type</c> is <c>AppointmentType</c> and falls back to "IN_PERSON".
///
/// <c>StartTime</c> and <c>EndTime</c> are "HH:mm" STRINGS, as on the entity — the client sorts
/// and compares them as strings, so they must not become times on the way through.
///
/// <c>ClinicName</c> is ALWAYS NULL as returned by
/// <see cref="IAppointmentDirectory.ListUpcomingForPatientAsync"/>: the Appointments context
/// owns <c>appointments</c> and <c>time_slots</c> only. The caller fills it in place —
/// <c>row with { ClinicName = clinics[row.ClinicId].Name }</c> — from
/// <see cref="IClinicDirectory.GetClinicsAsync"/>, and Node's <c>a.clinic?.name || null</c>
/// means an unresolved clinic legitimately stays null.
/// </summary>
public sealed record PatientAppointmentRow(
    string Id,
    DateTime AppointmentDate,
    string StartTime,
    string EndTime,
    string Status,
    string PhysicianId,
    string ClinicId,
    string? ClinicName,
    string AppointmentType);
