using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Abstractions.Directory;
using Tebrazi.SharedKernel.Exceptions;
using Tebrazi.SharedKernel.Logging;
using Tebrazi.SharedKernel.Mediation;
using Tebrazi.Visits.Application.Abstractions.Persistence;
using Tebrazi.Visits.Application.ApiModels.Responses;
using Tebrazi.Visits.Application.Services;

namespace Tebrazi.Visits.Application.UseCases;

// ════════════════════════════════════════════════════════════════════════════
//  The three AI endpoints on a visit: generate-soap, suggest-codes, transcribe.
//
//  All three sit behind aiUsageCheck in Node (server/src/services/aiUsageLimiter.js), which is
//  NOT ported: the 403 "Feature not available on your plan" and 429 "Monthly AI limit reached"
//  rejections, and the real call counting behind generate-soap's `aiUsage` block, do not exist
//  here yet. See the note on VisitAiUsageResponse.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// JavaScript value semantics these handlers depend on. The Node code coerces model output with
/// bare <c>String(value)</c> and gates on truthiness, and both are observable in what gets
/// persisted and returned — so they are reproduced rather than approximated.
/// </summary>
internal static class VisitAiJsValues
{
    /// <summary>JS truthiness of a raw request-body value: only false, 0, "" and null/absent are falsy.</summary>
    public static bool IsTruthy(JsonElement? value)
        => value?.ValueKind switch
        {
            null or JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => value.Value.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => value.Value.GetString() is { Length: > 0 },
            _ => true
        };

    /// <inheritdoc cref="IsTruthy(JsonElement?)"/>
    public static bool IsTruthy(JsonNode? value)
    {
        if (value is null) return false;

        return value.GetValueKind() switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.Number => value.AsValue().TryGetValue<double>(out var d) && d != 0,
            JsonValueKind.String => Stringify(value).Length > 0,
            _ => true
        };
    }

    /// <summary>
    /// <c>String(value)</c>. Numbers use invariant formatting, booleans are lowercase, an array
    /// becomes its comma-joined elements and any other object becomes the literal
    /// "[object Object]" — which is what Node persists into <c>specialtyData</c> if the model
    /// nests something.
    /// </summary>
    public static string Stringify(JsonNode? value)
    {
        if (value is null) return string.Empty;

        switch (value.GetValueKind())
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return string.Empty;
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.String:
                return value.AsValue().TryGetValue<string>(out var s) ? s ?? string.Empty : string.Empty;
            case JsonValueKind.Number:
                return value.AsValue().TryGetValue<double>(out var d)
                    ? d.ToString("R", CultureInfo.InvariantCulture)
                    : value.ToJsonString();
            case JsonValueKind.Array:
                return string.Join(",", value.AsArray().Select(Stringify));
            default:
                return "[object Object]";
        }
    }

    /// <summary>The node's string value, or null when it is not a JSON string.</summary>
    public static string? AsString(JsonNode? value)
        => value is not null
           && value.GetValueKind() == JsonValueKind.String
           && value.AsValue().TryGetValue<string>(out var s)
            ? s
            : null;

    /// <summary>Parses an opaque JSON column, yielding null for anything that is not an object.</summary>
    public static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// ── POST /api/visits/{id}/generate-soap ──────────────────────────────────────

/// <summary>
/// Turns the visit's raw notes into a SOAP note.
/// </summary>
/// <param name="Enhance">
/// The body's <c>enhance</c> value, RAW. Node applies <c>!!enhance</c>, so <c>"yes"</c> and
/// <c>1</c> both mean true; binding it as a <c>bool</c> would reject those with a 400 the Node
/// route never sends. The controller should hand the value straight through.
/// </param>
/// <param name="SpecialtyKey">
/// The client's template choice. Honoured only when it names a real specialty template; it is
/// the highest-priority tier of the four-tier detection chain.
/// </param>
public sealed record VisitAiGenerateSoapCommand(
    string VisitId,
    string UserId,
    JsonElement? Enhance,
    string? SpecialtyKey) : IRequest<VisitAiSoapResult>;

/// <summary>
/// Port of <c>POST /api/visits/{id}/generate-soap</c> (visits.js:674-938).
///
/// <para>There is no visit-status guard, deliberately: unlike <c>PUT /api/visits/{id}</c>, this
/// endpoint happily regenerates and overwrites the SOAP note of a COMPLETED visit that has
/// already been shared with the patient. That is a real hole and it is part of the contract.</para>
///
/// <para>The AI block's catch wraps the DATABASE WRITE as well as the model calls, so a failed
/// save re-enters the template fallback and writes boilerplate over the AI text. Narrowing the
/// catch would change behaviour, so it is left wide.</para>
/// </summary>
public sealed class VisitAiGenerateSoapHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IIdentityDirectory identity,
    IPatientDirectory patients,
    IAiGateway ai,
    IAppLogger<VisitAiGenerateSoapHandler> logger)
    : IRequestHandler<VisitAiGenerateSoapCommand, VisitAiSoapResult>
{
    /// <summary>
    /// Section headers, matched exactly as the Node regex does — no <c>Multiline</c>, so <c>^</c>
    /// only anchors the start of the response and every other header must be preceded by a newline.
    ///
    /// <para><c>CultureInvariant</c> is load-bearing, not decoration. .NET's
    /// <see cref="RegexOptions.IgnoreCase"/> folds case using the AMBIENT culture, and
    /// <c>InvariantGlobalization</c> is off in Directory.Build.props: under tr-TR or az-AZ the
    /// dotted/dotless i splits, so "Subjective:" and "Objective:" stop matching SUBJECTIVE and
    /// OBJECTIVE, the marker count drops below three and the endpoint silently returns the
    /// synthesized fewer-than-three-headers body instead of the parsed note. The JS <c>i</c> flag
    /// is culture-independent, so this is what reproduces it.</para>
    /// </summary>
    private static readonly Regex SoapHeaderPattern = new(
        @"(?:^|\n)\s*(SUBJECTIVE|OBJECTIVE|ASSESSMENT|PLAN)\s*:\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Greedy on purpose: first "{" to LAST "}", which is how code fences get stripped.</summary>
    private static readonly Regex JsonObjectPattern = new(@"\{[\s\S]*\}", RegexOptions.Compiled);

    /// <summary>
    /// TIER_LIMITS.monthlyAiCalls (aiUsageLimiter.js:18-44). CLINIC is deliberately absent: it is
    /// unlimited, and the wire value for unlimited is null. An unknown tier is also absent, which
    /// makes the limiter skip the usage block entirely.
    /// </summary>
    private static readonly Dictionary<string, int> MonthlyAiCallLimits = new(StringComparer.Ordinal)
    {
        ["FREE"] = 50,
        ["PRO"] = 500,
        ["PATIENT_FREE"] = 20,
        ["PATIENT_FAMILY"] = 100
    };

    private const string UnlimitedTier = "CLINIC";

    /// <summary>The key the extraction writes into <c>specialtyData</c> to remember its template.</summary>
    private const string TemplateKeySentinel = "__templateKey";

    public async Task<VisitAiSoapResult> Handle(
        VisitAiGenerateSoapCommand request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GenerateAsync(request, cancellationToken);
        }
        catch (AppException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The outer catch (visits.js:934). An AI failure never lands here — it degrades to the
            // 200 template fallback — so this is reached only by a database or mapping failure.
            logger.Error("Generate SOAP error", ex, new { request.VisitId });
            throw new BusinessException(
                "Failed to generate SOAP note", "Failed to generate SOAP note", 500);
        }
    }

    private async Task<VisitAiSoapResult> GenerateAsync(
        VisitAiGenerateSoapCommand request, CancellationToken ct)
    {
        var visit = await visits.GetForUpdateAsync(request.VisitId, ct)
            ?? throw new NotFoundException("Visit not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, ct);
        if (physician is null || visit.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your visit");

        if (string.IsNullOrEmpty(visit.RawNotes) && string.IsNullOrEmpty(visit.RawTranscript))
            throw new BusinessException(
                "No notes or transcript to process", "No notes or transcript to process");

        // rawNotes WINS. A long voice transcript is silently ignored when rawNotes has anything
        // in it at all, which is the common case because the recorder writes into rawNotes.
        var source = !string.IsNullOrEmpty(visit.RawNotes)
            ? visit.RawNotes
            : visit.RawTranscript ?? string.Empty;

        var enhance = VisitAiJsValues.IsTruthy(request.Enhance);
        var patientContext = await BuildPatientContextAsync(visit.SubprofileId, ct);
        var specialtyData = VisitAiJsValues.ParseObject(visit.SpecialtyData);

        try
        {
            var activeTemplateKey = ResolveTemplateKey(
                request.SpecialtyKey, specialtyData, physician.Specialty);

            logger.Information("SOAP generation", new
            {
                ActiveTemplateKey = activeTemplateKey ?? "none",
                physician.Specialty
            });

            var specialtyContext = FormatSpecialtyContext(specialtyData, activeTemplateKey);

            var result = await ai.ChatAsync(
                new AiChatRequest(
                    System: BuildSoapSystemPrompt(patientContext),
                    User: BuildSoapUserPrompt(visit.ChiefComplaint, source, specialtyContext),
                    Temperature: 0.2,
                    MaxTokens: enhance ? 3000 : 2000,
                    // 'soap_enhanced' routes the provider chain to OpenAI first, 'soap' to Gemini.
                    Agent: enhance ? "soap_enhanced" : "soap",
                    UserId: request.UserId),
                ct);

            var sections = ParseSoapSections(result.Text, visit.ChiefComplaint);

            // A SECOND, independently degrading model call. Its failure leaves the endpoint on the
            // success shape with specialtyData null — it does NOT trigger the template fallback.
            var merged = activeTemplateKey is null
                ? null
                : await TryExtractSpecialtyDataAsync(
                    activeTemplateKey, specialtyData, source, request.UserId, ct);

            visit.ApplySoap(sections.Subjective, sections.Objective, sections.Assessment, sections.Plan);

            // The column is left ALONE when nothing was extracted, rather than being nulled.
            if (merged is not null)
                visit.Update(specialtyData: merged.ToJsonString());

            await dbContext.SaveChangesAsync(ct);

            return new VisitAiGenerateSoapResponse(
                Message: enhance
                    ? "SOAP note generated with GPT-4o (enhanced). Review and edit as needed."
                    : "SOAP note generated by AI. Review and edit as needed.",
                Subjective: visit.Subjective,
                Objective: visit.Objective,
                Assessment: visit.Assessment,
                Plan: visit.Plan,
                AiProvider: result.Provider,
                AiModel: result.Model,
                Enhanced: enhance,
                AiUsage: await TryBuildUsageAsync(request.UserId, ct),
                SpecialtyData: merged,
                SpecialtyKey: activeTemplateKey);
        }
        // Every AI failure lands here, not just AiUnavailableException — Node's catch is bare, and
        // with no provider configured this is the path that actually runs.
        catch (Exception aiError) when (aiError is not OperationCanceledException)
        {
            logger.Error("AI SOAP error, using template fallback", aiError, new { request.VisitId });

            var template = BuildTemplateSoap(visit.ChiefComplaint, visit.FollowUpNotes, source);

            visit.ApplySoap(template.Subjective, template.Objective, template.Assessment, template.Plan);
            await dbContext.SaveChangesAsync(ct);

            return new VisitAiSoapTemplateResponse(
                Message: "SOAP note generated (template — AI unavailable). Edit as needed.",
                Subjective: visit.Subjective,
                Objective: visit.Objective,
                Assessment: visit.Assessment,
                Plan: visit.Plan,
                AiProvider: "template");
        }
    }

    // ── Patient context ──────────────────────────────────────────────────────

    /// <summary>
    /// Four newline-joined lines injected into the SYSTEM prompt, and only when the visit names a
    /// dependant. Wrapped in a bare catch like the Node block (visits.js:697-713): a failure here
    /// silently costs the model its context rather than failing the request.
    /// </summary>
    private async Task<string> BuildPatientContextAsync(string? subprofileId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subprofileId)) return string.Empty;

        try
        {
            var sub = await patients.GetSubprofileClinicalContextAsync(subprofileId, ct);
            if (sub is null) return string.Empty;

            var allergies = string.Join(", ", sub.Allergies.Select(a =>
                $"{a.Allergen} ({(string.IsNullOrEmpty(a.Severity) ? "unknown" : a.Severity)})"));
            var conditions = string.Join(", ", sub.Conditions);
            // The trailing space when dosage is blank is Node's — `${m.drugName} ${m.dosage || ''}`.
            var medications = string.Join(", ", sub.Medications.Select(m =>
                $"{m.DrugName} {(string.IsNullOrEmpty(m.Dosage) ? string.Empty : m.Dosage)}"));

            string[] lines =
            [
                $"Patient: {sub.Name} ({sub.Relation})",
                $"Allergies: {Or(allergies, "None documented")}",
                $"Chronic Conditions: {Or(conditions, "None documented")}",
                $"Current Medications: {Or(medications, "None documented")}"
            ];

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            logger.Debug("Patient context unavailable for the SOAP prompt", new { Error = ex.Message });
            return string.Empty;
        }
    }

    // ── Specialty template detection ─────────────────────────────────────────

    /// <summary>
    /// The four-tier chain, first hit wins (visits.js:727-747): the client's key, then the
    /// <c>__templateKey</c> sentinel stored on the visit, then a heuristic over the stored field
    /// keys, then the physician's own specialty through the alias table. May still be null, in
    /// which case no exam-data context is added and the extraction call is skipped entirely.
    /// </summary>
    private static string? ResolveTemplateKey(
        string? clientKey, JsonObject? specialtyData, string? physicianSpecialty)
    {
        if (SpecialtyTemplates.TryGetTemplate(clientKey) is { } fromClient)
            return fromClient.Key;

        var storedKey = VisitAiJsValues.AsString(specialtyData?[TemplateKeySentinel]);
        if (SpecialtyTemplates.TryGetTemplate(storedKey) is { } fromSentinel)
            return fromSentinel.Key;

        if (specialtyData is not null)
        {
            var dataKeys = specialtyData
                .Select(property => property.Key)
                .Where(key => key != TemplateKeySentinel)
                .ToList();

            if (dataKeys.Count > 0)
            {
                // Declaration order, first match wins, two or more overlapping keys required —
                // one is not enough. Ordered is the JS insertion order, so ophthalmology is
                // tested first.
                foreach (var template in SpecialtyTemplates.Ordered)
                {
                    var fields = template.Sections
                        .SelectMany(section => section.Fields)
                        .Select(field => field.Key)
                        .ToHashSet(StringComparer.Ordinal);

                    if (dataKeys.Count(fields.Contains) >= 2)
                        return template.Key;
                }
            }
        }

        return SpecialtyTemplates.GetTemplateForSpecialty(physicianSpecialty)?.Key;
    }

    private static string FormatSpecialtyContext(JsonObject? specialtyData, string? activeTemplateKey)
    {
        if (specialtyData is null || activeTemplateKey is null) return string.Empty;

        try
        {
            return SpecialtyTemplates.FormatSpecialtyDataForPrompt(specialtyData, activeTemplateKey);
        }
        catch
        {
            // visits.js:751-755 continues without the block rather than failing the generation.
            return string.Empty;
        }
    }

    // ── The model calls ──────────────────────────────────────────────────────

    private static string BuildSoapSystemPrompt(string patientContext)
        => SoapSystemHead
           + "\n\n"
           + (patientContext.Length > 0
               ? "\nPatient context (for reference only — do NOT add anything from here that the physician did not mention in their notes):\n"
                 + patientContext
               : string.Empty)
           + "\n\n"
           + SoapSystemTail;

    private static string BuildSoapUserPrompt(string? chiefComplaint, string source, string specialtyContext)
        => $"Chief complaint: {Or(chiefComplaint, "Not specified")}\n\nRaw clinical notes:\n{source}"
           + (specialtyContext.Length > 0
               ? $"\n\nStructured Specialty Exam Data:\n{specialtyContext}"
               : string.Empty);

    /// <summary>
    /// Splits the model's answer on the four headers. Fewer than three headers is not treated as
    /// a failure: it produces a DIFFERENT synthesized note that still reports as an AI success —
    /// note the subjective line has no trailing period, unlike the template fallback's.
    /// A repeated header overwrites the earlier capture, so the last occurrence wins.
    /// </summary>
    private static (string Subjective, string Objective, string Assessment, string Plan)
        ParseSoapSections(string? responseText, string? chiefComplaint)
    {
        var text = responseText ?? string.Empty;
        var markers = SoapHeaderPattern.Matches(text);

        if (markers.Count < 3)
        {
            return (
                $"Patient presents with: {Or(chiefComplaint, "unspecified complaint")}",
                string.Empty,
                text.Trim(),
                "Review AI-generated assessment above and edit as needed.");
        }

        var sections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["subjective"] = string.Empty,
            ["objective"] = string.Empty,
            ["assessment"] = string.Empty,
            ["plan"] = string.Empty
        };

        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            sections[markers[i].Groups[1].Value.ToLowerInvariant()] = text[start..end].Trim();
        }

        return (sections["subjective"], sections["objective"], sections["assessment"], sections["plan"]);
    }

    /// <summary>
    /// The second model call: pulls structured exam fields out of the same dictation and merges
    /// them over whatever the visit already stored. Every failure — no field map, a gateway
    /// throw, unparseable JSON, zero usable fields — returns null and is logged only.
    /// </summary>
    private async Task<JsonObject?> TryExtractSpecialtyDataAsync(
        string templateKey, JsonObject? existingData, string source, string userId, CancellationToken ct)
    {
        var fieldMap = SpecialtyTemplates.GetFieldMapForPrompt(templateKey);
        if (fieldMap.Length == 0) return null;

        try
        {
            logger.Information("Extracting specialty data for template", new { TemplateKey = templateKey });

            var extractResult = await ai.ChatAsync(
                new AiChatRequest(
                    System: SpecialtyExtractSystemPrompt,
                    User: "Extract values for these fields from the clinical notes below.\n\n"
                          + $"Fields:\n{fieldMap}\n\nClinical notes:\n{source}",
                    Temperature: 0.1,
                    MaxTokens: 500,
                    Agent: "specialty_extract",
                    UserId: userId),
                ct);

            var raw = (extractResult.Text ?? string.Empty).Trim();
            var match = JsonObjectPattern.Match(raw);
            if (!match.Success) return null;

            JsonObject? extracted;
            try
            {
                extracted = JsonNode.Parse(match.Value) as JsonObject;
            }
            catch (JsonException parseError)
            {
                logger.Warning("Failed to parse extraction JSON", new
                {
                    Error = parseError.Message,
                    RawSnippet = raw.Length > 200 ? raw[..200] : raw
                });
                return null;
            }

            if (extracted is null || extracted.Count == 0) return null;

            var merged = existingData is null
                ? new JsonObject()
                : (JsonObject)existingData.DeepClone();

            var fieldsAdded = 0;
            foreach (var (key, value) in extracted)
            {
                // null/undefined and the empty STRING are skipped; an empty array is not, and
                // lands in the column as "". There is no whitelist against the template's own
                // field keys, so a hallucinated key is persisted verbatim.
                if (value is null || value.GetValueKind() is JsonValueKind.Null) continue;

                var text = VisitAiJsValues.Stringify(value);
                if (value.GetValueKind() is JsonValueKind.String && text.Length == 0) continue;

                // Every value is coerced to a string, so a numeric IOP of 14 is stored as "14".
                merged[key] = JsonValue.Create(text);
                fieldsAdded++;
            }

            if (fieldsAdded == 0) return null;

            merged[TemplateKeySentinel] = templateKey;
            logger.Information("Populated specialty fields into visit", new { FieldsAdded = fieldsAdded });
            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error("Specialty extraction AI call failed", ex);
            return null;
        }
    }

    // ── Degraded paths ───────────────────────────────────────────────────────

    /// <summary>
    /// The non-AI note: the raw notes cut into thirds by line, under fixed headings. Blank lines
    /// are dropped; surviving lines keep their own leading and trailing whitespace.
    /// </summary>
    private static (string Subjective, string Objective, string Assessment, string Plan)
        BuildTemplateSoap(string? chiefComplaint, string? followUpNotes, string source)
    {
        var lines = source
            .Split('\n')
            .Where(line => line.Trim().Length > 0)
            .ToList();

        // Math.ceil on a real division, not integer division: for n = 1 the first third takes the
        // only line and the other two come out empty.
        var firstBoundary = (int)Math.Ceiling(lines.Count / 3.0);
        var secondBoundary = (int)Math.Ceiling(2.0 * lines.Count / 3.0);

        var first = string.Join("\n", lines.Take(firstBoundary));
        var middle = string.Join("\n", lines.Skip(firstBoundary).Take(secondBoundary - firstBoundary));
        var last = string.Join("\n", lines.Skip(secondBoundary));

        return (
            // The trailing period after the complaint is present HERE and absent in the
            // fewer-than-three-headers branch above. Both are the contract.
            $"Patient presents with: {Or(chiefComplaint, "unspecified complaint")}.\n\nPatient reports:\n"
                + Or(first, "(See raw notes)"),
            "Physical examination findings:\n" + Or(middle, "(Pending examination documentation)"),
            "Clinical assessment:\n" + Or(last, "(Pending clinical assessment)"),
            // Echoes the physician's own follow-up text back into the plan.
            "Treatment plan:\n- Follow-up as needed\n- " + Or(followUpNotes, "Continue monitoring"));
    }

    /// <summary>
    /// The <c>aiUsage</c> block. Real metering is NOT ported — there is no monthly call count — so
    /// <c>used</c> is 0 and <c>remaining</c> equals the tier's limit. The shape is exact: the
    /// unlimited CLINIC tier reports null limit and remaining, and an unknown tier makes the Node
    /// limiter skip the block entirely, which reaches the client as <c>"aiUsage": null</c>.
    /// </summary>
    private async Task<VisitAiUsageResponse?> TryBuildUsageAsync(string userId, CancellationToken ct)
    {
        try
        {
            var tier = await identity.GetSubscriptionTierAsync(userId, ct);
            var tierName = string.IsNullOrEmpty(tier) ? "FREE" : tier;

            if (string.Equals(tierName, UnlimitedTier, StringComparison.Ordinal))
                return new VisitAiUsageResponse(0, null, null, tierName);

            return MonthlyAiCallLimits.TryGetValue(tierName, out var limit)
                ? new VisitAiUsageResponse(0, limit, limit, tierName)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The limiter fails open, and a request that got through it carries no usage block.
            logger.Warning("AI usage lookup failed; reporting no usage", new { Error = ex.Message });
            return null;
        }
    }

    private static string Or(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;

    // ── Prompts, verbatim from visits.js ─────────────────────────────────────

    private const string SoapSystemHead =
        """
        You are a clinical documentation assistant. Your ONLY job is to organize the physician's raw notes into SOAP format. You are a SCRIBE, not a clinician.

        CRITICAL RULES:
        1. NEVER add information that is not explicitly stated in the raw notes or structured exam fields
        2. NEVER suggest differential diagnoses unless the physician mentioned them
        3. NEVER add treatment recommendations (like "smoking cessation advised", "lifestyle modifications", "follow-up appointments") unless the physician explicitly dictated them
        4. NEVER infer or assume clinical findings, assessments, or plans
        5. If the physician only stated a diagnosis, put ONLY that diagnosis in Assessment — do NOT add differentials
        6. If the physician only listed medications given, put ONLY those medications in Plan — do NOT add recommendations
        7. If information for a section is not in the notes, write "(Not documented)"
        8. If specialty-specific structured exam data is provided, incorporate it into the OBJECTIVE section in a well-organized format

        Output format — use these EXACT section headers:
        SUBJECTIVE:
        (ONLY symptoms, history, and complaints the physician mentioned)

        OBJECTIVE:
        (ONLY physical exam findings and test results the physician mentioned. Include any structured specialty exam data provided below. Organize specialty data clearly with headings.)

        ASSESSMENT:
        (ONLY the diagnosis or clinical impression the physician stated. Include ICD-10 code if you can identify it from the stated diagnosis. Do NOT add differentials unless the physician mentioned them.)

        PLAN:
        (ONLY the medications, interventions, and instructions the physician dictated. Do NOT add your own recommendations.)
        """;

    private const string SoapSystemTail =
        "You are transcribing, not practicing medicine. Output ONLY what the physician said.";

    private const string SpecialtyExtractSystemPrompt =
        """
        You are a medical data extraction tool. Extract structured exam data from clinical notes into JSON format.

        RULES:
        - Output ONLY a valid JSON object, nothing else. No markdown, no explanation, no code fences.
        - Only include fields where the physician explicitly stated a value.
        - For OD/OS (right/left eye) fields: "right" or "OD" → field ending in _od, "left" or "OS" → field ending in _os
        - For number fields (like IOP), output just the number as a string.
        - If a value is not mentioned, do NOT include that field.
        - Do NOT invent or guess values.
        """;
}

// ── POST /api/visits/{id}/suggest-codes ──────────────────────────────────────

/// <summary>
/// Suggests ICD-10-CM codes for a visit. The Node handler reads no body fields at all.
/// </summary>
public sealed record VisitAiSuggestCodesCommand(string VisitId, string UserId)
    : IRequest<VisitAiSuggestCodesResponse>;

/// <summary>
/// Port of <c>POST /api/visits/{id}/suggest-codes</c> (visits.js:1023-1116).
///
/// <para>Two failure behaviours that must not be merged: a gateway throw is a 500, while a
/// successful call whose text does not parse is a 200 with an empty list and a real
/// provider/model. Nothing is written to the database — the physician's accepted codes are
/// persisted later by <c>PUT /api/visits/{id}</c>.</para>
/// </summary>
public sealed class VisitAiSuggestCodesHandler(
    IVisitStore visits,
    IIdentityDirectory identity,
    IAiGateway ai,
    IAppLogger<VisitAiSuggestCodesHandler> logger)
    : IRequestHandler<VisitAiSuggestCodesCommand, VisitAiSuggestCodesResponse>
{
    private static readonly Regex JsonFencePattern = new("```json\n?", RegexOptions.Compiled);
    private static readonly Regex FencePattern = new("```\n?", RegexOptions.Compiled);

    public async Task<VisitAiSuggestCodesResponse> Handle(
        VisitAiSuggestCodesCommand request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SuggestAsync(request, cancellationToken);
        }
        catch (AppException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unlike generate-soap there is NO graceful fallback: a gateway failure is a 500.
            logger.Error("Suggest codes error", ex, new { request.VisitId });
            throw new BusinessException(
                "Failed to suggest diagnosis codes", "Failed to suggest diagnosis codes", 500);
        }
    }

    private async Task<VisitAiSuggestCodesResponse> SuggestAsync(
        VisitAiSuggestCodesCommand request, CancellationToken ct)
    {
        // Tracked load; nothing is modified, so nothing is saved.
        var visit = await visits.GetForUpdateAsync(request.VisitId, ct)
            ?? throw new NotFoundException("Visit not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, ct);
        if (physician is null || visit.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your visit");

        // The gate counts `objective` as content, but the prompt below never sends it. A visit
        // with nothing but an objective therefore passes and is coded from the chief complaint
        // alone. That asymmetry is the contract (visits.js:1039-1050).
        var hasSoapContent = new[] { visit.Assessment, visit.Plan, visit.Subjective, visit.Objective }
            .Any(section => !string.IsNullOrEmpty(section));

        if (!hasSoapContent && string.IsNullOrEmpty(visit.ChiefComplaint))
            throw new BusinessException(
                "No SOAP notes or chief complaint to analyze. Generate SOAP first.",
                "No SOAP notes or chief complaint to analyze. Generate SOAP first.");

        var existing = ReadExistingCodes(visit.DiagnosisCodes);

        var result = await ai.ChatAsync(
            new AiChatRequest(
                System: BuildSystemPrompt(existing.Rendered),
                User: BuildUserPrompt(visit.ChiefComplaint, visit.Assessment, visit.Plan, visit.Subjective),
                Temperature: 0.1,
                MaxTokens: 1000,
                UseCache: true,
                Agent: "icd_coder",
                UserId: request.UserId),
            ct);

        return new VisitAiSuggestCodesResponse(
            SuggestedCodes: ParseSuggestions(result.Text, existing.Assigned),
            AiProvider: result.Provider,
            AiModel: result.Model);
    }

    /// <summary>
    /// The codes already on the visit, read out of the opaque <c>diagnosisCodes</c> column —
    /// <c>Array.isArray(...) ? map(c =&gt; c.code) : []</c> (visits.js:1052-1054).
    ///
    /// <para>Two different products, because Node uses them differently: <c>Assigned</c> is the
    /// exclusion set, and only string values can ever match an upper-cased candidate, while
    /// <c>Rendered</c> is the ONE-PER-ITEM projection behind the do-not-repeat prompt line. They
    /// have different lengths on purpose: an item with no <c>code</c> still occupies a slot in
    /// <c>Rendered</c> — <c>["E11.9", undefined].join(", ")</c> is <c>"E11.9, "</c> — and the
    /// prompt line is gated on that COUNT, not on the joined text, so a column of
    /// <c>[{}]</c> still emits the line with an empty list after the colon. A column holding an
    /// object rather than an array excludes nothing and prints nothing.</para>
    /// </summary>
    private static (HashSet<string> Assigned, List<string> Rendered) ReadExistingCodes(string? diagnosisCodes)
    {
        JsonArray? codes = null;
        if (!string.IsNullOrWhiteSpace(diagnosisCodes))
        {
            try
            {
                codes = JsonNode.Parse(diagnosisCodes) as JsonArray;
            }
            catch (JsonException)
            {
                codes = null;
            }
        }

        if (codes is null) return (new HashSet<string>(StringComparer.Ordinal), new List<string>());

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var rendered = new List<string>(codes.Count);

        foreach (var entry in codes)
        {
            // A JSON null element is NOT tolerated, deliberately: `null.code` is a TypeError in
            // the Node map (visits.js:1054), it escapes to the outer catch and the endpoint
            // answers 500 {"error":"Failed to suggest diagnosis codes"}. Degrading to an empty
            // code here would turn that 500 into a 200 the client never sees from Express.
            if (entry is null)
                throw new InvalidOperationException(
                    "diagnosisCodes contains a null element; Node throws on `null.code`.");

            var code = entry is JsonObject item ? item["code"] : null;
            rendered.Add(VisitAiJsValues.Stringify(code));

            if (VisitAiJsValues.AsString(code) is { } text)
                assigned.Add(text);
        }

        return (assigned, rendered);
    }

    /// <summary>
    /// All-or-nothing and silent: any parse failure yields an empty list with HTTP 200, so the
    /// client cannot distinguish "no codes found" from "the model's answer was unusable".
    /// </summary>
    private IReadOnlyList<VisitAiSuggestedCodeResponse> ParseSuggestions(
        string? responseText, HashSet<string> assigned)
    {
        try
        {
            var cleaned = FencePattern
                .Replace(JsonFencePattern.Replace(responseText ?? string.Empty, string.Empty), string.Empty)
                .Trim();

            if (JsonNode.Parse(cleaned) is not JsonArray parsed)
                return [];

            var suggestions = new List<VisitAiSuggestedCodeResponse>();

            foreach (var entry in parsed)
            {
                if (entry is not JsonObject item) continue;

                var code = item["code"];
                var description = item["description"];

                // Truthy, not merely present: an empty-string code drops the whole item.
                if (!VisitAiJsValues.IsTruthy(code) || !VisitAiJsValues.IsTruthy(description))
                    continue;

                var codeText = VisitAiJsValues.Stringify(code).ToUpperInvariant();
                if (codeText.Length > 10) codeText = codeText[..10];

                var descriptionText = VisitAiJsValues.Stringify(description);
                if (descriptionText.Length > 200) descriptionText = descriptionText[..200];

                // Compares the UPPER-CASED, truncated candidate against the raw stored codes, so a
                // stored lowercase code does not match and gets suggested again.
                if (assigned.Contains(codeText)) continue;

                suggestions.Add(new VisitAiSuggestedCodeResponse(
                    codeText, descriptionText, ReadConfidence(item["confidence"])));

                // slice(0, 8) — the prompt asks for 2-6 codes and the cap is 8, applied to what
                // SURVIVES the exclusion filter. There is no de-duplication among the model's own
                // suggestions: a model that repeats E11.9 twice has it returned twice.
                if (suggestions.Count == 8) break;
            }

            return suggestions;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bare catch in Node, so every failure — not just bad JSON — degrades to [] with 200.
            logger.Error("Failed to parse ICD-10 suggestion response", ex);
            return [];
        }
    }

    /// <summary>A number is clamped to [0,1]; anything else, including an absent key, becomes 0.5.</summary>
    private static double ReadConfidence(JsonNode? confidence)
    {
        if (confidence is null || confidence.GetValueKind() != JsonValueKind.Number) return 0.5;
        if (!confidence.AsValue().TryGetValue<double>(out var value)) return 0.5;

        return Math.Max(0, Math.Min(1, value));
    }

    /// <summary>
    /// visits.js:1080 gates the do-not-repeat line on <c>existingCodes.length &gt; 0</c> — the
    /// COUNT of stored items, not the text they render to. So a stored <c>[{"description":"x"}]</c>
    /// still emits the line, with nothing after the colon.
    /// </summary>
    private static string BuildSystemPrompt(IReadOnlyList<string> existingCodes)
        => SystemHead
           + "\n"
           + (existingCodes.Count > 0
               ? $"- The following codes are already assigned — do NOT re-suggest them: {string.Join(", ", existingCodes)}"
               : string.Empty)
           + "\n\n"
           + SystemTail;

    private static string BuildUserPrompt(
        string? chiefComplaint, string? assessment, string? plan, string? subjective)
        => $"Chief Complaint: {Or(chiefComplaint, "Not specified")}\n\n"
           + $"SOAP Assessment:\n{Or(assessment, "(Not documented)")}\n\n"
           + $"SOAP Plan:\n{Or(plan, "(Not documented)")}\n\n"
           + $"Subjective:\n{Or(subjective, "(Not documented)")}";

    private static string Or(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;

    private const string SystemHead =
        """
        You are a medical coding assistant specializing in ICD-10-CM codes. Analyze the clinical documentation and suggest the most appropriate diagnosis codes.

        For each diagnosis, provide a JSON object with:
        - code: ICD-10-CM code (e.g., "E11.9", "I10", "J06.9")
        - description: Official ICD-10 code description
        - confidence: Number between 0.0 and 1.0 indicating certainty

        Rules:
        - Suggest 2-6 codes, ordered by confidence (highest first)
        - Use the most specific code available (e.g., E11.65 over E11.9 when documented)
        - Base codes ONLY on documented findings — do not infer undocumented diagnoses
        - Include the primary diagnosis first
        - Consider chronic conditions mentioned in the assessment
        - If symptoms are documented without a definitive diagnosis, use symptom codes (R-codes)
        """;

    private const string SystemTail =
        "Respond ONLY with a JSON array of code objects. No markdown, no explanation.";
}

// ── POST /api/visits/{id}/transcribe ─────────────────────────────────────────

/// <summary>
/// Transcribes an audio recording into the visit's running transcript.
/// </summary>
/// <param name="Audio">
/// The uploaded file's bytes. The controller owns the multipart binding, the 25 MB cap and the
/// <c>audio/*</c> filter — those rejections are rendered by the global error handler in Node and
/// come out as 500s with multer's own message, not 4xx.
/// </param>
/// <param name="FileName">Passed to the gateway for its temp file; Node always names it <c>.webm</c>.</param>
/// <param name="Language">
/// The multipart <c>language</c> field. Empty means "en" — Node's <c>req.body.language || 'en'</c>
/// makes Whisper's auto-detection unreachable from this route.
/// </param>
public sealed record VisitAiTranscribeCommand(
    string VisitId,
    string UserId,
    byte[] Audio,
    string? FileName,
    string? Language) : IRequest<VisitAiTranscribeResponse>;

/// <summary>
/// Port of <c>POST /api/visits/{id}/transcribe</c> (visits.js:964-1010).
///
/// <para>NO graceful degrade: every failure is a 500 whose body carries the raw exception message.
/// That leak is load-bearing — it is the only signal the client's recorder has that the provider
/// is unconfigured — so it is reproduced rather than tidied.</para>
///
/// <para>The transcript is APPENDED, read-modify-write with no locking, exactly as in Node: two
/// concurrent transcriptions of one visit lose a segment.</para>
/// </summary>
public sealed class VisitAiTranscribeHandler(
    IVisitsDbContext dbContext,
    IVisitStore visits,
    IIdentityDirectory identity,
    IAiGateway ai,
    IAppLogger<VisitAiTranscribeHandler> logger)
    : IRequestHandler<VisitAiTranscribeCommand, VisitAiTranscribeResponse>
{
    private const string RecordingSeparator = "\n\n--- New recording ---\n\n";

    public async Task<VisitAiTranscribeResponse> Handle(
        VisitAiTranscribeCommand request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await TranscribeAsync(request, cancellationToken);
        }
        catch (AppException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error("Transcribe error", ex, new { request.VisitId });

            // `error.message || 'Failed to transcribe audio'` — the internal message reaches the
            // client verbatim, e.g. the gateway's "All providers failed" text.
            var message = string.IsNullOrEmpty(ex.Message) ? "Failed to transcribe audio" : ex.Message;
            throw new BusinessException(message, message, 500);
        }
    }

    private async Task<VisitAiTranscribeResponse> TranscribeAsync(
        VisitAiTranscribeCommand request, CancellationToken ct)
    {
        // Node checks `!req.file`, which a zero-byte part would pass; there is no way to tell the
        // two apart once the file is a byte[], and an empty upload has nothing to transcribe.
        if (request.Audio is null or { Length: 0 })
            throw new BusinessException("No audio file provided", "No audio file provided");

        var visit = await visits.GetForUpdateAsync(request.VisitId, ct)
            ?? throw new NotFoundException("Visit not found");

        var physician = await identity.GetPhysicianByUserIdAsync(request.UserId, ct);
        if (physician is null || visit.PhysicianId != physician.Id)
            throw new ForbiddenException("Not your visit");

        var language = string.IsNullOrEmpty(request.Language) ? "en" : request.Language;

        var result = await ai.TranscribeAsync(
            new AiTranscriptionRequest(
                Audio: request.Audio,
                FileName: request.FileName,
                Language: language,
                Agent: "scribe",
                UserId: request.UserId),
            ct);

        // The marker appears only BETWEEN recordings — the first one gets no separator, and an
        // empty stored transcript counts as absent.
        var existing = visit.RawTranscript ?? string.Empty;
        var separator = existing.Length > 0 ? RecordingSeparator : string.Empty;
        var fullTranscript = existing + separator + result.Text;

        visit.SetTranscript(fullTranscript);
        await dbContext.SaveChangesAsync(ct);

        return new VisitAiTranscribeResponse(
            Transcript: result.Text,
            Duration: result.Duration,
            // Whisper's own language NAME wins; the requested code is only the fallback. The
            // gateway's expression is `transcription.language || language` (aiGateway.js:186), so
            // an EMPTY string falls back too — `??` alone would return "" where Node returns "en".
            Language: string.IsNullOrEmpty(result.Language) ? language : result.Language,
            Provider: result.Provider,
            FullTranscript: fullTranscript);
    }
}
