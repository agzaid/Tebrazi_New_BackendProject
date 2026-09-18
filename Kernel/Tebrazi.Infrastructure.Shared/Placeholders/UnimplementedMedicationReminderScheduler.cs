using Microsoft.Extensions.Logging;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Infrastructure.Shared.Placeholders;

/// <summary>
/// The placeholder <see cref="IMedicationReminderScheduler"/>, registered so
/// <c>PUT /api/prescriptions/{id}/send</c> can be built and route-tested before the Reminders
/// module exists.
///
/// <para><b>What it does not reproduce.</b> The <c>reminders</c> work at
/// prescriptions.js:369-404 — the case-sensitive <c>title contains drugName</c> purge of the
/// patient's ACTIVE medication reminders, and the one row per dose time that replaces them. A
/// patient whose prescription was sent against this implementation gets NO medication reminders,
/// and their previously existing ones are left exactly as they were rather than being purged.
/// The dose schedule itself IS computed — it lives on the caller's side in
/// <c>Tebrazi.Prescriptions.Application.Services.MedicationSchedule</c> — so the rows arrive here
/// fully formed and are simply dropped.</para>
///
/// <para><b>Why the endpoint still answers correctly.</b> Nothing about the reminders appears in
/// the <c>/send</c> response: no count, no ids, no flag. Returning 0 is a value Node itself
/// produces whenever a prescription's medications carry no usable <c>frequency</c>, and the only
/// consequence is that the caller skips the <c>MEDICATION_REMINDER_SET</c> notification — which
/// is the right answer, because no reminders were set. So the 200 body is byte-identical to
/// Node's and the patient's inbox is honest; what is missing is the reminders themselves.</para>
///
/// <para>Owner: the Reminders module. Replacing this means registering a real
/// <see cref="IMedicationReminderScheduler"/> AFTER <c>AddInfrastructureShared</c>, whose
/// registration then wins.</para>
/// </summary>
public sealed class UnimplementedMedicationReminderScheduler(
    ILogger<UnimplementedMedicationReminderScheduler> logger) : IMedicationReminderScheduler
{
    public Task<int> ScheduleAsync(MedicationReminderPlan plan, CancellationToken ct = default)
    {
        var doseCount = plan.Medications.Sum(m => m.Doses.Count);

        logger.LogWarning(
            "Medication-reminder scheduling SKIPPED for prescription {PrescriptionId} / patient "
            + "{PatientUserId}: the Reminders module is not ported, so {DrugCount} drug(s) and "
            + "{DoseCount} dose row(s) were dropped and no ACTIVE medication reminders were "
            + "purged. The prescription is still SENT and the response is unaffected. Register a "
            + "real IMedicationReminderScheduler after AddInfrastructureShared to enable it.",
            plan.PrescriptionId,
            plan.PatientUserId,
            plan.Medications.Count,
            doseCount);

        return Task.FromResult(0);
    }
}
