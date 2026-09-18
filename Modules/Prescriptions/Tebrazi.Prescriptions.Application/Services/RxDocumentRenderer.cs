using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tebrazi.Prescriptions.Application.Services;

/// <summary>
/// Port of <c>generateRxPDFHtml</c> from <c>server/src/services/pdfService.js</c>, which backs
/// <c>GET /api/prescriptions/{id}/pdf</c>.
///
/// <b>It returns HTML, not a PDF.</b> The endpoint's name is misleading: the Node route sets
/// <c>Content-Type: text/html</c> and sends this markup, which carries a print button and
/// <c>@page</c> rules so the browser produces the PDF. Returning a binary here would break the
/// client, which opens the response in a tab.
///
/// The function is pure — no I/O, no database — so it is a static helper in Application rather
/// than a port with an implementation elsewhere.
/// </summary>
public static class RxDocumentRenderer
{
    /// <summary>
    /// Renders the prescription. Every interpolated value is HTML-escaped exactly as the Node
    /// <c>escapeHtml</c> does; see <see cref="Escape"/> for why the entity set matters.
    /// </summary>
    public static string RenderHtml(RxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // `signedAt || createdAt`, formatted en-GB as "03 Sep 2026". The Node call uses
        // toLocaleDateString with the SERVER's local timezone, not UTC, and the same string is
        // reused in the patient bar and the signature block.
        var rxDate = (document.SignedAt ?? document.CreatedAt)
            .ToLocalTime()
            .ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("en-GB"));

        // The first eight characters of the id, upper-cased.
        var rxNumber = document.PrescriptionId.Length <= 8
            ? document.PrescriptionId.ToUpperInvariant()
            : document.PrescriptionId[..8].ToUpperInvariant();

        var rows = new StringBuilder();
        var index = 1;

        foreach (var med in document.Medications)
        {
            // `escapeHtml(x) || '—'`: escapeHtml returns "" for a null or empty value, and ""
            // is falsy in JS, so an em dash stands in. The drug name column falls back to ""
            // rather than the dash — a different default in the same row.
            rows.Append(CultureInfo.InvariantCulture, $"""

                    <tr>
                      <td class="med-num">{index}</td>
                      <td class="med-name">
                        <strong>{Escape(med.DrugName)}</strong>
                        {(string.IsNullOrEmpty(med.Instructions)
                            ? string.Empty
                            : $"""<div class="med-instr">{Escape(med.Instructions)}</div>""")}
                      </td>
                      <td>{Dash(med.Dosage)}</td>
                      <td>{Dash(med.Frequency)}</td>
                      <td>{Dash(med.Duration)}</td>
                    </tr>

                """);
            index++;
        }

        var clinicAddress = string.Join(", ",
            new[] { document.ClinicAddress, document.ClinicCity }
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(Escape));

        var logo = string.IsNullOrEmpty(document.ClinicLogo)
            ? string.Empty
            : $"""<img src="{Escape(document.ClinicLogo)}" alt="" style="height:50px;margin-bottom:6px;" />""";

        var clinicPhone = string.IsNullOrEmpty(document.ClinicPhone)
            ? string.Empty
            : $"<p>Tel: {Escape(document.ClinicPhone)}</p>";

        var licence = string.IsNullOrEmpty(document.PhysicianLicenseNumber)
            ? string.Empty
            : $"<p>License: {Escape(document.PhysicianLicenseNumber)}</p>";

        var notes = string.IsNullOrEmpty(document.Notes)
            ? string.Empty
            : $"""

              <div class="notes-section">
                <strong>Notes:</strong> {Escape(document.Notes)}
              </div>
              """;

        // The clinic name falls back to the literal "Clinic" when empty, matching
        // `escapeHtml(clinic.name) || 'Clinic'`.
        var clinicName = string.IsNullOrEmpty(document.ClinicName) ? "Clinic" : Escape(document.ClinicName);

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>Rx {{rxNumber}} — {{Escape(document.PhysicianName)}}</title>
              <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                  font-family: 'Segoe UI', -apple-system, system-ui, sans-serif;
                  max-width: 210mm; margin: 0 auto; padding: 20mm 15mm;
                  color: #1a1a2e; font-size: 13px; line-height: 1.5;
                  background: #fff;
                }
                .rx-header {
                  display: flex; justify-content: space-between; align-items: flex-start;
                  padding-bottom: 14px; margin-bottom: 18px;
                  border-bottom: 3px solid #1e3a5f;
                }
                .rx-header .clinic-info h2 {
                  color: #1e3a5f; font-size: 1.4rem; margin-bottom: 3px;
                }
                .rx-header .clinic-info p {
                  color: #666; font-size: 11px; margin: 1px 0;
                }
                .rx-header .doc-info {
                  text-align: right;
                }
                .rx-header .doc-info h3 {
                  color: #1e3a5f; font-size: 1.1rem; margin-bottom: 3px;
                }
                .rx-header .doc-info p {
                  color: #666; font-size: 11px; margin: 1px 0;
                }
                .rx-symbol {
                  font-size: 2rem; font-weight: 900; color: #1e3a5f;
                  font-family: serif; margin-bottom: 12px;
                }
                .patient-bar {
                  background: #f4f6f8; border: 1px solid #e2e5ea;
                  padding: 10px 14px; border-radius: 6px; margin-bottom: 18px;
                  display: flex; gap: 24px; font-size: 12px;
                }
                .patient-bar strong { color: #1e3a5f; }

                table.meds {
                  width: 100%; border-collapse: collapse; margin-bottom: 18px;
                }
                table.meds thead th {
                  background: #1e3a5f; color: #fff; padding: 8px 10px;
                  text-align: left; font-size: 11px; text-transform: uppercase;
                  letter-spacing: 0.5px;
                }
                table.meds tbody td {
                  padding: 10px; border-bottom: 1px solid #e8e8ee;
                  vertical-align: top; font-size: 12.5px;
                }
                table.meds tbody tr:nth-child(even) { background: #fafbfc; }
                .med-num { width: 30px; text-align: center; color: #999; }
                .med-name strong { color: #1e3a5f; font-size: 13px; }
                .med-instr { color: #888; font-size: 11px; font-style: italic; margin-top: 2px; }

                .notes-section {
                  background: #fffbeb; border: 1px solid #fde68a;
                  padding: 10px 14px; border-radius: 6px; margin-bottom: 20px;
                  font-size: 12px;
                }
                .notes-section strong { color: #92400e; }

                .signature-block {
                  margin-top: 36px; padding-top: 14px;
                  border-top: 1px solid #ddd; text-align: right;
                }
                .signature-block .doc-name {
                  color: #1e3a5f; font-weight: 700; font-size: 14px;
                }
                .signature-block .sig-note {
                  color: #aaa; font-size: 10px; margin-top: 2px;
                }
                .rx-footer {
                  margin-top: 30px; text-align: center; color: #bbb;
                  font-size: 9px; border-top: 1px solid #eee; padding-top: 10px;
                }

                /* Print optimization */
                @media print {
                  body { padding: 10mm; margin: 0; }
                  .rx-header { break-inside: avoid; }
                  table.meds { break-inside: auto; }
                  table.meds tr { break-inside: avoid; }
                  .signature-block { break-inside: avoid; }
                  @page { size: A4; margin: 12mm; }
                }

                /* Auto-print trigger button (hidden in print) */
                .print-btn {
                  position: fixed; top: 16px; right: 16px; z-index: 999;
                  padding: 10px 20px; background: #1e3a5f; color: #fff;
                  border: none; border-radius: 8px; font-size: 14px;
                  font-weight: 700; cursor: pointer;
                }
                .print-btn:hover { background: #153050; }
                @media print { .print-btn { display: none; } }
              </style>
            </head>
            <body>
              <button class="print-btn" onclick="window.print()">🖨 Print / Save PDF</button>

              <!-- Header -->
              <div class="rx-header">
                <div class="clinic-info">
                  {{logo}}
                  <h2>{{clinicName}}</h2>
                  <p>{{clinicAddress}}</p>
                  {{clinicPhone}}
                </div>
                <div class="doc-info">
                  <h3>Dr. {{Escape(document.PhysicianName)}}</h3>
                  <p>{{Escape(document.PhysicianSpecialty)}}</p>
                  {{licence}}
                </div>
              </div>

              <!-- Rx Symbol -->
              <div class="rx-symbol">℞</div>

              <!-- Patient Info -->
              <div class="patient-bar">
                <div><strong>Patient:</strong> {{Escape(document.PatientName)}}</div>
                <div><strong>Date:</strong> {{rxDate}}</div>
                <div><strong>Rx #:</strong> {{rxNumber}}</div>
              </div>

              <!-- Medications Table -->
              <table class="meds">
                <thead>
                  <tr>
                    <th>#</th>
                    <th>Medication</th>
                    <th>Dosage</th>
                    <th>Frequency</th>
                    <th>Duration</th>
                  </tr>
                </thead>
                <tbody>
                  {{rows}}
                </tbody>
              </table>
            {{notes}}
              <!-- Signature -->
              <div class="signature-block">
                <div class="doc-name">Dr. {{Escape(document.PhysicianName)}}</div>
                <div class="sig-note">Electronically signed via Tebrazi · {{rxDate}}</div>
              </div>

              <!-- Footer -->
              <div class="rx-footer">
                Generated by Tebrazi — Digital Healthcare Platform &nbsp;|&nbsp; tebrazi.com
              </div>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Reads the stored <c>medications</c> JSON into the four fields the table renders.
    /// Anything that is not a JSON array — or a array element that is not an object — yields no
    /// rows rather than throwing, because the column is unvalidated free JSON and the Node
    /// template simply maps over whatever is there.
    /// </summary>
    public static IReadOnlyList<RxMedication> ParseMedications(string? medicationsJson)
    {
        if (string.IsNullOrWhiteSpace(medicationsJson)) return [];

        try
        {
            using var parsed = JsonDocument.Parse(medicationsJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array) return [];

            var list = new List<RxMedication>();

            foreach (var element in parsed.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                list.Add(new RxMedication(
                    ReadString(element, "drugName"),
                    ReadString(element, "dosage"),
                    ReadString(element, "frequency"),
                    ReadString(element, "duration"),
                    ReadString(element, "instructions")));
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The Node <c>escapeHtml</c>, entity for entity: <c>&amp;</c> first (or the later
    /// replacements would be double-escaped), then <c>&lt; &gt; " '</c>. The apostrophe becomes
    /// the NUMERIC reference <c>&amp;#39;</c>, not <c>&amp;apos;</c> — <c>WebUtility.HtmlEncode</c>
    /// produces <c>&amp;#39;</c> too but also escapes non-ASCII characters, which would mangle
    /// Arabic drug names that the Node version passes through untouched.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);
    }

    /// <summary>Escapes, falling back to an em dash when the result is empty.</summary>
    private static string Dash(string? value)
    {
        var escaped = Escape(value);
        return escaped.Length == 0 ? "—" : escaped;
    }
}

/// <summary>
/// Everything the template needs, already resolved by the handler. The renderer performs no
/// lookups of its own — the clinic, physician and patient all live in other modules and reach
/// it through their ports.
/// </summary>
/// <param name="PatientName">
/// Resolved by the Node route in this precedence: the subprofile as
/// <c>"{name} ({relation})"</c>, then the clinic chart's name, then the platform user's display
/// name, then the literal "Patient".
/// </param>
public sealed record RxDocument(
    string PrescriptionId,
    DateTime? SignedAt,
    DateTime CreatedAt,
    string? Notes,
    string? ClinicName,
    string? ClinicAddress,
    string? ClinicCity,
    string? ClinicPhone,
    string? ClinicLogo,
    string PhysicianName,
    string? PhysicianSpecialty,
    string? PhysicianLicenseNumber,
    string PatientName,
    IReadOnlyList<RxMedication> Medications);

public sealed record RxMedication(
    string? DrugName,
    string? Dosage,
    string? Frequency,
    string? Duration,
    string? Instructions);
