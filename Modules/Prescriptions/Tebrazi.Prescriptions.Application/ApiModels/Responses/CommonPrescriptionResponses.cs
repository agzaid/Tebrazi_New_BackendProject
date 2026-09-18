namespace Tebrazi.Prescriptions.Application.ApiModels.Responses;

/// <summary>
/// The five-key medication line the two AI extraction endpoints emit —
/// <c>POST /api/prescriptions/extract-from-plan</c> (prescriptions.js:1128-1136) and
/// <c>POST /api/prescriptions/transcribe-rx</c> (prescriptions.js:1040-1048).
///
/// <para>Shared because the two bodies are byte-identical here and the KEY ORDER is the contract:
/// both routes build the element from the same object literal, so the wire order is
/// <c>drugName, dosage, frequency, duration, instructions</c> and nothing else. Declaring it
/// twice would let the two drift.</para>
///
/// <para><b>Every value is a non-null trimmed string.</b> Both routes normalize with
/// <c>String(m.x || '').trim()</c>, so a missing field, a null, a literal <c>0</c> and a literal
/// <c>false</c> all become <c>""</c>, and a numeric dosage of 500 becomes <c>"500"</c>. Never
/// emit null for one of these.</para>
///
/// <para>This is NOT the shape stored in <c>prescriptions.medications</c>. That column is opaque
/// JSON echoed verbatim, keeps whatever extra keys the client sent, and grows
/// <c>stoppedAt</c>/<c>stoppedByPatient</c> from the stop-medication route. Never round-trip a
/// stored medication through this record.</para>
/// </summary>
/// <param name="DrugName">
/// The only field either route requires: elements are filtered by <c>m &amp;&amp; m.drugName</c>
/// before normalization, so an element that reaches here always has a truthy name and the array
/// can be shorter than what the model produced.
/// </param>
/// <param name="Dosage">e.g. "500mg". Empty string when the model omitted it.</param>
/// <param name="Frequency">e.g. "Three times daily". Empty string when the model omitted it.</param>
/// <param name="Duration">e.g. "7 days". Empty string when the model omitted it.</param>
/// <param name="Instructions">
/// Free text, e.g. "After meals". Empty string when the model omitted it — which is the common
/// case, so <c>"instructions": ""</c> appears in almost every real response.
/// </param>
public sealed record RxMedicationLine(
    string DrugName,
    string Dosage,
    string Frequency,
    string Duration,
    string Instructions);
