namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The published write port onto <c>payments</c>, needed by exactly one endpoint:
/// <c>PUT /api/appointments/{id}/check-in</c> auto-creates a PENDING cash payment as the
/// patient arrives, because in an Egyptian clinic reception collects the fee BEFORE the
/// consultation (appointments.js:1064-1106). Appointments does not own the table — the Payments
/// module will, and it does not exist yet.
///
/// <para>The FEE is decided by the caller, not here. Follow-up detection
/// (<c>IVisitDirectory.CountCompletedAsync</c> &gt; 0) and the
/// <c>followUpFee || 200</c> / <c>consultationFee || 300</c> fallbacks are appointment-side
/// contract quirks — the Node code uses <c>||</c>, so a fee explicitly configured as 0 is falsy
/// and silently becomes 200/300 — and hiding them in here would put them out of reach of the
/// handler that has to reproduce them byte for byte.</para>
///
/// <para><b>This port never throws.</b> The Node call site wraps the whole block in a try/catch
/// that logs at warning level and leaves <c>autoPayment</c> null, so a payment that cannot be
/// written still answers 200 with <c>"payment": null</c>. An implementation therefore returns
/// null rather than raising — and a caller MUST NOT read null as "no payment exists". Node
/// returns null in three different situations: a payment already existed for the appointment, no
/// clinic row could be read, or the write failed.</para>
///
/// <para>Because it commits through the Payments unit of work, a call from inside
/// <c>ExecuteInTransactionAsync</c> is NOT covered by that transaction. Create the payment after
/// the appointment write has committed, which is also the Node ordering.</para>
/// </summary>
public interface IPaymentWriter
{
    /// <summary>
    /// Creates the payment unless one already references
    /// <see cref="AppointmentPaymentRequest.AppointmentId"/> — the Node guard is a bare
    /// <c>findFirst({ where: { appointmentId } })</c> with no status or clinic filter
    /// (appointments.js:1087-1089), so a CANCELLED or REFUNDED payment still suppresses a new
    /// one.
    /// </summary>
    /// <returns>
    /// The three fields the check-in response serializes, or null when nothing was created —
    /// whether because a payment already existed or because the write could not be performed.
    /// A null result is a valid 200 for the calling endpoint, not an error to surface.
    /// </returns>
    Task<AppointmentPaymentSummary?> CreateForAppointmentIfAbsentAsync(
        AppointmentPaymentRequest request, CancellationToken ct = default);
}

/// <param name="ClinicId">The clinic collecting the fee, copied off the appointment.</param>
/// <param name="PhysicianId">
/// A physician-PROFILE id (<c>payments.physician_id</c>), copied straight off the appointment —
/// not the physician's user id.
/// </param>
/// <param name="PatientUserId">Who owes the fee, copied off the appointment.</param>
/// <param name="AppointmentId">
/// The appointment being checked in. Also the duplicate key: a payment already referencing it
/// suppresses a new one.
/// </param>
/// <param name="Amount">
/// Prisma <c>Float</c>, so a plain JSON number on the wire. Passed in already resolved: the
/// caller has applied the follow-up test and the falsy-zero fallbacks.
/// </param>
/// <param name="Description">
/// "Follow-up visit fee" or "Consultation fee" (appointments.js:1100). Echoed verbatim in the
/// response.
/// </param>
/// <param name="Currency">"EGP" on this path (appointments.js:1098).</param>
/// <param name="Method">"CASH" on this path (appointments.js:1099).</param>
/// <param name="Status">
/// "PENDING" on this path (appointments.js:1101) — reception has not collected yet.
/// </param>
public sealed record AppointmentPaymentRequest(
    string ClinicId,
    string PhysicianId,
    string PatientUserId,
    string AppointmentId,
    double Amount,
    string Description,
    string Currency = "EGP",
    string Method = "CASH",
    string Status = "PENDING");

/// <summary>
/// Exactly the three keys the check-in body's <c>payment</c> object carries — no more
/// (appointments.js:1112). The full payment row belongs to the Payments module's own endpoints.
/// </summary>
public sealed record AppointmentPaymentSummary(string Id, double Amount, string? Description);
