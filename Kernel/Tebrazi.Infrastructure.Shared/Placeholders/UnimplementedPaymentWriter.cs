using Microsoft.Extensions.Logging;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Infrastructure.Shared.Placeholders;

/// <summary>
/// The placeholder <see cref="IPaymentWriter"/>, registered so
/// <c>PUT /api/appointments/{id}/check-in</c> can be built and route-tested before the Payments
/// module exists.
///
/// <para><b>What it does not reproduce.</b> The <c>payments</c> insert at
/// appointments.js:1091-1103 — a PENDING, CASH, EGP row for the consultation or follow-up fee —
/// and the <c>findFirst({ appointmentId })</c> duplicate guard in front of it. Reception is NOT
/// shown an amount to collect for a patient checked in against this implementation.</para>
///
/// <para><b>Why the endpoint still answers correctly.</b> It returns null, which the check-in
/// response renders as <c>"payment": null</c> — a value Node genuinely produces on three of its
/// own paths (a payment already exists, the clinic row could not be read, or the write threw).
/// So the response stays a shape the client already handles, and nothing is fabricated. What
/// IS lost is the distinction: against this implementation <c>payment</c> is ALWAYS null, and a
/// client cannot tell that from "already paid".</para>
///
/// <para>Owner: the Payments module. Replacing this means registering a real
/// <see cref="IPaymentWriter"/> AFTER <c>AddInfrastructureShared</c>, whose registration then
/// wins.</para>
/// </summary>
public sealed class UnimplementedPaymentWriter(
    ILogger<UnimplementedPaymentWriter> logger) : IPaymentWriter
{
    public Task<AppointmentPaymentSummary?> CreateForAppointmentIfAbsentAsync(
        AppointmentPaymentRequest request, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Auto-payment SKIPPED for appointment {AppointmentId} at clinic {ClinicId} "
            + "({Amount} {Currency}, {Description}): the Payments module is not ported, so no "
            + "payments row was written and the check-in response will carry payment: null. "
            + "Register a real IPaymentWriter after AddInfrastructureShared to enable it.",
            request.AppointmentId,
            request.ClinicId,
            request.Amount,
            request.Currency,
            request.Description);

        return Task.FromResult<AppointmentPaymentSummary?>(null);
    }
}
