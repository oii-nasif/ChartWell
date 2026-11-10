using System.Globalization;
using System.Text.RegularExpressions;
using HealthTracker.Constants;

namespace HealthTracker.Services.Labs;

/// <summary>A lab value read off a report (by Gemini or by the offline text scan), ready to prefill the entry form.</summary>
public sealed class LabDraft
{
    /// <summary>The catalog test, or null when the printed name is not in the catalog (becomes a custom test).</summary>
    public LabTestDefinition? Definition { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsQualitative { get; init; }
    public double? Value { get; init; }
    public string? ValueText { get; init; }
    public string Unit { get; init; } = string.Empty;
    /// <summary>Range as printed on the report, in <see cref="Unit"/>; null when not printed or not parseable.</summary>
    public double? RefLow { get; init; }
    public double? RefHigh { get; init; }
}

/// <summary>
/// Turns loosely formatted report text into <see cref="LabDraft"/>s: name → catalog test, value →
/// number or qualitative word, "12.0 - 15.5" / "&lt; 200" / "Up to 40" → range. Never invents a value:
/// anything that cannot be read is dropped. MAUI-free (linked into the test project).
/// </summary>
public static partial class LabFindingParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Builds a draft from one name/value/unit/range tuple; null when there is no usable value.</summary>
    public static LabDraft? Parse(string? name, string? value, string? unit, string? referenceRange)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        var def = LabCatalog.Match(name);
        var rawValue = value?.Trim() ?? string.Empty;

        if (def is { IsQualitative: true })
        {
            var option = MatchOption(def.Options, rawValue);
            return option is null ? null : new LabDraft { Definition = def, Name = def.Name, IsQualitative = true, ValueText = option };
        }

        if (TryParseNumber(rawValue, out var number))
        {
            var (low, high) = ParseRange(referenceRange);
            var cleanUnit = unit?.Trim() ?? string.Empty;
            if (def is not null)
            {
                // Keep the printed unit only when it is one we can convert; otherwise assume the primary unit
                // when none was printed, and drop the draft's range if the unit is foreign to the catalog.
                var canonical = def.CanonicalUnit(cleanUnit);
                if (canonical is not null) cleanUnit = canonical;
                else if (cleanUnit.Length == 0) cleanUnit = def.Unit;
            }
            return new LabDraft
            {
                Definition = def, Name = def?.Name ?? name, Value = number, Unit = cleanUnit, RefLow = low, RefHigh = high,
            };
        }

        // A word result for a test we do not know: keep it as a custom qualitative value.
        if (def is null && MatchOption(KnownWords, rawValue) is { } word)
            return new LabDraft { Name = name, IsQualitative = true, ValueText = word };

        return null;
    }

    private static readonly string[] KnownWords =
        ["Negative", "Positive", "Non-reactive", "Reactive", "Nil", "Trace", "Normal", "Not detected", "Detected", "Absent", "Present"];

    /// <summary>
    /// Maps a printed word to one of the test's options. "Non Reactive", "NEGATIVE (0.21)", "Nil / absent"
    /// all resolve; an option is only returned when exactly one fits, the longest match winning so that
    /// "Non-reactive" is never read as "Reactive".
    /// </summary>
    public static string? MatchOption(IEnumerable<string> options, string? printed)
    {
        if (string.IsNullOrWhiteSpace(printed)) return null;
        var text = LabCatalog.Normalize(printed);
        var raw = printed.Trim();

        string? best = null;
        var bestLength = 0;
        foreach (var option in options)
        {
            var key = LabCatalog.Normalize(option);
            bool hit;
            if (key.Length == 0)
            {
                // "+", "++": compare the symbols themselves.
                hit = raw.Equals(option, StringComparison.Ordinal) || raw.StartsWith(option + " ", StringComparison.Ordinal);
                if (hit && option.Length > bestLength) { best = option; bestLength = option.Length; }
                continue;
            }
            if (text.Length == 0) continue;
            hit = text == key || text.StartsWith(key + " ", StringComparison.Ordinal)
                  || text.EndsWith(" " + key, StringComparison.Ordinal) || text.Contains(" " + key + " ", StringComparison.Ordinal);
            if (hit && key.Length > bestLength) { best = option; bestLength = key.Length; }
        }
        if (best is not null) return best;

        // Synonyms labs use for the two common pairs.
        var set = options.ToList();
        string? Pick(string wanted) => set.FirstOrDefault(o => o.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (text is "neg" or "absent" or "nil" or "not detected" or "non reactive" or "nonreactive" or "nr" or "no growth")
            return Pick("Negative") ?? Pick("Non-reactive") ?? Pick("Nil") ?? Pick("Not detected") ?? Pick("No growth");
        if (text is "pos" or "present" or "detected" or "reactive")
            return Pick("Positive") ?? Pick("Reactive") ?? Pick("Detected");
        return null;
    }

    /// <summary>Reads "13.2", "1,250", "&lt; 0.5", "7.2 H" → the number. Words give false.</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = NumberAtStart().Match(text.Trim());
        if (!m.Success) return false;
        var digits = m.Groups[1].Value.Replace(",", "");
        return double.TryParse(digits, NumberStyles.Float, Inv, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>"12.0 - 15.5" → (12, 15.5); "&lt; 200" / "Up to 200" → (null, 200); "&gt; 40" → (40, null).</summary>
    public static (double? Low, double? High) ParseRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var t = text.Trim().Replace(",", "");

        var between = RangeBetween().Match(t);
        if (between.Success
            && double.TryParse(between.Groups[1].Value, NumberStyles.Float, Inv, out var lo)
            && double.TryParse(between.Groups[2].Value, NumberStyles.Float, Inv, out var hi) && lo <= hi)
            return (lo, hi);

        var upper = RangeUpper().Match(t);
        if (upper.Success && double.TryParse(upper.Groups[1].Value, NumberStyles.Float, Inv, out var h)) return (null, h);

        var lower = RangeLower().Match(t);
        if (lower.Success && double.TryParse(lower.Groups[1].Value, NumberStyles.Float, Inv, out var l)) return (l, null);

        return (null, null);
    }

    /// <summary>
    /// Offline fallback: scans OCR text line by line for "Test name  value  unit  range" rows of tests the
    /// catalog knows. Only catalog tests are returned (free OCR text is too noisy to trust unknown names),
    /// and each test at most once (first occurrence).
    /// </summary>
    public static List<LabDraft> ScanText(string? ocrText)
    {
        var drafts = new List<LabDraft>();
        if (string.IsNullOrWhiteSpace(ocrText)) return drafts;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in ocrText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 3 || line.Length > 160) continue;

            // Split "name" from the rest at the first number, or at a colon / run of spaces for word results.
            // A name may itself contain a number ("Glucose 2 hours after meal"), so try each candidate.
            LabDraft? draft = null;
            foreach (Match numberAt in FirstStandaloneNumber().Matches(line))
            {
                if (numberAt.Index <= 1) continue;
                var name = line[..numberAt.Index].Trim(' ', ':', '-', '.', '=', '|', '\t');
                var def = LabCatalog.Match(name);
                if (def is not { IsQualitative: false }) continue;

                var rest = line[(numberAt.Index + numberAt.Length)..].Trim();
                var unit = def.UnitNames.FirstOrDefault(u => u.Length > 0 && StartsWithUnit(rest, u)) ?? string.Empty;
                var afterUnit = unit.Length > 0 ? SkipUnit(rest, unit) : rest;
                draft = Parse(name, numberAt.Groups[1].Value, unit, afterUnit);
                break;
            }

            // Word results ("HBsAg : Negative", "Anti-HCV   Positive"): try every split of the line into
            // a leading name and a trailing word, longest name first.
            if (draft is null)
            {
                var tokens = line.Split([' ', '\t', ':', '='], StringSplitOptions.RemoveEmptyEntries);
                for (var i = Math.Min(tokens.Length - 1, 8); i >= 1 && draft is null; i--)
                {
                    var name = string.Join(' ', tokens[..i]).Trim('-', '.', '|');
                    var def = LabCatalog.Match(name);
                    if (def is { IsQualitative: true })
                        draft = Parse(name, string.Join(' ', tokens[i..]), null, null);
                }
            }

            if (draft?.Definition is { } d && seen.Add(d.Code)) drafts.Add(draft);
        }
        return drafts;
    }

    private static bool StartsWithUnit(string rest, string unit)
    {
        var token = rest.Split(' ', '\t').FirstOrDefault() ?? string.Empty;
        return LabCatalog.UnitEquals(token, unit);
    }

    private static string SkipUnit(string rest, string unit)
    {
        var space = rest.IndexOfAny([' ', '\t']);
        return space < 0 ? string.Empty : rest[space..].Trim();
    }

    [GeneratedRegex(@"^[<>≤≥=~\s]*(-?\d[\d,]*(?:\.\d+)?)")]
    private static partial Regex NumberAtStart();

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s*(?:-|–|—|to)\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeBetween();

    [GeneratedRegex(@"(?:<=?|≤|up\s*to|upto|below|less\s+than|under)\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeUpper();

    [GeneratedRegex(@"(?:>=?|≥|above|more\s+than|greater\s+than|over)\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeLower();

    // A number that is not glued to letters ("B12", "T3", "HbA1c" must stay part of the name).
    [GeneratedRegex(@"(?<![A-Za-z\d.])(\d[\d,]*(?:\.\d+)?)(?![A-Za-z\d])")]
    private static partial Regex FirstStandaloneNumber();
}
