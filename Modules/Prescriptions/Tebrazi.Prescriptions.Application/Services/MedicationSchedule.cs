using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Prescriptions.Application.Services;

/// <summary>
/// One medication's dose schedule, as <c>parseFrequency</c> resolves it.
/// </summary>
/// <param name="DosesPerDay">Clamped to 1-12 inclusive; never 0, never negative.</param>
/// <param name="IntervalHours">
/// <c>Math.round(24 / dosesPerDay)</c>. Carried into the reminder's <c>notes</c> JSON and
/// nowhere else — the dose TIMES come from <see cref="MedicationSchedule.GetDefaultDoseTimes"/>,
/// which does not use it.
/// </param>
public readonly record struct MedicationFrequency(int DosesPerDay, int IntervalHours);

/// <summary>
/// Port of <c>server/src/utils/medicationScheduleUtils.js</c> plus the reminder-row literals that
/// <c>PUT /api/prescriptions/{id}/send</c> composes around them (prescriptions.js:361-404).
///
/// <para>This sits on the PRESCRIPTIONS side of <see cref="IMedicationReminderScheduler"/> on
/// purpose. Nothing here touches <c>reminders</c>: it is pure logic over a prescription's own
/// free-text <c>frequency</c> and <c>dosage</c> strings, every output is a literal that is
/// byte-visible in the rows <c>/send</c> writes, and the DOSE COUNT it produces decides whether
/// the caller raises its <c>MEDICATION_REMINDER_SET</c> notification — a side effect Prescriptions
/// owns. Pushing it behind the port would move the contract out of reach of a comparison against
/// the Node handler and would leave the caller unable to reproduce its own notification. Same
/// reasoning, and same shape, as <c>SpecialtyTemplates</c> in Visits.</para>
///
/// <para>Pure and static — no I/O, no database, no clock except the one you pass in.</para>
/// </summary>
public static partial class MedicationSchedule
{
    /// <summary>Zero-padded 12-hour clock with AM/PM, as <c>toLocaleTimeString('en-US')</c> renders it.</summary>
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// <c>JSON.stringify</c> semantics for the <c>notes</c> payload: JavaScript escapes only
    /// quotes, backslashes and control characters, so an Arabic or accented drug name must survive
    /// unescaped. The default .NET encoder would emit <c>ا...</c> and change the stored bytes.
    /// </summary>
    private static readonly JsonSerializerOptions NotesJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // The six tests, in the ORDER parseFrequency applies them. Order is load-bearing: the
    // "twice|b.i.d" branch is tested before "four times|q.i.d", and "three times|t.i.d" before
    // both, so "two to four times" resolves to 2 and not 4.
    [GeneratedRegex(@"every\s*(\d+)\s*h", RegexOptions.IgnoreCase)]
    private static partial Regex EveryNHours();

    [GeneratedRegex(@"(\d+)\s*times?\s*(a\s*day|daily|/\s*day)", RegexOptions.IgnoreCase)]
    private static partial Regex NTimesADay();

    /// <summary>
    /// The LOOSER pattern the JS uses to pull the number back out after
    /// <see cref="NTimesADay"/> matched. It is deliberately not the same regex, and it scans from
    /// the START of the string, so "5 doses, 3 times a day" passes the test on "3 times a day" and
    /// then extracts <b>5</b>. Reproduce the two-pattern dance, not a single capture.
    /// </summary>
    [GeneratedRegex(@"(\d+)\s*times?", RegexOptions.IgnoreCase)]
    private static partial Regex NTimesLoose();

    [GeneratedRegex(@"three\s*times|thrice|t\.?i\.?d", RegexOptions.IgnoreCase)]
    private static partial Regex ThreeTimes();

    [GeneratedRegex(@"twice|two\s*times|b\.?i\.?d|2\s*x", RegexOptions.IgnoreCase)]
    private static partial Regex TwiceDaily();

    [GeneratedRegex(@"four\s*times|q\.?i\.?d|4\s*x", RegexOptions.IgnoreCase)]
    private static partial Regex FourTimes();

    /// <summary>
    /// Note <c>o\.?d</c>: it matches the bare letters "od" anywhere, so an unrelated word
    /// containing them ("food", "period") resolves to one dose a day. That over-match is in the
    /// Node source and is reproduced.
    /// </summary>
    [GeneratedRegex(@"once|one\s*time|o\.?d|1\s*x", RegexOptions.IgnoreCase)]
    private static partial Regex OnceDaily();

    /// <summary>
    /// Port of <c>parseFrequency</c>. Reads a free-text frequency ("BID", "3 times a day",
    /// "every 8 hours", "twice daily") and returns the dose count and interval.
    ///
    /// <para>Unrecognised text falls through to one dose a day rather than failing — there is no
    /// error path. <c>"every 0 hours"</c> divides by zero, which in JavaScript is
    /// <c>Infinity</c> and then clamps to 12; the arithmetic here is done in <c>double</c> for
    /// exactly that reason, and an integer divide would throw where Node returns 12.</para>
    /// </summary>
    /// <param name="frequency">
    /// The medication's raw <c>frequency</c> string. Node calls <c>.toLowerCase().trim()</c> on
    /// it unguarded, so a NON-STRING frequency throws a TypeError there and aborts the whole
    /// reminder block. A caller holding a non-string value must abandon the entire plan rather
    /// than coerce it — see docs/prescriptions-surface.md.
    /// </param>
    public static MedicationFrequency ParseFrequency(string frequency)
    {
        ArgumentNullException.ThrowIfNull(frequency);

        var freq = frequency.ToLowerInvariant().Trim();
        double dosesPerDay = 1;

        if (EveryNHours().Match(freq) is { Success: true } everyMatch)
        {
            // parseInt on a digits-only capture. Parsed as a double so an absurd hour count
            // behaves like JS (a large number, then clamped) instead of overflowing.
            var hours = ParseDigits(everyMatch.Groups[1].Value);
            dosesPerDay = JsRound(24.0 / hours);
        }
        else if (NTimesADay().IsMatch(freq))
        {
            // Tested with one pattern, extracted with another — see NTimesLoose().
            var loose = NTimesLoose().Match(freq);
            if (loose.Success) dosesPerDay = ParseDigits(loose.Groups[1].Value);
        }
        else if (ThreeTimes().IsMatch(freq)) dosesPerDay = 3;
        else if (TwiceDaily().IsMatch(freq)) dosesPerDay = 2;
        else if (FourTimes().IsMatch(freq)) dosesPerDay = 4;
        else if (OnceDaily().IsMatch(freq)) dosesPerDay = 1;

        // Math.max(1, Math.min(dosesPerDay, 12)) — clamped while still a double, because the
        // "every 0 h" path is +Infinity at this point and would not survive a cast.
        var clamped = (int)Math.Max(1, Math.Min(dosesPerDay, 12));

        return new MedicationFrequency(clamped, (int)JsRound(24.0 / clamped));
    }

    /// <summary>
    /// Port of <c>getDefaultDoseTimes</c>. Returns "HH:mm" strings for the given dose count.
    ///
    /// <para>1-4 doses use the hard-coded medically sensible schedules. <b>5 or more are spaced
    /// evenly from 08:00 with a modulo-24h wraparound, so the list is NOT in ascending order</b>
    /// — a 5-dose day is 08:00, 12:48, 17:36, 22:24, 03:12, and that last entry belongs to the
    /// following morning. The minute is rounded, so a schedule whose spacing lands on x.5 minutes
    /// can produce a literal <c>"HH:60"</c>; JavaScript's <c>setHours(h, 60)</c> rolls that into
    /// the next hour, and <see cref="NextOccurrence"/> reproduces the rollover.</para>
    /// </summary>
    /// <param name="dosesPerDay">
    /// Normally 1-12 from <see cref="ParseFrequency"/>. Zero or negative falls into the even-
    /// spacing branch and returns an empty list, matching the JS loop that never runs.
    /// </param>
    public static IReadOnlyList<string> GetDefaultDoseTimes(int dosesPerDay)
        => dosesPerDay switch
        {
            1 => ["08:00"],
            2 => ["08:00", "20:00"],
            3 => ["08:00", "14:00", "20:00"],
            4 => ["08:00", "12:00", "18:00", "22:00"],
            _ => EvenlySpacedFrom8Am(dosesPerDay)
        };

    private static List<string> EvenlySpacedFrom8Am(int dosesPerDay)
    {
        var times = new List<string>(Math.Max(0, dosesPerDay));
        var intervalHours = 24.0 / dosesPerDay;

        for (var i = 0; i < dosesPerDay; i++)
        {
            var totalMinutes = ((8 * 60) + (i * intervalHours * 60)) % (24 * 60);
            var h = (int)Math.Floor(totalMinutes / 60);
            var m = (int)JsRound(totalMinutes % 60);

            times.Add(string.Concat(
                h.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0'),
                ":",
                m.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')));
        }

        return times;
    }

    /// <summary>
    /// The first firing of a dose: today at that local wall-clock time, or tomorrow when the
    /// instant has already gone by (prescriptions.js:385-391).
    ///
    /// <para>Computed against the SERVER's local timezone, not the patient's — which is the Node
    /// behaviour and a known clinical wart, not something to fix here.</para>
    /// </summary>
    /// <param name="doseTime">
    /// An "HH:mm" string from <see cref="GetDefaultDoseTimes"/>. A minute component of 60 rolls
    /// into the following hour, exactly as <c>setHours(h, 60, 0, 0)</c> does.
    /// </param>
    /// <param name="nowLocal">
    /// The local "now" to compare against. Defaults to <see cref="DateTime.Now"/>; pass one
    /// explicitly to keep every dose of a prescription on the same reference instant, since Node
    /// re-reads the clock per dose.
    /// </param>
    /// <returns>The firing instant, converted to UTC for storage.</returns>
    public static DateTime NextOccurrence(string doseTime, DateTime? nowLocal = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(doseTime);

        var parts = doseTime.Split(':');
        var hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minute = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;

        var now = nowLocal ?? DateTime.Now;

        // AddHours/AddMinutes rather than a constructor, so minute 60 rolls the hour over.
        var dose = now.Date.AddHours(hour).AddMinutes(minute);
        if (dose < now) dose = dose.AddDays(1);

        return dose.Kind == DateTimeKind.Utc ? dose : DateTime.SpecifyKind(dose, DateTimeKind.Local).ToUniversalTime();
    }

    /// <summary>
    /// Builds the whole reminder group for one medication: the purge key plus one fully formed
    /// row per dose, with every literal the Node handler writes.
    ///
    /// <para>Call it only for a medication that has BOTH a truthy <c>drugName</c> and a truthy
    /// <c>frequency</c> — Node skips the others before reaching any of this
    /// (prescriptions.js:361).</para>
    /// </summary>
    /// <param name="drugName">
    /// The medication's raw <c>drugName</c>. Used verbatim as the group's purge key AND, with the
    /// pill emoji in front, as each row's title.
    /// </param>
    /// <param name="dosage">
    /// The medication's <c>dosage</c>, or null. Node's <c>med.dosage || ''</c> collapses null and
    /// the empty string to <c>""</c>, which then removes the description's <c>" — "</c> separator
    /// entirely and is stored as <c>""</c> in the notes JSON.
    /// </param>
    /// <param name="frequency">
    /// The medication's raw <c>frequency</c>. Parsed for the dose count and ALSO copied verbatim
    /// into the notes JSON — the notes carry the original text, not the lowercased form.
    /// </param>
    /// <param name="nowLocal">
    /// The local reference instant for <see cref="NextOccurrence"/>. Defaults to
    /// <see cref="DateTime.Now"/>.
    /// </param>
    public static MedicationReminderGroup BuildGroup(
        string drugName,
        string? dosage,
        string frequency,
        DateTime? nowLocal = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(drugName);
        ArgumentNullException.ThrowIfNull(frequency);

        var schedule = ParseFrequency(frequency);
        var doseTimes = GetDefaultDoseTimes(schedule.DosesPerDay);

        // `med.dosage || ''` — null and "" are the same thing downstream.
        var effectiveDosage = dosage ?? string.Empty;

        var rows = new List<MedicationReminderRow>(doseTimes.Count);

        for (var i = 0; i < doseTimes.Count; i++)
        {
            var remindAt = NextOccurrence(doseTimes[i], nowLocal);

            // `dosesPerDay === 1 ? '' : ` (Dose ${i+1}/${dosesPerDay})`` — note the LEADING space
            // and the 1-based numbering.
            var doseLabel = schedule.DosesPerDay == 1
                ? string.Empty
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $" (Dose {i + 1}/{schedule.DosesPerDay})");

            // toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', hour12: true })
            // against the LOCAL rendering of the instant, e.g. "08:00 AM".
            var timeStr = remindAt.ToLocalTime().ToString("hh:mm tt", EnUs);

            var description = effectiveDosage.Length == 0
                ? $"Take at {timeStr}"
                : $"{effectiveDosage} — Take at {timeStr}";

            var notes = JsonSerializer.Serialize(
                new MedicationReminderNotes(
                    drugName,
                    effectiveDosage,
                    frequency,
                    i + 1,
                    schedule.DosesPerDay,
                    schedule.IntervalHours),
                NotesJson);

            rows.Add(new MedicationReminderRow(
                Title: $"\U0001F48A {drugName}{doseLabel}",
                Description: description,
                RemindAt: remindAt,
                Notes: notes));
        }

        return new MedicationReminderGroup(drugName, rows);
    }

    /// <summary>JavaScript's <c>Math.round</c>: ties go toward positive infinity, not to even.</summary>
    private static double JsRound(double value) => Math.Floor(value + 0.5);

    /// <summary>
    /// A digits-only capture group as a double. Digits-only because every call site's regex
    /// captures <c>(\d+)</c>, and a double so a 20-digit match clamps like JS rather than
    /// overflowing.
    /// </summary>
    private static double ParseDigits(string digits)
        => double.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
}

/// <summary>
/// The <c>notes</c> payload of a medication reminder, serialized to a JSON STRING (the column is
/// text, not JSON). <b>Property order is the contract</b> — <c>JSON.stringify</c> emits the object
/// literal's insertion order and this record reproduces it exactly
/// (prescriptions.js:402).
/// </summary>
/// <param name="DrugName">The medication's raw drug name.</param>
/// <param name="Dosage">The <c>|| ''</c> dosage — never null, possibly empty.</param>
/// <param name="Frequency">The medication's raw frequency text, not the parsed form.</param>
/// <param name="DoseNumber">1-based position of this dose within the day.</param>
/// <param name="TotalDoses">The day's dose count, 1-12.</param>
/// <param name="IntervalHours"><c>Math.round(24 / totalDoses)</c>.</param>
public sealed record MedicationReminderNotes(
    string DrugName,
    string Dosage,
    string Frequency,
    int DoseNumber,
    int TotalDoses,
    int IntervalHours);
