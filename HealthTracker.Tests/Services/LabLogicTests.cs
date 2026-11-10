using HealthTracker.Constants;
using HealthTracker.Models;
using HealthTracker.Services.Labs;

namespace HealthTracker.Tests.Services;

public class LabCatalogTests
{
    [Fact]
    public void Codes_AreUnique_AndEveryTestHasAKnownPanel()
    {
        var codes = LabCatalog.Tests.Select(t => t.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var panels = LabCatalog.Panels.Select(p => p.Code).ToHashSet();
        Assert.All(LabCatalog.Tests, t => Assert.Contains(t.PanelCode, panels));
    }

    [Fact]
    public void EveryPanelExceptCustom_HasTests()
    {
        foreach (var panel in LabCatalog.Panels.Where(p => p.Code != LabCatalog.CustomPanel))
            Assert.NotEmpty(LabCatalog.TestsIn(panel.Code));
    }

    [Fact]
    public void NumericRanges_AreOrdered_ForEverySex()
    {
        foreach (var t in LabCatalog.Tests.Where(t => !t.IsQualitative))
            foreach (var sex in new[] { null, "Male", "Female" })
            {
                var (low, high) = t.RangeFor(sex);
                if (low is { } l && high is { } h) Assert.True(l < h, $"{t.Code} ({sex}) range is not ordered");
            }
    }

    [Fact]
    public void QualitativeTests_HaveOptions_AndExpectedIsOneOfThem()
    {
        foreach (var t in LabCatalog.Tests.Where(t => t.IsQualitative))
        {
            Assert.True(t.Options.Length >= 2, t.Code);
            if (t.Expected is not null) Assert.Contains(t.Expected, t.Options);
        }
    }

    [Fact]
    public void AlternativeUnits_HavePositiveFactors_AndDifferFromPrimary()
    {
        foreach (var t in LabCatalog.Tests)
            foreach (var alt in t.AltUnits)
            {
                Assert.True(alt.ToPrimary > 0, t.Code);
                Assert.False(LabCatalog.UnitEquals(alt.Name, t.Unit), t.Code);
            }
    }

    [Theory]
    [InlineData("S. Creatinine", "creatinine")]
    [InlineData("Serum Creatinine", "creatinine")]
    [InlineData("ALT (SGPT)", "alt")]
    [InlineData("SGPT", "alt")]
    [InlineData("S. ALT", "alt")]
    [InlineData("AST/SGOT", "ast")]
    [InlineData("Cholesterol - Total", "chol_total")]
    [InlineData("Serum Cholesterol (Total)", "chol_total")]
    [InlineData("LDL Cholesterol", "ldl")]
    [InlineData("HDL-C", "hdl")]
    [InlineData("Triglyceride", "triglycerides")]
    [InlineData("HBsAg", "hbsag")]
    [InlineData("Anti-HCV", "anti_hcv")]
    [InlineData("HbA1c", "hba1c")]
    [InlineData("Fasting Blood Sugar", "fbs")]
    [InlineData("Haemoglobin (Hb)", "hemoglobin")]
    [InlineData("Total WBC Count", "wbc")]
    [InlineData("Platelet Count", "platelets")]
    [InlineData("Vitamin D (25-OH)", "vitamin_d")]
    [InlineData("TSH", "tsh")]
    public void Match_ResolvesPrintedNames(string printed, string expectedCode)
        => Assert.Equal(expectedCode, LabCatalog.Match(printed)?.Code);

    [Theory]
    [InlineData("Glucose")]          // ambiguous: fasting, random or post-meal
    [InlineData("Patient name")]
    [InlineData("")]
    [InlineData(null)]
    public void Match_ReturnsNull_WhenNotConfident(string? printed)
        => Assert.Null(LabCatalog.Match(printed));

    [Theory]
    [InlineData("µmol/L", "umol/L")]
    [InlineData("mg/dL", "MG/DL")]
    [InlineData("×10³/µL", "x10^3/uL")]
    [InlineData("g/dL", "gm/dl")]
    [InlineData("U/L", "IU/L")]
    public void UnitEquals_IgnoresSpellingVariants(string a, string b)
        => Assert.True(LabCatalog.UnitEquals(a, b));

    [Fact]
    public void RangeFor_UsesSexSpecificRange_AndFallsBackToGeneral()
    {
        var hdl = LabCatalog.Find("hdl")!;
        Assert.Equal(50, hdl.RangeFor("Female").Low);
        Assert.Equal(40, hdl.RangeFor("Male").Low);
        Assert.Equal(40, hdl.RangeFor(null).Low);
    }

    [Fact]
    public void CustomCode_IsStableAcrossSpelling()
    {
        Assert.Equal(LabCatalog.CustomCode("Anti CCP"), LabCatalog.CustomCode("  anti-ccp "));
        Assert.True(LabCatalog.IsCustom(LabCatalog.CustomCode("Anti CCP")));
        Assert.False(LabCatalog.IsCustom("creatinine"));
    }

    [Fact]
    public void Search_FindsByAliasAndPanel()
    {
        Assert.Contains(LabCatalog.Search("sgpt"), t => t.Code == "alt");
        Assert.Contains(LabCatalog.Search("lipid"), t => t.Code == "ldl");
        Assert.Equal(LabCatalog.Tests.Count, LabCatalog.Search("").Count);
    }
}

public class LabEvaluatorTests
{
    private static LabResult Numeric(double value, double? low, double? high, string code = "creatinine", string unit = "mg/dL")
        => new() { TestCode = code, Value = value, RefLow = low, RefHigh = high, Unit = unit };

    [Theory]
    [InlineData(0.9, 0.7, 1.3, LabFlag.Normal)]
    [InlineData(0.7, 0.7, 1.3, LabFlag.Normal)]   // bounds are inclusive
    [InlineData(1.3, 0.7, 1.3, LabFlag.Normal)]
    [InlineData(0.69, 0.7, 1.3, LabFlag.Low)]
    [InlineData(1.31, 0.7, 1.3, LabFlag.High)]
    public void Evaluate_Numeric_AgainstBothBounds(double value, double low, double high, LabFlag expected)
        => Assert.Equal(expected, LabEvaluator.Evaluate(Numeric(value, low, high)));

    [Fact]
    public void Evaluate_Numeric_OneSidedAndMissingRanges()
    {
        Assert.Equal(LabFlag.High, LabEvaluator.Evaluate(Numeric(240, null, 200)));
        Assert.Equal(LabFlag.Normal, LabEvaluator.Evaluate(Numeric(180, null, 200)));
        Assert.Equal(LabFlag.Low, LabEvaluator.Evaluate(Numeric(35, 40, null)));
        Assert.Equal(LabFlag.None, LabEvaluator.Evaluate(Numeric(35, null, null)));
        Assert.Equal(LabFlag.None, LabEvaluator.Evaluate(new LabResult { RefLow = 1, RefHigh = 2 }));
    }

    [Theory]
    [InlineData("Negative", "Negative", LabFlag.Normal)]
    [InlineData("Positive", "Negative", LabFlag.Abnormal)]
    [InlineData("non reactive", "Non-reactive", LabFlag.Normal)]
    [InlineData("Reactive", "Non-reactive", LabFlag.Abnormal)]
    [InlineData("Trace", "Nil", LabFlag.Abnormal)]
    [InlineData("O+", null, LabFlag.None)]          // blood group: nothing is "expected"
    public void Evaluate_Qualitative(string value, string? expected, LabFlag flag)
        => Assert.Equal(flag, LabEvaluator.Evaluate(new LabResult { IsQualitative = 1, ValueText = value, ExpectedText = expected }));

    [Theory]
    [InlineData(250.0, "250")]
    [InlineData(12.5, "12.5")]
    [InlineData(1.25, "1.25")]
    [InlineData(0.045, "0.045")]
    [InlineData(5.0, "5")]
    public void Format_PicksDecimalsFromMagnitude(double value, string expected)
        => Assert.Equal(expected, LabEvaluator.Format(value));

    [Fact]
    public void Format_HonoursFixedDecimals()
    {
        Assert.Equal("1.010", LabEvaluator.Format(1.01, 3));
        Assert.Equal("90", LabEvaluator.Format(89.6, 0));
    }

    [Fact]
    public void RangeText_CoversAllShapes()
    {
        Assert.Equal("70 – 99", LabEvaluator.RangeText(70, 99));
        Assert.Equal("up to 200", LabEvaluator.RangeText(null, 200));
        Assert.Equal("40 or above", LabEvaluator.RangeText(40, null));
        Assert.Equal(string.Empty, LabEvaluator.RangeText(null, null));
    }

    [Fact]
    public void Convert_RoundTripsBetweenUnits()
    {
        var glucose = LabCatalog.Find("fbs")!;
        var mgdl = LabEvaluator.Convert(glucose, 5.5, "mmol/L", "mg/dL")!.Value;
        Assert.Equal(99.1, mgdl, 1);
        Assert.Equal(5.5, LabEvaluator.Convert(glucose, mgdl, "mg/dL", "mmol/L")!.Value, 6);

        var creatinine = LabCatalog.Find("creatinine")!;
        Assert.Equal(1.0, LabEvaluator.Convert(creatinine, 88.4, "umol/L", "mg/dL")!.Value, 6);
    }

    [Fact]
    public void Convert_ReturnsNull_ForUnknownUnitOrTest()
    {
        Assert.Null(LabEvaluator.Convert(LabCatalog.Find("fbs"), 5, "furlongs", "mg/dL"));
        Assert.Null(LabEvaluator.Convert(null, 5, "a", "b"));
        Assert.Equal(5, LabEvaluator.Convert(null, 5, "mg/dL", "MG/DL"));
    }

    [Fact]
    public void RangeIn_ConvertsBoundsToTheEntryUnit()
    {
        var glucose = LabCatalog.Find("fbs")!;
        var (low, high) = LabEvaluator.RangeIn(glucose, null, "mmol/L");
        Assert.Equal(3.89, low!.Value, 2);
        Assert.Equal(5.5, high!.Value, 1);

        var (pLow, pHigh) = LabEvaluator.RangeIn(glucose, null, "mg/dL");
        Assert.Equal(70, pLow);
        Assert.Equal(99, pHigh);
    }

    [Fact]
    public void ValueWithUnit_And_ReferenceText()
    {
        var r = Numeric(1.1, 0.7, 1.3);
        Assert.Equal("1.10 mg/dL", LabEvaluator.ValueWithUnit(r));
        Assert.Equal("Reference 0.70 – 1.30 mg/dL", LabEvaluator.ReferenceText(r));

        var q = new LabResult { TestCode = "hbsag", IsQualitative = 1, ValueText = "Negative", ExpectedText = "Negative" };
        Assert.Equal("Negative", LabEvaluator.ValueWithUnit(q));
        Assert.Equal("Expected: Negative", LabEvaluator.ReferenceText(q));
    }
}

public class LabCalculatorTests
{
    private static Dictionary<string, double> Values(params (string Code, double Value)[] items)
        => items.ToDictionary(i => i.Code, i => i.Value);

    [Fact]
    public void Derive_LipidPanel_Friedewald()
    {
        var d = LabCalculator.Derive(Values(("chol_total", 200), ("hdl", 50), ("triglycerides", 150)), null, null)
            .ToDictionary(x => x.TestCode, x => x.Value);
        Assert.Equal(120, d["ldl"]);
        Assert.Equal(30, d["vldl"]);
        Assert.Equal(150, d["non_hdl"]);
        Assert.Equal(4, d["chol_hdl_ratio"]);
    }

    [Fact]
    public void Derive_SkipsFriedewald_WhenTriglyceridesTooHigh()
    {
        var d = LabCalculator.Derive(Values(("chol_total", 260), ("hdl", 40), ("triglycerides", 450)), null, null);
        Assert.DoesNotContain(d, x => x.TestCode is "ldl" or "vldl");
        Assert.Contains(d, x => x.TestCode == "non_hdl");
    }

    [Fact]
    public void Derive_NeverOverwritesAnEnteredValue()
    {
        var d = LabCalculator.Derive(Values(("chol_total", 200), ("hdl", 50), ("triglycerides", 150), ("ldl", 110)), null, null);
        Assert.DoesNotContain(d, x => x.TestCode == "ldl");
    }

    [Theory]
    // Reference values of the CKD-EPI 2021 creatinine equation.
    [InlineData(1.0, "Male", 50, 92)]
    [InlineData(1.0, "Female", 50, 69)]
    [InlineData(0.6, "Female", 30, 124)]
    [InlineData(2.0, "Male", 70, 35)]
    public void EstimateGfr_MatchesCkdEpi2021(double creatinine, string sex, int age, double expected)
        => Assert.Equal(expected, LabCalculator.EstimateGfr(creatinine, sex, age));

    [Fact]
    public void EstimateGfr_NeedsAdultAgeAndSex()
    {
        Assert.Null(LabCalculator.EstimateGfr(1.0, null, 40));
        Assert.Null(LabCalculator.EstimateGfr(1.0, "Male", null));
        Assert.Null(LabCalculator.EstimateGfr(1.0, "Male", 12));
        Assert.Null(LabCalculator.EstimateGfr(0, "Male", 40));
    }

    [Fact]
    public void Derive_Egfr_OnlyWithProfile()
    {
        Assert.DoesNotContain(LabCalculator.Derive(Values(("creatinine", 1.0)), null, null), x => x.TestCode == "egfr");
        Assert.Contains(LabCalculator.Derive(Values(("creatinine", 1.0)), "Male", 50), x => x.TestCode == "egfr" && x.Value == 92);
    }

    [Fact]
    public void Derive_BilirubinAndGlobulin()
    {
        var d = LabCalculator.Derive(Values(("bili_total", 1.0), ("bili_direct", 0.3), ("total_protein", 7.2), ("albumin", 4.4)), null, null)
            .ToDictionary(x => x.TestCode, x => x.Value);
        Assert.Equal(0.7, d["bili_indirect"], 2);
        Assert.Equal(2.8, d["globulin"], 1);
    }

    [Fact]
    public void Bmi_AndCategory()
    {
        Assert.Equal(22.9, LabCalculator.Bmi(70, 175));
        Assert.Null(LabCalculator.Bmi(70, null));
        Assert.Null(LabCalculator.Bmi(0, 175));
        Assert.Equal("Healthy weight", LabCalculator.BmiCategory(22.9));
        Assert.Equal("Overweight", LabCalculator.BmiCategory(27));
    }
}

public class LabFindingParserTests
{
    [Fact]
    public void Parse_NumericFinding_MapsToCatalogAndKeepsPrintedRange()
    {
        var d = LabFindingParser.Parse("S. Creatinine", "1.4", "mg/dl", "0.7 - 1.3")!;
        Assert.Equal("creatinine", d.Definition!.Code);
        Assert.Equal(1.4, d.Value);
        Assert.Equal("mg/dL", d.Unit);           // catalog spelling
        Assert.Equal(0.7, d.RefLow);
        Assert.Equal(1.3, d.RefHigh);
    }

    [Fact]
    public void Parse_AlternativeUnit_IsKept()
    {
        var d = LabFindingParser.Parse("Fasting Blood Sugar", "5.6", "mmol/l", "3.9-5.5")!;
        Assert.Equal("fbs", d.Definition!.Code);
        Assert.Equal("mmol/L", d.Unit);
    }

    [Fact]
    public void Parse_MissingUnit_AssumesPrimary()
    {
        var d = LabFindingParser.Parse("Hemoglobin", "13.2", null, null)!;
        Assert.Equal("g/dL", d.Unit);
        Assert.Null(d.RefLow);
    }

    [Theory]
    [InlineData("HBsAg", "Negative", "Negative")]
    [InlineData("HBsAg", "NEGATIVE (0.21)", "Negative")]
    [InlineData("HIV 1 & 2", "Non Reactive", "Non-reactive")]
    [InlineData("HIV 1 & 2", "Reactive", "Reactive")]
    [InlineData("Anti-HCV", "Positive", "Positive")]
    [InlineData("Urine protein", "Trace", "Trace")]
    [InlineData("Urine sugar", "++", "++")]
    [InlineData("Urine sugar", "Absent", "Nil")]
    public void Parse_QualitativeFinding_PicksTheOption(string name, string value, string expected)
    {
        var d = LabFindingParser.Parse(name, value, null, null)!;
        Assert.True(d.IsQualitative);
        Assert.Equal(expected, d.ValueText);
    }

    [Fact]
    public void Parse_UnknownTest_BecomesCustomDraft()
    {
        var d = LabFindingParser.Parse("Anti-CCP", "12.5", "U/mL", "< 20")!;
        Assert.Null(d.Definition);
        Assert.Equal("Anti-CCP", d.Name);
        Assert.Equal(20, d.RefHigh);
    }

    [Theory]
    [InlineData("HBsAg", "see comment")]
    [InlineData("Hemoglobin", "pending")]
    [InlineData("", "5")]
    public void Parse_ReturnsNull_WhenValueIsUnusable(string name, string value)
        => Assert.Null(LabFindingParser.Parse(name, value, null, null));

    [Theory]
    [InlineData("12.0 - 15.5", 12.0, 15.5)]
    [InlineData("150–450", 150.0, 450.0)]
    [InlineData("3.5 to 5.1", 3.5, 5.1)]
    public void ParseRange_Between(string text, double low, double high)
        => Assert.Equal((low, high), LabFindingParser.ParseRange(text));

    [Theory]
    [InlineData("< 200")]
    [InlineData("Up to 200")]
    [InlineData("Less than 200 mg/dL")]
    public void ParseRange_UpperOnly(string text)
        => Assert.Equal(((double?)null, (double?)200), LabFindingParser.ParseRange(text));

    [Theory]
    [InlineData("> 40")]
    [InlineData(">= 40")]
    [InlineData("Above 40")]
    public void ParseRange_LowerOnly(string text)
        => Assert.Equal(((double?)40, (double?)null), LabFindingParser.ParseRange(text));

    [Fact]
    public void ParseRange_Unreadable_GivesNoRange()
        => Assert.Equal(((double?)null, (double?)null), LabFindingParser.ParseRange("see note"));

    [Theory]
    [InlineData("13.2", 13.2)]
    [InlineData("1,250", 1250)]
    [InlineData("< 0.5", 0.5)]
    [InlineData("7.2 H", 7.2)]
    public void TryParseNumber_ReadsLabNotation(string text, double expected)
    {
        Assert.True(LabFindingParser.TryParseNumber(text, out var v));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void ScanText_ReadsReportRows_AndIgnoresNoise()
    {
        const string text = """
            POPULAR DIAGNOSTIC CENTRE
            Patient ID 4455123   Age 42 Years
            S. Creatinine      1.10 mg/dL   0.7 - 1.3
            ALT (SGPT)  : 62 U/L   Up to 41
            Total Cholesterol 212 mg/dL < 200
            Vitamin B12 350 pg/mL 200 - 900
            HBsAg : Negative
            Anti-HCV    Positive
            Printed on 12/10/2026
            """;
        var drafts = LabFindingParser.ScanText(text).ToDictionary(d => d.Definition!.Code);

        Assert.Equal(6, drafts.Count);
        Assert.Equal(1.10, drafts["creatinine"].Value);
        Assert.Equal(1.3, drafts["creatinine"].RefHigh);
        Assert.Equal(62, drafts["alt"].Value);
        Assert.Equal(41, drafts["alt"].RefHigh);
        Assert.Equal(212, drafts["chol_total"].Value);
        Assert.Equal(350, drafts["vitamin_b12"].Value);
        Assert.Equal("Negative", drafts["hbsag"].ValueText);
        Assert.Equal("Positive", drafts["anti_hcv"].ValueText);
    }
}
