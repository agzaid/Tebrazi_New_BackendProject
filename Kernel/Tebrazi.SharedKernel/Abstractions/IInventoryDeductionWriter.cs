namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// The published write port onto <c>inventory_items</c> and <c>inventory_transactions</c>, needed
/// by exactly one endpoint: <c>PUT /api/prescriptions/{id}/dispense</c> optionally decrements
/// clinic stock for a caller-supplied list of items and echoes what it took
/// (prescriptions.js:464-508). Prescriptions does not own either table — the Inventory module
/// will, and it does not exist yet.
///
/// <para><b>This port is LOAD-BEARING, not best-effort — the opposite of
/// <see cref="IMedicationReminderScheduler"/>.</b> Its result IS part of the 200 body
/// (<c>inventoryDeducted</c>), and Node does NOT wrap the inventory loop in a try/catch of its
/// own: a Prisma failure inside it propagates to the route's outer catch and becomes
/// <c>500 {"error":"Failed to dispense prescription"}</c> — with the prescription ALREADY
/// DISPENSED (the status write happens first, at prescriptions.js:458-461) and every earlier item
/// already decremented. So a null result means "the deduction could not be performed", and the
/// caller MUST answer with that same 500 rather than a 200 with an empty array. A thrown
/// exception is equally acceptable and lands in the same place through the handler's file
/// guard.</para>
///
/// <para><b>An EMPTY list is a success, not a failure.</b> Node genuinely returns
/// <c>inventoryDeducted: []</c> whenever no items were supplied, the value was not an array, or
/// every item was skipped (missing id, falsy quantity, unknown item, inactive item, insufficient
/// stock), so the caller renders <c>[]</c> as a normal 200.</para>
///
/// <para>Because it commits through the Inventory unit of work, a call from inside
/// <c>ExecuteInTransactionAsync</c> is NOT covered by that transaction. Deduct after the
/// prescription's DISPENSED write has committed, which is also the Node ordering — and is why a
/// failed deduction leaves a dispensed prescription behind in both backends.</para>
/// </summary>
public interface IInventoryDeductionWriter
{
    /// <summary>
    /// Applies the deductions in request order and returns one line per item that actually moved.
    ///
    /// <para>Per item, the behaviour an implementation has to reproduce
    /// (prescriptions.js:466-508):</para>
    /// <list type="number">
    /// <item>Read the item by id with <b>NO clinic or tenant scoping at all</b> — Node's
    /// <c>findUnique({ where: { id: itemId } })</c> accepts any inventory item in the database, so
    /// a physician can decrement another clinic's stock. Reproduce it; it is the contract.</item>
    /// <item>Skip silently — no line, no transaction row, no error — when the item does not exist
    /// or its <c>isActive</c> is false.</item>
    /// <item>In ONE transaction per item (not one for the batch): decrement
    /// <c>currentStock</c> by the quantity, and insert an <c>inventory_transactions</c> row
    /// <c>{ itemId, type: "DISPENSED", quantity: -qty, previousStock: &lt;the stale pre-read
    /// value&gt;, newStock: Math.max(0, preRead - qty), prescriptionId, notes: "Dispensed via
    /// prescription", performedById }</c>. Note the sign flip: the transaction row stores
    /// <c>-qty</c> while the response line reports <c>+qty</c>.</item>
    /// <item>If the post-decrement <c>currentStock</c> is negative, increment it back by the same
    /// quantity in a bare update OUTSIDE any transaction and omit the item from the result —
    /// <b>leaving the transaction row in place</b>. That orphan row is a real defect in the Node
    /// code and is part of the observable behaviour; do not tidy it away.</item>
    /// <item>Otherwise emit a line whose <c>NewStock</c> is read back from the post-decrement row,
    /// which under concurrency differs from the clamped value written into the transaction
    /// row.</item>
    /// </list>
    ///
    /// <para>A negative quantity is legal and INCREASES stock: it passes Node's truthiness test,
    /// <c>decrement: -5</c> adds five, the &lt; 0 rollback test does not fire, and the response
    /// reports <c>quantity: -5</c> against a raised <c>newStock</c>.</para>
    /// </summary>
    /// <param name="request">The prescription being dispensed and the items to take.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The lines to serialize as <c>inventoryDeducted</c>, in request order minus skipped items —
    /// empty when nothing moved. <b>Null when the deduction could not be performed</b>, which the
    /// caller must render as <c>500 {"error":"Failed to dispense prescription"}</c> on an
    /// already-DISPENSED prescription.
    /// </returns>
    Task<IReadOnlyList<InventoryDeductionLine>?> DeductForPrescriptionAsync(
        InventoryDeductionRequest request, CancellationToken ct = default);
}

/// <param name="PrescriptionId">
/// Stamped on every <c>inventory_transactions</c> row as <c>prescription_id</c>. The raw
/// <c>:id</c> route segment in Node (prescriptions.js:485).
/// </param>
/// <param name="PerformedByUserId">
/// <c>inventory_transactions.performed_by_id</c> — the dispensing physician's USER id
/// (<c>req.user.id</c>, prescriptions.js:487), not their profile id.
/// </param>
/// <param name="Items">
/// The requested deductions, in the caller's order. The caller has already applied Node's own
/// two filters — <c>Array.isArray(inventoryItems) &amp;&amp; length &gt; 0</c> and the
/// <c>if (!itemId || !quantity) continue</c> truthiness skip (prescriptions.js:465-467) — because
/// both are quirks of the dispense contract rather than of inventory. An empty list here means
/// "nothing to do" and must yield an empty result, never null.
/// </param>
public sealed record InventoryDeductionRequest(
    string PrescriptionId,
    string PerformedByUserId,
    IReadOnlyList<InventoryDeductionItem> Items);

/// <param name="ItemId">
/// An <c>inventory_items</c> id, echoed straight back in the response line. Deliberately
/// unscoped — see <see cref="IInventoryDeductionWriter.DeductForPrescriptionAsync"/>.
/// </param>
/// <param name="Quantity">
/// The units to take, already through the caller's <c>parseInt</c> (so "5 boxes" is 5 and 1.9 is
/// 1, matching prescriptions.js:471).
///
/// <para><b>Null means the caller's <c>parseInt</c> produced NaN</b> — a truthy but unparseable
/// quantity such as "abc". Node sends that NaN into Prisma's <c>decrement</c>, which throws, so
/// an implementation must process items IN ORDER and, on reaching a null quantity, stop and
/// return null after the preceding items have already been applied. Pre-validating the whole
/// list and failing before any write would diverge: Node leaves the earlier deductions
/// committed.</para>
/// </param>
public sealed record InventoryDeductionItem(string ItemId, int? Quantity);

/// <summary>
/// Exactly the four keys an <c>inventoryDeducted</c> element carries — no more
/// (prescriptions.js:505).
/// </summary>
/// <param name="ItemId">
/// Echoed from the request. Note the key is <c>itemId</c>, not <c>id</c>: it does not match the
/// inventory item's own primary-key field name.
/// </param>
/// <param name="Name">
/// <c>inventory_items.name</c> from the PRE-decrement read — the one response value that comes
/// from a different model than everything else in the body.
/// </param>
/// <param name="Quantity">
/// The POSITIVE number taken, even though the transaction row stores its negation.
/// </param>
/// <param name="NewStock">
/// The item's <c>currentStock</c> read back AFTER the atomic decrement, which is not clamped and
/// can differ from the clamped value stored on the transaction row.
/// </param>
public sealed record InventoryDeductionLine(
    string ItemId,
    string Name,
    int Quantity,
    int NewStock);
