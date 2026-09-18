using Microsoft.Extensions.Logging;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Infrastructure.Shared.Placeholders;

/// <summary>
/// The placeholder <see cref="IWaitingQueueWriter"/>, registered so the two endpoints that push
/// onto the waiting room can be built and route-tested before the WaitingRoom module exists.
///
/// <para><b>What it does not reproduce.</b> The <c>waiting_queues</c> insert at
/// appointments.js:670-684 (<c>POST /api/appointments/walk-in</c>) and appointments.js:1048-1060
/// (<c>PUT /api/appointments/{id}/check-in</c>), together with the already-queued test and the
/// <c>queueNumber</c> assignment that precede each. A patient checked in against this
/// implementation is NOT in the clinic's waiting room, and
/// <c>GET /api/waiting-room/...</c> — an endpoint this port's owner will publish — would not
/// list them.</para>
///
/// <para><b>What is unaffected.</b> Both calling endpoints answer correctly regardless: neither
/// response body carries any queue field, and Node itself swallows a failed enqueue. And
/// <c>GET /api/appointments/queue</c> does not read this table at all — it reads
/// <c>appointments</c> and <c>visits</c> and parses the <c>CHECKIN:</c> marker out of
/// <c>appointments.notes</c> (appointments.js:1236-1287), all of which the Appointments module
/// owns, so it returns real data.</para>
///
/// <para>Owner: the WaitingRoom module. Replacing this means registering a real
/// <see cref="IWaitingQueueWriter"/> AFTER <c>AddInfrastructureShared</c>, whose registration
/// then wins.</para>
/// </summary>
public sealed class UnimplementedWaitingQueueWriter(
    ILogger<UnimplementedWaitingQueueWriter> logger) : IWaitingQueueWriter
{
    public Task EnqueueIfAbsentAsync(WaitingQueueEnrolment enrolment, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Waiting-room enqueue SKIPPED for patient {PatientUserId} at clinic {ClinicId} "
            + "(appointment {AppointmentId}, queue date {QueueDate:yyyy-MM-dd}): the WaitingRoom "
            + "module is not ported, so no waiting_queues row was written. Register a real "
            + "IWaitingQueueWriter after AddInfrastructureShared to enable it.",
            enrolment.PatientUserId,
            enrolment.ClinicId,
            enrolment.AppointmentId,
            enrolment.QueueDate);

        return Task.CompletedTask;
    }
}
