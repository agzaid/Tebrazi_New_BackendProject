using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tebrazi.Visits.Application.Services;

/// <summary>
/// One specialty exam template — the structured fields the visit detail page renders between
/// Vitals and SOAP, whose values are stored in <c>visits.specialty_data</c>.
///
/// Member order is part of the contract: <c>JSON.stringify</c> emits JS insertion order and the
/// client caches the body, so these are declared exactly as
/// <c>server/src/config/specialtyTemplates.js</c> writes them.
/// </summary>
public sealed record SpecialtyTemplateDefinition(
    string Key,
    string Label,
    string Icon,
    IReadOnlyList<SpecialtyTemplateSection> Sections);

/// <summary>
/// A group of fields. <c>paired</c> is present only on the six sections that set
/// <c>paired: true</c> — all five ophthalmology sections and ENT's "Ear" — and the other 28 omit
/// the key entirely. It is never written as <c>false</c>, so a plain <c>bool</c> would emit
/// <c>"paired":false</c> on those 28 and change the payload.
/// </summary>
public sealed record SpecialtyTemplateSection(
    string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Paired,
    IReadOnlyList<SpecialtyTemplateField> Fields);

/// <summary>
/// One input. Of the 117 fields, 93 carry <c>placeholder</c>, 11 carry <c>unit</c>, 23 carry
/// <c>options</c>, and <c>obgyn.lmp</c> carries none of the three: the JS literal simply omits the
/// keys it does not set, so they are annotated to disappear rather than serialize as null — the
/// host's global policy is <c>DefaultIgnoreCondition = Never</c>.
///
/// This one declaration order reproduces all four of the config's key sets, because no field ever
/// carries both <c>unit</c> and <c>options</c> and no select carries a <c>placeholder</c>.
/// </summary>
public sealed record SpecialtyTemplateField(
    string Key,
    string Label,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Options,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Placeholder);

/// <summary>
/// The picker-dropdown projection <c>{ key, label, icon }</c> — the same templates with
/// <c>sections</c> dropped, which is what <c>allTemplates</c> carries.
/// </summary>
public sealed record SpecialtyTemplateSummary(string Key, string Label, string Icon);

/// <summary>
/// The port of <c>server/src/config/specialtyTemplates.js</c>: 11 exam templates, the 45-entry
/// alias table that maps a physician's free-text specialty onto one of them, and the two prompt
/// formatters the SOAP generator injects into its AI calls.
///
/// Nothing here is read from the database — it is a frozen constant in both backends — so this
/// class serves <c>GET /api/visits/specialty-template</c>, its by-key sibling, and
/// <c>POST /api/visits/{id}/generate-soap</c> alike.
/// </summary>
public static class SpecialtyTemplates
{
    /// <summary>
    /// The 11 templates in the JS object's own insertion order. That order is observable: it is
    /// the order of <c>allTemplates</c>, hence of the picker dropdown, and it also decides which
    /// template wins generate-soap's field-key sniffing. Never serve it out of a dictionary.
    /// </summary>
    public static IReadOnlyList<SpecialtyTemplateDefinition> Ordered { get; } = Build();

    /// <summary>The <c>allTemplates</c> array: every template minus its sections, same order.</summary>
    public static IReadOnlyList<SpecialtyTemplateSummary> Summaries { get; } =
        [.. Ordered.Select(t => new SpecialtyTemplateSummary(t.Key, t.Label, t.Icon))];

    // Ordinal, not OrdinalIgnoreCase: the by-key route indexes the object literal with the raw
    // path segment, so /specialty-template/Ophthalmology is a 404 in Node and stays one here.
    private static readonly IReadOnlyDictionary<string, SpecialtyTemplateDefinition> ByKey =
        Ordered.ToDictionary(t => t.Key, StringComparer.Ordinal);

    /// <summary>
    /// specialtyTemplates.js L399-456, verbatim — 45 entries, all keys already lowercase. Ordinal
    /// because the lookup key is lowercased first; an ignore-case comparer would change which
    /// inputs match. The Node module exports this map but nothing else reads it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ophthalmology"] = "ophthalmology",
        ["ophthal"] = "ophthalmology",
        ["eye"] = "ophthalmology",

        ["cardiology"] = "cardiology",
        ["cardiac"] = "cardiology",
        ["cardiovascular"] = "cardiology",

        ["neurology"] = "neurology",
        ["neuro"] = "neurology",
        ["neurological"] = "neurology",

        ["orthopedics"] = "orthopedics",
        ["orthopaedics"] = "orthopedics",
        ["orthopedic surgery"] = "orthopedics",
        ["ortho"] = "orthopedics",

        ["pediatrics"] = "pediatrics",
        ["paediatrics"] = "pediatrics",
        ["peds"] = "pediatrics",
        ["ped"] = "pediatrics",

        ["pulmonology"] = "pulmonology",
        ["pulmonary"] = "pulmonology",
        ["respiratory"] = "pulmonology",
        ["chest"] = "pulmonology",

        ["internal medicine"] = "internal_medicine",
        ["internal_medicine"] = "internal_medicine",
        ["im"] = "internal_medicine",
        ["general medicine"] = "internal_medicine",
        ["general practice"] = "internal_medicine",
        ["gp"] = "internal_medicine",
        ["family medicine"] = "internal_medicine",

        ["dermatology"] = "dermatology",
        ["derm"] = "dermatology",

        ["obgyn"] = "obgyn",
        ["ob/gyn"] = "obgyn",
        ["obstetrics"] = "obgyn",
        ["gynecology"] = "obgyn",
        ["gynaecology"] = "obgyn",
        ["obstetrics and gynecology"] = "obgyn",
        ["obstetrics & gynecology"] = "obgyn",

        ["psychiatry"] = "psychiatry",
        ["psych"] = "psychiatry",
        ["mental health"] = "psychiatry",

        ["ent"] = "ent",
        ["ent (ear, nose, throat)"] = "ent",
        ["otolaryngology"] = "ent",
        ["ear nose throat"] = "ent",
        ["ear, nose & throat"] = "ent",
    };

    /// <summary>
    /// The template registered under <paramref name="key"/>, or null when there is none.
    ///
    /// Exact match only: <c>SPECIALTY_TEMPLATES[req.params.key]</c> neither lowercases, trims nor
    /// alias-resolves, so only the 11 literal keys resolve — <c>eye</c> is a valid specialty alias
    /// and still not a valid key.
    /// </summary>
    public static SpecialtyTemplateDefinition? TryGetTemplate(string? key)
        => key is not null && ByKey.TryGetValue(key, out var template) ? template : null;

    /// <summary>
    /// Resolves a physician's free-text specialty to a template, or null when it matches nothing —
    /// which is the designed outcome for 22 of the 35 seeded specialties (Urology, Nephrology,
    /// General Surgery...), not an error. The caller then emits <c>"template": null</c> and the
    /// physician picks from <c>allTemplates</c> by hand.
    ///
    /// Matching is an exact alias-table lookup after <c>toLowerCase().trim()</c>: no prefix,
    /// substring or fuzzy matching. Adding any would start returning templates for those 22
    /// ("Cardiac Surgery" contains "cardiac", "Neurosurgery" contains "neuro") and change what the
    /// client renders.
    /// </summary>
    public static SpecialtyTemplateDefinition? GetTemplateForSpecialty(string? specialty)
    {
        // JS `if (!specialty) return null` — only the empty string is falsy among strings, so
        // "   " is NOT rejected here; it normalises to "" below and misses the table instead.
        if (string.IsNullOrEmpty(specialty)) return null;

        // ToLowerInvariant, never ToLower: under tr-TR the alias "im" lowercases to "ım" and
        // every Internal Medicine / GP / Family Medicine physician silently loses their template.
        var normalized = specialty.ToLowerInvariant().Trim();

        return Aliases.TryGetValue(normalized, out var key) ? TryGetTemplate(key) : null;
    }

    /// <summary>
    /// Formats stored exam values as the plain-text block the SOAP prompt carries under
    /// "Structured Specialty Exam Data:" (visits.js:787). Returns the empty string whenever there
    /// is nothing to say, and the caller then omits the whole block.
    ///
    /// Lines are joined with a literal LF, never <c>Environment.NewLine</c>: on Windows the latter
    /// would put CRLF into the prompt and the model's output drifts from the Node backend's.
    /// </summary>
    /// <param name="specialtyData">
    /// The parsed <c>specialty_data</c> object. Anything that is not a JSON object formats as the
    /// empty string, matching the JS <c>typeof !== 'object'</c> guard.
    /// </param>
    /// <param name="templateKey">Drives the iteration; an unknown key formats as empty.</param>
    public static string FormatSpecialtyDataForPrompt(JsonNode? specialtyData, string? templateKey)
    {
        // A JSON array passes the JS typeof test but can never match a field key, so it lands on
        // the same empty string this early return gives it.
        if (specialtyData is not JsonObject data) return string.Empty;

        var template = TryGetTemplate(templateKey);
        if (template is null) return string.Empty;

        List<string> lines = [$"--- {template.Label} ---"];

        foreach (var section in template.Sections)
        {
            List<string> sectionLines = [];

            foreach (var field in section.Fields)
            {
                // Iteration is template-driven, so stale keys from a previously selected template
                // and the __templateKey sentinel are ignored without being looked for.
                if (!data.TryGetPropertyValue(field.Key, out var value)) continue;

                // JS skips undefined, null and the empty STRING only. 0 and false print, and so
                // does an empty array — the test is `value !== ''` against the raw value.
                if (value is null) continue;
                if (value.GetValueKind() is JsonValueKind.String && value.GetValue<string>().Length == 0) continue;

                var unit = string.IsNullOrEmpty(field.Unit) ? string.Empty : $" {field.Unit}";
                sectionLines.Add($"  {field.Label}: {ToPromptText(value)}{unit}");
            }

            // A section with no values emits nothing at all, not even its header.
            if (sectionLines.Count == 0) continue;

            lines.Add($"[{section.Title}]");
            lines.AddRange(sectionLines);
        }

        // `lines.length > 1` — when no section matched, the lone "--- label ---" header is thrown
        // away rather than sent on its own.
        return lines.Count > 1 ? string.Join("\n", lines) : string.Empty;
    }

    /// <summary>
    /// Overload for the raw <c>visits.specialty_data</c> column, which this backend stores as JSON
    /// text where Prisma handed Node an already-parsed value. Unparseable text formats as the
    /// empty string, which is also what the Node caller's try/catch produces (visits.js:751-755).
    /// </summary>
    public static string FormatSpecialtyDataForPrompt(string? specialtyDataJson, string? templateKey)
    {
        if (string.IsNullOrWhiteSpace(specialtyDataJson)) return string.Empty;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(specialtyDataJson);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return FormatSpecialtyDataForPrompt(parsed, templateKey);
    }

    /// <summary>
    /// The flat, JSON-shaped field map the extraction prompt lists under "Fields:"
    /// (visits.js:818-835), telling the model which keys it may return. Sections do not appear.
    /// Empty string for an unknown key; never empty for any of the 11 real ones.
    /// </summary>
    public static string GetFieldMapForPrompt(string? templateKey)
    {
        var template = TryGetTemplate(templateKey);
        if (template is null) return string.Empty;

        List<string> parts = [];

        foreach (var section in template.Sections)
        {
            foreach (var field in section.Fields)
            {
                var unit = string.IsNullOrEmpty(field.Unit) ? string.Empty : $" [{field.Unit}]";

                // Only `select` gets the options suffix: `date` and `number` are announced with no
                // marker beyond the unit, so the model is told "lmp": "LMP" and must infer a date.
                // All 23 select fields carry options — JS would throw on one that did not, and its
                // caller catches that and skips extraction entirely (visits.js:873).
                var options = field.Type == "select"
                    ? $" (options: {string.Join(", ", field.Options ?? [])})"
                    : string.Empty;

                parts.Add($"  \"{field.Key}\": \"{field.Label}{unit}{options}\"");
            }
        }

        return $"{{\n{string.Join(",\n", parts)}\n}}";
    }

    /// <summary>
    /// JS string coercion of a stored value. Every value the client writes is a string — even the
    /// number and date inputs bind <c>e.target.value</c> — but the column has no shape validation,
    /// so the other kinds are coerced the way <c>`${value}`</c> would.
    /// </summary>
    private static string ToPromptText(JsonNode value)
    {
        if (value is JsonObject) return "[object Object]";

        // Array.prototype.toString: elements comma-joined, null rendered as empty, nested arrays
        // flattened by the same rule.
        if (value is JsonArray array)
            return string.Join(",", array.Select(item => item is null ? string.Empty : ToPromptText(item)));

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            // Lowercase, as JS prints them. .NET's "True"/"False" would reach the model instead.
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            // Numbers keep the digits exactly as stored, which spares us a culture-dependent
            // decimal separator; JSON has no other scalar left.
            _ => value.ToJsonString()
        };
    }

    // ── The data ─────────────────────────────────────────────────────────────
    //
    // Icons are written as escapes rather than pasted emoji: the ophthalmology eye is TWO code
    // points and an editor that normalises or strips the variation selector would change both the
    // payload and the rendered glyph. Every other non-ASCII character in this file (five em-dashes
    // in the mMRC options, one degree sign) is escaped for the same reason.
    private const string IconEye = "\U0001F441\uFE0F";    // U+1F441 EYE + U+FE0F VARIATION SELECTOR-16
    private const string IconHeart = "\U0001FAC0";        // anatomical heart
    private const string IconBrain = "\U0001F9E0";        // brain - neurology AND psychiatry, deliberately
    private const string IconBone = "\U0001F9B4";         // bone
    private const string IconBaby = "\U0001F476";         // baby
    private const string IconLungs = "\U0001FAC1";        // lungs
    private const string IconStethoscope = "\U0001FA7A";  // stethoscope
    private const string IconDna = "\U0001F9EC";          // DNA
    private const string IconPregnant = "\U0001F930";     // pregnant woman
    private const string IconHospital = "\U0001F3E5";     // hospital

    private static SpecialtyTemplateField TextField(string key, string label, string placeholder)
        => new(key, label, "text", null, null, placeholder);

    private static SpecialtyTemplateField AreaField(string key, string label, string placeholder)
        => new(key, label, "textarea", null, null, placeholder);

    private static SpecialtyTemplateField NumberField(string key, string label, string placeholder, string? unit = null)
        => new(key, label, "number", unit, null, placeholder);

    private static SpecialtyTemplateField SelectField(string key, string label, IReadOnlyList<string> options)
        => new(key, label, "select", null, options, null);

    private static SpecialtyTemplateField DateField(string key, string label)
        => new(key, label, "date", null, null, null);

    /// <summary>A section with no <c>paired</c> key at all — 28 of the 34.</summary>
    private static SpecialtyTemplateSection Section(string title, IReadOnlyList<SpecialtyTemplateField> fields)
        => new(title, null, fields);

    /// <summary>A section carrying <c>paired: true</c>, which the client lays out in two columns.</summary>
    private static SpecialtyTemplateSection PairedSection(string title, IReadOnlyList<SpecialtyTemplateField> fields)
        => new(title, true, fields);

    private static SpecialtyTemplateDefinition Template(
        string key, string label, string icon, IReadOnlyList<SpecialtyTemplateSection> sections)
        => new(key, label, icon, sections);

    /// <summary>
    /// specialtyTemplates.js L12-393, field for field: 11 templates, 34 sections, 117 fields.
    /// The helpers above encode which keys each field type carries, which is the part that has to
    /// be right — a stray <c>placeholder</c> on a select or a materialised <c>paired: false</c>
    /// changes the payload without failing any smoke test.
    /// </summary>
    private static IReadOnlyList<SpecialtyTemplateDefinition> Build() =>
    [
        Template("ophthalmology", "Ophthalmology Exam", IconEye,
        [
            PairedSection("Visual Acuity",
            [
                TextField("va_uncorrected_od", "VA Uncorrected (OD)", "6/6"),
                TextField("va_uncorrected_os", "VA Uncorrected (OS)", "6/6"),
                TextField("va_corrected_od", "BCVA (OD)", "6/6"),
                TextField("va_corrected_os", "BCVA (OS)", "6/6")
            ]),
            PairedSection("Intraocular Pressure",
            [
                NumberField("iop_od", "IOP (OD)", "14", unit: "mmHg"),
                NumberField("iop_os", "IOP (OS)", "14", unit: "mmHg")
            ]),
            PairedSection("Pupil & Refraction",
            [
                TextField("pupil_od", "Pupil (OD)", "RAPD -, 3mm, reactive"),
                TextField("pupil_os", "Pupil (OS)", "RAPD -, 3mm, reactive"),
                TextField("refraction_od", "Refraction (OD)", "-2.00/-0.50x180"),
                TextField("refraction_os", "Refraction (OS)", "-2.00/-0.50x180")
            ]),
            PairedSection("Anterior Segment (Slit Lamp)",
            [
                AreaField("anterior_od", "Anterior Segment (OD)", "Conjunctiva, cornea, AC, iris, lens"),
                AreaField("anterior_os", "Anterior Segment (OS)", "Conjunctiva, cornea, AC, iris, lens")
            ]),
            PairedSection("Fundus Examination",
            [
                AreaField("fundus_od", "Fundus (OD)", "CDR, disc, macula, vessels, periphery"),
                AreaField("fundus_os", "Fundus (OS)", "CDR, disc, macula, vessels, periphery")
            ])
        ]),

        Template("cardiology", "Cardiology Exam", IconHeart,
        [
            Section("Cardiac Symptoms",
            [
                SelectField("chest_pain", "Chest Pain",
                    ["None", "Typical angina", "Atypical angina", "Nonanginal", "Unspecified"]),
                SelectField("nyha_class", "NYHA Class", ["I", "II", "III", "IV"]),
                SelectField("palpitations", "Palpitations", ["None", "Intermittent", "Persistent"]),
                SelectField("syncope", "Syncope/Pre-syncope", ["None", "Pre-syncope", "Syncope"])
            ]),
            Section("Cardiac Examination",
            [
                TextField("jvp", "JVP", "Normal / Elevated (cm)"),
                TextField("heart_sounds", "Heart Sounds", "S1 S2 normal, no murmurs"),
                SelectField("peripheral_edema", "Peripheral Edema", ["None", "Trace", "1+", "2+", "3+", "4+"]),
                SelectField("carotid_bruit", "Carotid Bruit", ["Absent", "Present (R)", "Present (L)", "Bilateral"])
            ]),
            Section("Investigations",
            [
                AreaField("ecg_findings", "ECG Findings", "Rhythm, rate, axis, intervals, ST changes"),
                NumberField("echo_ef", "Echo EF", "55", unit: "%"),
                AreaField("echo_notes", "Echo Notes", "Valvular, wall motion, diastolic function")
            ])
        ]),

        Template("neurology", "Neurological Exam", IconBrain,
        [
            Section("Mental Status",
            [
                NumberField("gcs", "GCS", "15", unit: "/15"),
                SelectField("orientation", "Orientation",
                    ["Fully oriented (x3)", "Oriented to person only", "Oriented to person & place", "Disoriented"]),
                TextField("speech", "Speech", "Fluent, no dysarthria")
            ]),
            Section("Cranial Nerves & Motor",
            [
                TextField("cranial_nerves", "Cranial Nerves", "II-XII intact"),
                TextField("motor_ue", "Motor Power (UE)", "5/5 bilateral"),
                TextField("motor_le", "Motor Power (LE)", "5/5 bilateral"),
                TextField("muscle_tone", "Muscle Tone", "Normal / Increased / Decreased")
            ]),
            Section("Sensory, Reflexes & Coordination",
            [
                TextField("sensory", "Sensory", "Intact to light touch, pinprick"),
                TextField("reflexes", "Deep Tendon Reflexes", "2+ symmetric"),
                TextField("plantars", "Plantars", "Downgoing bilateral"),
                TextField("coordination", "Coordination", "Finger-nose, heel-shin intact"),
                SelectField("gait", "Gait",
                    ["Normal", "Ataxic", "Spastic", "Shuffling", "Waddling", "Unable to assess"])
            ])
        ]),

        Template("orthopedics", "Orthopedic Exam", IconBone,
        [
            Section("Affected Area",
            [
                TextField("affected_region", "Joint / Region", "e.g. Right knee, L4-L5"),
                TextField("mechanism", "Mechanism of Injury", "Trauma, overuse, spontaneous")
            ]),
            Section("Examination",
            [
                TextField("rom_active", "ROM (Active)", "Flexion 0-120\u00B0, full extension"),
                TextField("rom_passive", "ROM (Passive)", "Full / Limited"),
                TextField("muscle_strength", "Muscle Strength", "5/5"),
                SelectField("swelling", "Swelling", ["None", "Mild", "Moderate", "Severe"]),
                TextField("tenderness", "Tenderness", "Location and grade"),
                AreaField("special_tests", "Special Tests", "McMurray, Lachman, Thomas, etc."),
                TextField("neurovascular", "Neurovascular Status", "Distal pulses present, sensation intact")
            ])
        ]),

        Template("pediatrics", "Pediatric Exam", IconBaby,
        [
            Section("Growth",
            [
                TextField("weight_percentile", "Weight Percentile", "50th"),
                TextField("height_percentile", "Height Percentile", "50th"),
                NumberField("head_circumference", "Head Circumference", "35", unit: "cm"),
                TextField("bmi_percentile", "BMI Percentile", "50th")
            ]),
            Section("Development & Nutrition",
            [
                AreaField("milestones", "Developmental Milestones", "Age-appropriate? Gross motor, fine motor, language, social"),
                TextField("feeding", "Feeding", "Breastfed / Formula / Solid foods"),
                SelectField("vaccination_status", "Vaccination Status",
                    ["Up to date", "Behind schedule", "Not vaccinated", "Unknown"])
            ]),
            Section("Pediatric Specifics",
            [
                SelectField("fontanelle", "Fontanelle",
                    ["Open & flat", "Open & bulging", "Open & sunken", "Closed", "N/A"]),
                SelectField("hydration", "Hydration Status",
                    ["Well hydrated", "Mild dehydration", "Moderate dehydration", "Severe dehydration"]),
                SelectField("activity_level", "Activity Level",
                    ["Active & alert", "Irritable", "Lethargic", "Unresponsive"])
            ])
        ]),

        Template("pulmonology", "Pulmonology Exam", IconLungs,
        [
            Section("Respiratory Symptoms",
            [
                SelectField("dyspnea_mmrc", "Dyspnea (mMRC)",
                    ["0 \u2014 No breathlessness", "1 \u2014 Walking on hills", "2 \u2014 Slower than peers",
                     "3 \u2014 Stops after 100m", "4 \u2014 Too breathless to leave house"]),
                SelectField("cough", "Cough", ["None", "Dry", "Productive"]),
                TextField("sputum", "Sputum", "Color, amount, blood-tinged"),
                SelectField("wheezing", "Wheezing", ["None", "Expiratory", "Inspiratory", "Both"])
            ]),
            Section("Examination",
            [
                AreaField("breath_sounds", "Breath Sounds", "Clear, wheeze, crackles, rhonchi, diminished"),
                TextField("chest_expansion", "Chest Expansion", "Symmetric / Reduced (side)"),
                TextField("percussion", "Percussion", "Resonant / Dull / Hyperresonant")
            ]),
            Section("Spirometry / PFT",
            [
                NumberField("fev1", "FEV1", "80", unit: "% predicted"),
                NumberField("fvc", "FVC", "85", unit: "% predicted"),
                NumberField("fev1_fvc", "FEV1/FVC", "75", unit: "%"),
                NumberField("peak_flow", "Peak Flow", "400", unit: "L/min")
            ])
        ]),

        Template("internal_medicine", "Systems Review", IconStethoscope,
        [
            Section("General & HEENT",
            [
                TextField("general_appearance", "General Appearance", "Well-appearing, NAD"),
                AreaField("heent", "HEENT", "Head: NC/AT. Eyes: PERRLA. Ears: TMs clear. Throat: no erythema"),
                TextField("lymph_nodes", "Lymph Nodes", "No lymphadenopathy")
            ]),
            Section("Chest & Cardiovascular",
            [
                AreaField("cardiovascular", "Cardiovascular", "RRR, no murmurs, no JVD"),
                AreaField("respiratory", "Respiratory", "CTA bilaterally, no wheezes/crackles")
            ]),
            Section("Abdomen & Extremities",
            [
                AreaField("abdomen", "Abdomen", "Soft, non-tender, no organomegaly, BS+"),
                TextField("extremities", "Extremities", "No edema, pulses 2+ symmetric"),
                TextField("skin", "Skin", "No rash, no lesions"),
                TextField("neurological", "Neurological", "A&O x3, CN II-XII intact, normal gait")
            ])
        ]),

        Template("dermatology", "Dermatology Exam", IconDna,
        [
            Section("Lesion Morphology",
            [
                SelectField("lesion_type", "Lesion Type",
                    ["Macule", "Papule", "Plaque", "Nodule", "Vesicle", "Bulla", "Pustule", "Patch", "Wheal",
                     "Cyst", "Ulcer", "Erosion", "Other"]),
                TextField("distribution", "Distribution", "Face, trunk, bilateral upper extremities"),
                TextField("color", "Color", "Erythematous, hyperpigmented, violaceous"),
                TextField("size", "Size", "2 x 3 cm")
            ]),
            Section("Characteristics",
            [
                SelectField("border", "Border", ["Well-defined", "Irregular", "Diffuse", "Pedunculated"]),
                TextField("surface", "Surface", "Scaly, crusted, smooth, verrucous"),
                TextField("texture", "Texture", "Firm, soft, fluctuant"),
                TextField("associated_symptoms", "Associated Symptoms", "Pruritus, pain, burning, none")
            ]),
            Section("Additional Findings",
            [
                TextField("nails", "Nails", "Pitting, onycholysis, dystrophy"),
                TextField("hair", "Hair / Scalp", "Alopecia, scaling"),
                AreaField("dermoscopy", "Dermoscopy", "Dermoscopic findings if performed")
            ])
        ]),

        Template("obgyn", "OB/GYN Exam", IconPregnant,
        [
            Section("Obstetric History",
            [
                DateField("lmp", "LMP"),
                NumberField("gravida", "Gravida", "1"),
                NumberField("para", "Para", "0"),
                TextField("gestational_age", "Gestational Age", "32+4 weeks")
            ]),
            Section("Obstetric Examination",
            [
                NumberField("fundal_height", "Fundal Height", "32", unit: "cm"),
                NumberField("fhr", "Fetal Heart Rate", "140", unit: "bpm"),
                SelectField("presentation", "Presentation",
                    ["Cephalic", "Breech", "Transverse", "Oblique", "Not assessed"]),
                TextField("cervical_exam", "Cervical Exam", "Dilation, effacement, station")
            ]),
            Section("Gynecologic Notes",
            [
                AreaField("us_notes", "Ultrasound Notes", "Biometry, AFI, placenta, anomalies"),
                AreaField("gyn_exam", "Gynecologic Exam", "Speculum, bimanual, cervix, adnexa")
            ])
        ]),

        Template("psychiatry", "Mental Status Exam", IconBrain,
        [
            Section("Appearance & Behavior",
            [
                TextField("appearance", "Appearance", "Well-groomed, appropriate attire"),
                TextField("behavior", "Behavior", "Cooperative, good eye contact"),
                SelectField("psychomotor", "Psychomotor", ["Normal", "Retardation", "Agitation", "Catatonia"]),
                TextField("mse_speech", "Speech", "Normal rate/tone/volume")
            ]),
            Section("Mood & Thought",
            [
                TextField("mood", "Mood", "Patient-reported (e.g. \"depressed\", \"anxious\")"),
                TextField("affect", "Affect", "Congruent, flat, labile, restricted"),
                SelectField("thought_process", "Thought Process",
                    ["Logical & goal-directed", "Circumstantial", "Tangential", "Flight of ideas",
                     "Loose associations", "Thought blocking"]),
                AreaField("thought_content", "Thought Content", "SI: denied. HI: denied. Delusions: none. Obsessions: none.")
            ]),
            Section("Perception & Cognition",
            [
                TextField("perception", "Perception", "No hallucinations (auditory/visual)"),
                TextField("cognition", "Cognition", "Oriented x3, attention intact, memory intact"),
                SelectField("insight", "Insight", ["Good", "Fair", "Poor", "Absent"]),
                SelectField("judgment", "Judgment", ["Good", "Fair", "Poor", "Impaired"])
            ])
        ]),

        Template("ent", "ENT Exam", IconHospital,
        [
            PairedSection("Ear",
            [
                AreaField("ear_r", "Ear (Right)", "Canal clear, TM intact, no effusion"),
                AreaField("ear_l", "Ear (Left)", "Canal clear, TM intact, no effusion")
            ]),
            Section("Hearing & Nose",
            [
                TextField("hearing", "Hearing", "Weber midline, Rinne AC>BC bilateral"),
                AreaField("nose", "Nose", "Septum midline, turbinates normal, no discharge")
            ]),
            Section("Throat & Neck",
            [
                AreaField("throat", "Throat / Oropharynx", "Tonsils, pharynx, uvula midline, no exudate"),
                TextField("neck", "Neck", "No lymphadenopathy, thyroid normal"),
                TextField("voice", "Voice", "Normal / Hoarse"),
                AreaField("laryngoscopy", "Laryngoscopy", "Vocal cords mobile, no lesions")
            ])
        ])
    ];
}
