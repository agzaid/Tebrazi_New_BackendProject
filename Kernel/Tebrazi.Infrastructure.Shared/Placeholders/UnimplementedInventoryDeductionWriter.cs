using Microsoft.Extensions.Logging;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Infrastructure.Shared.Placeholders;

/// <summary>
/// The placeholder <see cref="IInventoryDeductionWriter"/>, registered so
/// <c>PUT /api/prescriptions/{id}/dispense</c> can be built and route-tested before the Inventory
/// module exists.
///
/// <para><b>What it does not reproduce.</b> Everything at prescriptions.js:466-508 — the unscoped
/// item read, the inactive-item skip, the per-item transaction pairing an atomic
/// <c>currentStock</c> decrement with a DISPENSED <c>inventory_transactions</c> row, the
/// negative-stock rollback that leaves that transaction row orphaned, and the NaN-quantity path
/// that fails the request outright. Stock levels do NOT change for a prescription dispensed
/// against this implementation, and no transaction history is recorded.</para>
///
/// <para><b>Why the endpoint still answers correctly.</b> It returns an EMPTY list, which the
/// dispense response renders as <c>"inventoryDeducted": []</c> — a value Node genuinely produces
/// on several of its own paths (no <c>inventoryItems</c> in the body, a non-array value, or every
/// item skipped), and the one the client actually sees today, because
/// <c>PrescriptionsPage.jsx</c> sends no body at all. So for the real client this placeholder is
/// behaviourally complete. What IS lost is the distinction: a caller that DOES send items gets
/// <c>[]</c> with no hint that nothing was taken, and the NaN-quantity 500 becomes a 200.</para>
///
/// <para>It returns empty rather than null deliberately. Null is this port's "could not be
/// performed" signal and would make the caller emit
/// <c>500 {"error":"Failed to dispense prescription"}</c> on every dispense — a status the
/// endpoint should not carry just because a downstream module is missing, given that the
/// prescription itself was dispensed successfully.</para>
///
/// <para>Owner: the Inventory module. Replacing this means registering a real
/// <see cref="IInventoryDeductionWriter"/> AFTER <c>AddInfrastructureShared</c>, whose
/// registration then wins.</para>
/// </summary>
public sealed class UnimplementedInventoryDeductionWriter(
    ILogger<UnimplementedInventoryDeductionWriter> logger) : IInventoryDeductionWriter
{
    public Task<IReadOnlyList<InventoryDeductionLine>?> DeductForPrescriptionAsync(
        InventoryDeductionRequest request, CancellationToken ct = default)
    {
        if (request.Items.Count > 0)
        {
            logger.LogWarning(
                "Inventory deduction SKIPPED for prescription {PrescriptionId}: the Inventory "
                + "module is not ported, so {ItemCount} requested item(s) "
                + "([{ItemIds}]) were NOT decremented, no inventory_transactions rows were "
                + "written, and the dispense response will carry inventoryDeducted: []. The "
                + "prescription is still DISPENSED. Register a real IInventoryDeductionWriter "
                + "after AddInfrastructureShared to enable it.",
                request.PrescriptionId,
                request.Items.Count,
                string.Join(", ", request.Items.Select(i => i.ItemId)));
        }

        return Task.FromResult<IReadOnlyList<InventoryDeductionLine>?>([]);
    }
}
