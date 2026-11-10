using System.Globalization;
using HealthTracker.Constants;
using HealthTracker.Models;

namespace HealthTracker.Services.Labs;

public enum LabFlag
{
    /// <summary>Nothing to judge against (no range, no expected word, or no value).</summary>
    None,
    Normal,
    Low,
    High,
    /// <summary>A qualitative result other than the expected word.</summary>
    Abnormal
}

/// <summary>
/// Pure functions over lab values: flagging against the stored range, unit conversion and display
/// formatting. MAUI-free (linked into the test project).
/// </summary>
public static class LabEvaluator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static LabFlag Evaluate(LabResult r)
    {
        if (r.IsQualitative == 1)
        {
            if (string.IsNullOrWhiteSpace(r.ValueText) || string.IsNullOrWhiteSpace(r.ExpectedText)) return LabFlag.None;
            return LabCatalog.Normalize(r.ValueText) == LabCatalog.Normalize(r.ExpectedText) ? LabFlag.Normal : LabFlag.Abnormal;
        }
        return Evaluate(r.Value, r.RefLow, r.RefHigh);
    }

    /// <summary>Both bounds are inclusive: a value equal to a bound is in range.</summary>
    public static LabFlag Evaluate(double? value, double? low, double? high)
    {
        if (value is not { } v || (low is null && high is null)) return LabFlag.None;
        if (low is { } l && v < l) return LabFlag.Low;
        if (high is { } h && v > h) return LabFlag.High;
        return LabFlag.Normal;
    }

    public static bool NeedsAttention(LabFlag flag) => flag is LabFlag.Low or LabFlag.High or LabFlag.Abnormal;

    /// <summary>Theme token for a flag (resolved by ThemeColorConverter).</summary>
    public static string Token(LabFlag flag) => flag switch
    {
        LabFlag.Normal => "Success",
        LabFlag.Low or LabFlag.High => "Warning",
        LabFlag.Abnormal => "Danger",
        _ => "TextSecondary"
    };

    /// <summary>Short chip text. A qualitative result shows its own word, so only numeric flags get a label.</summary>
    public static string Label(LabFlag flag) => flag switch
    {
        LabFlag.Normal => "In range",
        LabFlag.Low => "Low",
        LabFlag.High => "High",
        LabFlag.Abnormal => "Check",
        _ => string.Empty
    };

    // ── Formatting ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats a number the way a lab report would: fixed decimals when given, otherwise as many as the
    /// magnitude warrants (250, 12.5, 1.25, 0.045). Trailing zeros are dropped unless decimals are fixed.
    /// </summary>
    public static string Format(double value, int? decimals = null)
    {
        if (decimals is { } d) return value.ToString("F" + Math.Clamp(d, 0, 6), Inv);
        var abs = Math.Abs(value);
        var format = abs switch
        {
            >= 100 => "0.#",
            >= 10 => "0.##",
            >= 1 => "0.##",
            >= 0.1 => "0.###",
            _ => "0.####"
        };
        return value.ToString(format, Inv);
    }

    /// <summary>Decimals to show a value of this test in <paramref name="unit"/> (fixed only in the primary unit).</summary>
    public static int? DecimalsFor(LabTestDefinition? def, string? unit)
        => def is not null && LabCatalog.UnitEquals(unit, def.Unit) ? def.Decimals : null;

    /// <summary>"12.5" for a number, the word for a qualitative result, "—" when empty.</summary>
    public static string ValueText(LabResult r)
    {
        if (r.IsQualitative == 1) return string.IsNullOrWhiteSpace(r.ValueText) ? "—" : r.ValueText!;
        return r.Value is { } v ? Format(v, DecimalsFor(LabCatalog.Find(r.TestCode), r.Unit)) : "—";
    }

    /// <summary>"12.5 g/dL" / "Negative".</summary>
    public static string ValueWithUnit(LabResult r)
    {
        var value = ValueText(r);
        return r.IsQualitative == 1 || string.IsNullOrEmpty(r.Unit) ? value : $"{value} {r.Unit}";
    }

    /// <summary>"70 – 99", "up to 200", "40 or above"; empty when there is no range.</summary>
    public static string RangeText(double? low, double? high, int? decimals = null)
    {
        if (low is { } l && high is { } h) return $"{Format(l, decimals)} – {Format(h, decimals)}";
        if (high is { } hi) return $"up to {Format(hi, decimals)}";
        if (low is { } lo) return $"{Format(lo, decimals)} or above";
        return string.Empty;
    }

    /// <summary>"Reference 70 – 99 mg/dL" or "Expected: Negative"; empty when nothing applies.</summary>
    public static string ReferenceText(LabResult r)
    {
        if (r.IsQualitative == 1)
            return string.IsNullOrWhiteSpace(r.ExpectedText) ? string.Empty : $"Expected: {r.ExpectedText}";
        var range = RangeText(r.RefLow, r.RefHigh, DecimalsFor(LabCatalog.Find(r.TestCode), r.Unit));
        if (range.Length == 0) return string.Empty;
        return string.IsNullOrEmpty(r.Unit) ? $"Reference {range}" : $"Reference {range} {r.Unit}";
    }

    // ── Units ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts between two units of the same test. Returns null when either unit is not one the
    /// catalog knows for this test (custom tests, odd units): callers then leave the value alone.
    /// </summary>
    public static double? Convert(LabTestDefinition? def, double value, string? fromUnit, string? toUnit)
    {
        if (LabCatalog.UnitEquals(fromUnit, toUnit)) return value;
        if (def is null) return null;
        var from = def.FactorToPrimary(fromUnit);
        var to = def.FactorToPrimary(toUnit);
        if (from is null || to is null || to == 0) return null;
        return value * from.Value / to.Value;
    }

    /// <summary>The catalog reference range for a sex, expressed in <paramref name="unit"/> (rounded to a readable precision).</summary>
    public static (double? Low, double? High) RangeIn(LabTestDefinition def, string? sex, string? unit)
    {
        var (low, high) = def.RangeFor(sex);
        if (LabCatalog.UnitEquals(unit, def.Unit)) return (low, high);
        return (RoundSig(low is { } l ? Convert(def, l, def.Unit, unit) : null),
                RoundSig(high is { } h ? Convert(def, h, def.Unit, unit) : null));
    }

    /// <summary>Three significant figures: enough for a converted bound without printing 5.172413.</summary>
    private static double? RoundSig(double? value)
    {
        if (value is not { } v || v == 0) return value;
        var magnitude = Math.Floor(Math.Log10(Math.Abs(v)));
        var digits = (int)Math.Clamp(2 - magnitude, 0, 6);
        return Math.Round(v, digits, MidpointRounding.AwayFromZero);
    }

    /// <summary>The result's value in the test's primary unit, or as stored when it cannot be converted.</summary>
    public static double? ValueInPrimary(LabResult r)
    {
        if (r.Value is not { } v) return null;
        var def = LabCatalog.Find(r.TestCode);
        return def is null ? v : Convert(def, v, r.Unit, def.Unit) ?? v;
    }
}
