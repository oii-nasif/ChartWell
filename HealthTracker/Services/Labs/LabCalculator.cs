using HealthTracker.Constants;

namespace HealthTracker.Services.Labs;

/// <summary>A value derived from others in the same batch, in the target test's primary unit.</summary>
public sealed record DerivedLabValue(string TestCode, double Value, string Formula);

/// <summary>
/// Standard derived lab values. Each formula only runs when its inputs are present and the derived
/// test was not itself entered, so a measured value is never overwritten by an estimate.
/// MAUI-free (linked into the test project).
/// </summary>
public static class LabCalculator
{
    /// <summary>Friedewald is not valid at or above this triglyceride level (mg/dL).</summary>
    public const double FriedewaldTriglycerideLimit = 400;

    /// <param name="primaryValues">Entered numeric values by test code, already in each test's primary unit.</param>
    /// <param name="sex">"Male"/"Female"; needed for eGFR.</param>
    /// <param name="ageYears">Age on the collection date; needed for eGFR (adults only).</param>
    public static List<DerivedLabValue> Derive(IReadOnlyDictionary<string, double> primaryValues, string? sex, int? ageYears)
    {
        var derived = new List<DerivedLabValue>();
        bool Has(string code, out double v) => primaryValues.TryGetValue(code, out v);
        bool Missing(string code) => !primaryValues.ContainsKey(code);

        var hasTc = Has("chol_total", out var tc);
        var hasHdl = Has("hdl", out var hdl);
        var hasTg = Has("triglycerides", out var tg);

        if (hasTg && tg < FriedewaldTriglycerideLimit && Missing("vldl"))
            derived.Add(new("vldl", Round(tg / 5, 1), "Triglycerides ÷ 5"));

        if (hasTc && hasHdl && hasTg && tg < FriedewaldTriglycerideLimit && Missing("ldl"))
        {
            var ldl = tc - hdl - tg / 5;
            if (ldl > 0) derived.Add(new("ldl", Round(ldl, 1), "Friedewald: total − HDL − triglycerides ÷ 5"));
        }

        if (hasTc && hasHdl && hdl > 0)
        {
            if (Missing("non_hdl") && tc > hdl)
                derived.Add(new("non_hdl", Round(tc - hdl, 1), "Total cholesterol − HDL"));
            if (Missing("chol_hdl_ratio"))
                derived.Add(new("chol_hdl_ratio", Round(tc / hdl, 1), "Total cholesterol ÷ HDL"));
        }

        if (Has("creatinine", out var scr) && Missing("egfr") && EstimateGfr(scr, sex, ageYears) is { } egfr)
            derived.Add(new("egfr", egfr, "CKD-EPI 2021 (creatinine, age, sex)"));

        if (Has("bili_total", out var bt) && Has("bili_direct", out var bd) && Missing("bili_indirect") && bt >= bd)
            derived.Add(new("bili_indirect", Round(bt - bd, 2), "Total − direct bilirubin"));

        if (Has("total_protein", out var tp) && Has("albumin", out var alb) && Missing("globulin") && tp > alb)
            derived.Add(new("globulin", Round(tp - alb, 1), "Total protein − albumin"));

        return derived;
    }

    /// <summary>
    /// CKD-EPI 2021 creatinine equation (race-free), mL/min/1.73 m². Returns null without sex, for
    /// anyone under 18, or for a non-positive creatinine.
    /// </summary>
    public static double? EstimateGfr(double creatinineMgDl, string? sex, int? ageYears)
    {
        if (creatinineMgDl <= 0 || ageYears is not { } age || age < 18 || age > 120) return null;
        bool female;
        if (string.Equals(sex, "Female", StringComparison.OrdinalIgnoreCase)) female = true;
        else if (string.Equals(sex, "Male", StringComparison.OrdinalIgnoreCase)) female = false;
        else return null;

        var kappa = female ? 0.7 : 0.9;
        var alpha = female ? -0.241 : -0.302;
        var ratio = creatinineMgDl / kappa;
        var egfr = 142
                   * Math.Pow(Math.Min(ratio, 1), alpha)
                   * Math.Pow(Math.Max(ratio, 1), -1.200)
                   * Math.Pow(0.9938, age)
                   * (female ? 1.012 : 1);
        return Math.Round(egfr, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Body-mass index from kilograms and centimetres; null for non-positive input.</summary>
    public static double? Bmi(double? weightKg, double? heightCm)
    {
        if (weightKg is not { } w || heightCm is not { } h || w <= 0 || h <= 0) return null;
        var m = h / 100;
        return Round(w / (m * m), 1);
    }

    public static string BmiCategory(double bmi) => bmi switch
    {
        < 18.5 => "Underweight",
        < 25 => "Healthy weight",
        < 30 => "Overweight",
        _ => "Obese"
    };

    private static double Round(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
}
