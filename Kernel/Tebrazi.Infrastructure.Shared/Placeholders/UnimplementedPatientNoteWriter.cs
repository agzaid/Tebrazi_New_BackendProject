using Microsoft.Extensions.Logging;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Infrastructure.Shared.Placeholders;

/// <summary>
/// The placeholder <see cref="IPatientNoteWriter"/>, registered so
/// <c>POST /api/appointments/{id}/intake-note</c> can be built and route-tested before the
/// PatientNotes module exists.
///
/// <para><b>What it does not reproduce.</b> The <c>patient_notes</c> upsert at
/// appointments.js:1157-1179 — the <c>startsWith</c> dedupe lookup, and the create that always
/// runs because that lookup never matches.</para>
///
/// <para><b>This one CANNOT degrade gracefully, and does not pretend to.</b> Unlike the
/// notification, waiting-room and payment placeholders, the note is this endpoint's whole
/// product: its id, content and timestamps ARE the 200 body. Returning a synthesised record
/// would report a note that nobody can read back, so it returns null instead, and the calling
/// handler is required to answer with Node's own failure body for the route,
/// <c>500 {"error":"Failed to save intake note"}</c>. Until PatientNotes lands, this endpoint is
/// a documented 500 — which is honest about the missing write, and is a status the client
/// already handles from Node's own catch.</para>
///
/// <para>Owner: the PatientNotes module. Replacing this means registering a real
/// <see cref="IPatientNoteWriter"/> AFTER <c>AddInfrastructureShared</c>, whose registration
/// then wins — and, on SQL Server, indexing around
/// <c>@@unique([physicianUserId, patientUserId, subprofileId])</c> with a NULL-tolerant filtered
/// index, or the second note for a physician/patient pair fails where Node's succeeds.</para>
/// </summary>
public sealed class UnimplementedPatientNoteWriter(
    ILogger<UnimplementedPatientNoteWriter> logger) : IPatientNoteWriter
{
    public Task<PatientNoteRecord?> UpsertNoteAsync(
        PatientNoteUpsert request, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Patient-note upsert SKIPPED for physician {PhysicianUserId} / patient "
            + "{PatientUserId}: the PatientNotes module is not ported, so no patient_notes row "
            + "was written and the caller has no row to answer with. Register a real "
            + "IPatientNoteWriter after AddInfrastructureShared to enable it.",
            request.PhysicianUserId,
            request.PatientUserId);

        return Task.FromResult<PatientNoteRecord?>(null);
    }
}
