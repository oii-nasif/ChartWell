namespace HealthTracker.Constants;

public enum LabValueKind
{
    /// <summary>A measured number with a unit and (usually) a reference range.</summary>
    Numeric,
    /// <summary>A reported word: Positive/Negative, Reactive/Non-reactive, Nil/Trace/+ ...</summary>
    Qualitative
}

/// <summary>An alternative unit. <paramref name="ToPrimary"/> converts a value in this unit to the test's primary unit.</summary>
public sealed record LabUnit(string Name, double ToPrimary);

/// <summary>A group of related tests, normally ordered and printed together.</summary>
public sealed record LabPanel(string Code, string Name, string Icon, string ColorToken, string Description);

/// <summary>
/// One orderable lab test. Reference ranges are typical adult ranges in the primary unit; every lab
/// prints its own, so a saved result carries the range it was judged against and the user can
/// overwrite it with the printed one.
/// </summary>
public sealed class LabTestDefinition
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string PanelCode { get; init; }
    public LabValueKind Kind { get; init; } = LabValueKind.Numeric;

    /// <summary>Primary unit (empty for ratios and qualitative tests).</summary>
    public string Unit { get; init; } = string.Empty;
    public LabUnit[] AltUnits { get; init; } = [];

    public double? RefLow { get; init; }
    public double? RefHigh { get; init; }
    public (double? Low, double? High)? Male { get; init; }
    public (double? Low, double? High)? Female { get; init; }

    /// <summary>Fixed decimal places for display; null picks them from the magnitude.</summary>
    public int? Decimals { get; init; }

    /// <summary>Allowed words for a qualitative test, in the order shown.</summary>
    public string[] Options { get; init; } = [];
    /// <summary>The unremarkable option, or null when no option is "normal" (blood group, immunity markers).</summary>
    public string? Expected { get; init; }

    /// <summary>Other names the test is printed under; matched case- and punctuation-insensitively.</summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>One or two plain sentences on what the test looks at. Never advice.</summary>
    public string About { get; init; } = string.Empty;

    public bool IsQualitative => Kind == LabValueKind.Qualitative;

    /// <summary>Reference range in the primary unit for the given sex ("Male"/"Female"; anything else gives the general range).</summary>
    public (double? Low, double? High) RangeFor(string? sex)
    {
        if (string.Equals(sex, "Male", StringComparison.OrdinalIgnoreCase) && Male is { } m) return m;
        if (string.Equals(sex, "Female", StringComparison.OrdinalIgnoreCase) && Female is { } f) return f;
        return (RefLow, RefHigh);
    }

    /// <summary>Primary unit first, then the alternatives.</summary>
    public IEnumerable<string> UnitNames => new[] { Unit }.Concat(AltUnits.Select(u => u.Name));

    /// <summary>Factor that converts a value in <paramref name="unit"/> to the primary unit; null when the unit is unknown.</summary>
    public double? FactorToPrimary(string? unit)
    {
        if (LabCatalog.UnitEquals(unit, Unit)) return 1;
        foreach (var alt in AltUnits)
            if (LabCatalog.UnitEquals(unit, alt.Name)) return alt.ToPrimary;
        return null;
    }

    /// <summary>The catalog spelling of <paramref name="unit"/> when it is one of this test's units, else null.</summary>
    public string? CanonicalUnit(string? unit)
        => UnitNames.FirstOrDefault(u => LabCatalog.UnitEquals(unit, u));
}

/// <summary>
/// The built-in list of lab panels and tests. MAUI-free (linked into the test project).
/// Ranges are common adult reference intervals; they are a convenience for flagging, not a diagnosis.
/// </summary>
public static class LabCatalog
{
    public const string CustomPanel = "custom";
    public const string CustomPrefix = "custom:";

    public static readonly string[] NegPos = ["Negative", "Positive"];
    public static readonly string[] NegPosBorderline = ["Negative", "Positive", "Borderline"];
    public static readonly string[] Reactive = ["Non-reactive", "Reactive"];
    public static readonly string[] Graded = ["Nil", "Trace", "+", "++", "+++", "++++"];

    public static readonly IReadOnlyList<LabPanel> Panels =
    [
        new("lipid", "Lipid profile", "ic_cardiology.png", "LabLipid", "Cholesterol and triglycerides: fats carried in the blood."),
        new("kidney", "Kidney function", "ic_nephrology.png", "LabKidney", "How well the kidneys clear waste from the blood."),
        new("liver", "Liver function", "ic_gastroenterology.png", "LabLiver", "Liver enzymes, bilirubin and proteins."),
        new("diabetes", "Blood sugar", "ic_glucose.png", "LabDiabetes", "Glucose at a point in time and averaged over months."),
        new("cbc", "Complete blood count", "ic_hematology.png", "LabBlood", "Red cells, white cells and platelets."),
        new("thyroid", "Thyroid", "ic_endocrinology.png", "LabThyroid", "Thyroid hormones and the signal that controls them."),
        new("electrolytes", "Electrolytes & minerals", "ic_experiment.png", "LabElectrolyte", "Salts and minerals dissolved in the blood."),
        new("vitamins", "Vitamins & iron", "ic_nutrition.png", "LabVitamin", "Vitamin levels and iron stores."),
        new("cardiac", "Heart & inflammation", "ic_ecg_heart.png", "LabCardiac", "Markers of heart strain and inflammation."),
        new("infection", "Hepatitis & infections", "ic_microbiology.png", "LabInfection", "Screening tests that are reported as positive or negative."),
        new("urine", "Urine routine", "ic_urology.png", "LabUrine", "Routine examination of a urine sample."),
        new("other", "Other tests", "ic_science.png", "LabOther", "Coagulation, pancreas, blood group and more."),
        new(CustomPanel, "My tests", "ic_biotech.png", "LabOther", "Tests you added yourself."),
    ];

    public static readonly IReadOnlyList<LabTestDefinition> Tests = Build();

    private static readonly Dictionary<string, LabTestDefinition> ByCode =
        Tests.ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, LabPanel> PanelByCode =
        Panels.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, LabTestDefinition> ByAlias = BuildAliasIndex();

    public static LabTestDefinition? Find(string? code)
        => code is not null && ByCode.TryGetValue(code, out var def) ? def : null;

    public static LabPanel Panel(string? code)
        => code is not null && PanelByCode.TryGetValue(code, out var p) ? p : PanelByCode["other"];

    public static IEnumerable<LabTestDefinition> TestsIn(string panelCode)
        => Tests.Where(t => t.PanelCode.Equals(panelCode, StringComparison.OrdinalIgnoreCase));

    public static bool IsCustom(string? testCode)
        => testCode is not null && testCode.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable code for a user-defined test: "custom:" + the normalised name.</summary>
    public static string CustomCode(string name) => CustomPrefix + Normalize(name).Replace(' ', '-');

    /// <summary>
    /// Finds the catalog test a printed name refers to ("S. Creatinine", "ALT (SGPT)", "Serum Cholesterol - Total").
    /// Returns null when nothing matches confidently; never guesses between two tests.
    /// </summary>
    public static LabTestDefinition? Match(string? printedName)
    {
        var key = Normalize(printedName);
        if (key.Length == 0) return null;
        if (ByAlias.TryGetValue(key, out var def)) return def;

        // Specimen prefixes and trailing qualifiers that labs add freely.
        var stripped = key;
        foreach (var prefix in new[] { "serum ", "s ", "sr ", "plasma ", "blood ", "p ", "b " })
            if (stripped.StartsWith(prefix, StringComparison.Ordinal)) { stripped = stripped[prefix.Length..]; break; }
        foreach (var suffix in new[] { " serum", " plasma", " level", " levels", " test", " count", " total" })
            if (stripped.EndsWith(suffix, StringComparison.Ordinal)) { stripped = stripped[..^suffix.Length]; break; }

        return stripped != key && ByAlias.TryGetValue(stripped, out def) ? def : null;
    }

    /// <summary>Tests whose name or an alias contains the query; used by the add-test search.</summary>
    public static List<LabTestDefinition> Search(string? query)
    {
        var q = Normalize(query);
        if (q.Length == 0) return Tests.ToList();
        return Tests
            .Where(t => Normalize(t.Name).Contains(q, StringComparison.Ordinal)
                        || t.Aliases.Any(a => Normalize(a).Contains(q, StringComparison.Ordinal))
                        || Normalize(Panel(t.PanelCode).Name).Contains(q, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>Lower-case letters and digits separated by single spaces ("ALT (SGPT)" → "alt sgpt").</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var ch in s.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else pendingSpace = true;
        }
        return sb.ToString();
    }

    /// <summary>Unit comparison that ignores case, spaces and the µ/u, ×/x and superscript spellings labs mix up.</summary>
    public static bool UnitEquals(string? a, string? b) => UnitKey(a) == UnitKey(b);

    private static string UnitKey(string? u)
    {
        if (string.IsNullOrWhiteSpace(u)) return string.Empty;
        var s = u.Trim().ToLowerInvariant()
            .Replace('μ', 'u').Replace('µ', 'u')
            .Replace('×', 'x').Replace("^", "")
            .Replace("³", "3").Replace("⁶", "6").Replace("²", "2")
            .Replace("mcg", "ug").Replace("mcl", "ul")
            .Replace("cu mm", "cmm").Replace("cumm", "cmm").Replace("mm3", "cmm")
            .Replace(" ", "");
        return s switch
        {
            "iu/l" => "u/l",
            "miu/ml" or "uiu/ml" => "miu/l",
            "gm/dl" or "g%" or "gm%" => "g/dl",
            "mg%" => "mg/dl",
            "mmhr" or "mm/1sthr" or "mm/1sthour" or "mmin1sthour" => "mm/hr",
            "ml/min/1.73m2" or "ml/min" => "ml/min/1.73m2",
            _ => s
        };
    }

    private static Dictionary<string, LabTestDefinition> BuildAliasIndex()
    {
        var index = new Dictionary<string, LabTestDefinition>(StringComparer.Ordinal);
        foreach (var t in Tests)
        {
            foreach (var name in new[] { t.Name, t.Code.Replace('_', ' ') }.Concat(t.Aliases))
            {
                var key = Normalize(name);
                // First definition wins: an alias shared by two tests stays with the earlier, more common one.
                if (key.Length > 0) index.TryAdd(key, t);
            }
        }
        return index;
    }

    // ── Builders ─────────────────────────────────────────────────────────────────────────────

    private static LabTestDefinition N(string code, string panel, string name, string unit, double? low, double? high,
        string about, string[]? aliases = null, (double?, double?)? male = null, (double?, double?)? female = null,
        LabUnit[]? alt = null, int? decimals = null) => new()
    {
        Code = code, PanelCode = panel, Name = name, Unit = unit, RefLow = low, RefHigh = high, About = about,
        Aliases = aliases ?? [], Male = male, Female = female, AltUnits = alt ?? [], Decimals = decimals,
    };

    private static LabTestDefinition Q(string code, string panel, string name, string[] options, string? expected,
        string about, string[]? aliases = null) => new()
    {
        Code = code, PanelCode = panel, Name = name, Kind = LabValueKind.Qualitative, Options = options,
        Expected = expected, About = about, Aliases = aliases ?? [],
    };

    private static LabUnit[] Alt(string unit, double toPrimary) => [new LabUnit(unit, toPrimary)];

    private static List<LabTestDefinition> Build() =>
    [
        // ── Lipid profile (mg/dL; mmol/L accepted) ───────────────────────────────────────────
        N("chol_total", "lipid", "Total cholesterol", "mg/dL", null, 200,
            "All the cholesterol in the blood. Below 200 mg/dL is considered desirable.",
            ["Cholesterol", "Cholesterol total", "Total cholesterol", "S. Cholesterol", "TC", "Cholesterol (Total)"],
            alt: Alt("mmol/L", 38.67)),
        N("ldl", "lipid", "LDL cholesterol", "mg/dL", null, 130,
            "The \"bad\" cholesterol that can build up in artery walls. Below 100 mg/dL is optimal; doctors often set a lower personal target.",
            ["LDL", "LDL-C", "LDL cholesterol", "Cholesterol LDL", "LDL cholesterol direct", "Low density lipoprotein"],
            alt: Alt("mmol/L", 38.67)),
        N("hdl", "lipid", "HDL cholesterol", "mg/dL", 40, null,
            "The \"good\" cholesterol that carries cholesterol away from the arteries. Higher is better.",
            ["HDL", "HDL-C", "HDL cholesterol", "Cholesterol HDL", "High density lipoprotein"],
            male: (40, null), female: (50, null), alt: Alt("mmol/L", 38.67)),
        N("triglycerides", "lipid", "Triglycerides", "mg/dL", null, 150,
            "The most common fat in the blood; rises after meals, so it is usually measured fasting.",
            ["TG", "Triglyceride", "S. Triglyceride", "Triglycerides fasting"],
            alt: Alt("mmol/L", 88.57)),
        N("vldl", "lipid", "VLDL cholesterol", "mg/dL", null, 30,
            "Cholesterol carried with triglycerides; usually estimated as triglycerides ÷ 5.",
            ["VLDL", "VLDL-C", "Very low density lipoprotein"], alt: Alt("mmol/L", 38.67)),
        N("non_hdl", "lipid", "Non-HDL cholesterol", "mg/dL", null, 160,
            "Total cholesterol minus HDL: all the cholesterol in particles that can clog arteries.",
            ["Non HDL", "Non-HDL-C", "Non HDL cholesterol"], alt: Alt("mmol/L", 38.67)),
        N("chol_hdl_ratio", "lipid", "Cholesterol / HDL ratio", "", null, 5,
            "Total cholesterol divided by HDL. A lower ratio means a larger share is the protective kind.",
            ["TC/HDL ratio", "Chol/HDL ratio", "Total cholesterol HDL ratio", "Cholesterol HDL ratio"], decimals: 1),

        // ── Kidney ───────────────────────────────────────────────────────────────────────────
        N("creatinine", "kidney", "Serum creatinine", "mg/dL", 0.6, 1.3,
            "A waste product from muscle that healthy kidneys filter out. A rising value can mean the kidneys are filtering less.",
            ["Creatinine", "S. Creatinine", "S Creatinine", "Sr. Creatinine", "Creatinine serum"],
            male: (0.7, 1.3), female: (0.6, 1.1), alt: Alt("µmol/L", 1 / 88.4), decimals: 2),
        N("egfr", "kidney", "eGFR", "mL/min/1.73m²", 60, null,
            "Estimated filtering rate of the kidneys, worked out from creatinine, age and sex. 90 or more is normal; below 60 for three months suggests reduced kidney function.",
            ["Estimated GFR", "e-GFR", "GFR", "eGFR CKD-EPI", "Estimated glomerular filtration rate"], decimals: 0),
        N("urea", "kidney", "Blood urea", "mg/dL", 15, 45,
            "A waste product of protein breakdown, cleared by the kidneys.",
            ["Urea", "S. Urea", "Serum urea"], alt: Alt("mmol/L", 6.006)),
        N("bun", "kidney", "BUN", "mg/dL", 7, 20,
            "Blood urea nitrogen: the nitrogen part of urea. Roughly half the blood urea value.",
            ["Blood urea nitrogen", "Urea nitrogen"], alt: Alt("mmol/L", 2.801)),
        N("uric_acid", "kidney", "Uric acid", "mg/dL", 2.4, 7.0,
            "Formed when the body breaks down purines. High levels can cause gout and kidney stones.",
            ["S. Uric acid", "Serum uric acid", "Urate"],
            male: (3.4, 7.0), female: (2.4, 6.0), alt: Alt("µmol/L", 1 / 59.48)),
        N("urine_acr", "kidney", "Urine albumin / creatinine ratio", "mg/g", null, 30,
            "Albumin leaking into urine, an early sign of kidney stress. Below 30 mg/g is normal.",
            ["ACR", "Urine ACR", "Albumin creatinine ratio", "Microalbumin creatinine ratio", "UACR"],
            alt: Alt("mg/mmol", 8.84)),

        // ── Liver ────────────────────────────────────────────────────────────────────────────
        N("alt", "liver", "ALT (SGPT)", "U/L", null, 41,
            "An enzyme found mainly in liver cells; it leaks into the blood when the liver is irritated or injured.",
            ["ALT", "SGPT", "S. ALT", "ALT SGPT", "SGPT ALT", "S. SGPT", "Alanine aminotransferase", "Alanine transaminase"],
            male: (null, 41), female: (null, 33), decimals: 0),
        N("ast", "liver", "AST (SGOT)", "U/L", null, 40,
            "An enzyme in the liver, heart and muscle. Read together with ALT.",
            ["AST", "SGOT", "S. AST", "AST SGOT", "SGOT AST", "S. SGOT", "Aspartate aminotransferase", "Aspartate transaminase"],
            male: (null, 40), female: (null, 32), decimals: 0),
        N("alp", "liver", "Alkaline phosphatase", "U/L", 40, 129,
            "An enzyme from the bile ducts and bone.",
            ["ALP", "S. ALP", "Alk phosphatase", "Alkaline phosphatase ALP", "S. Alkaline phosphatase"],
            male: (40, 129), female: (35, 104), decimals: 0),
        N("ggt", "liver", "GGT", "U/L", null, 71,
            "An enzyme that rises with bile duct problems and regular alcohol use.",
            ["Gamma GT", "γ-GT", "Gamma glutamyl transferase", "GGTP"],
            male: (null, 71), female: (null, 42), decimals: 0),
        N("bili_total", "liver", "Bilirubin (total)", "mg/dL", null, 1.2,
            "The yellow pigment from broken-down red cells, processed by the liver. High levels cause jaundice.",
            ["Bilirubin", "Total bilirubin", "S. Bilirubin", "S. Bilirubin total", "Bilirubin total", "Serum bilirubin"],
            alt: Alt("µmol/L", 1 / 17.1), decimals: 2),
        N("bili_direct", "liver", "Bilirubin (direct)", "mg/dL", null, 0.3,
            "Bilirubin the liver has already processed (conjugated).",
            ["Direct bilirubin", "Conjugated bilirubin", "Bilirubin direct", "Bilirubin conjugated"],
            alt: Alt("µmol/L", 1 / 17.1), decimals: 2),
        N("bili_indirect", "liver", "Bilirubin (indirect)", "mg/dL", null, 1.0,
            "Bilirubin not yet processed by the liver: total minus direct.",
            ["Indirect bilirubin", "Unconjugated bilirubin", "Bilirubin indirect", "Bilirubin unconjugated"],
            alt: Alt("µmol/L", 1 / 17.1), decimals: 2),
        N("total_protein", "liver", "Total protein", "g/dL", 6.0, 8.3,
            "Albumin plus globulins, the two main protein groups in blood.",
            ["S. Total protein", "Protein total", "Serum total protein"], alt: Alt("g/L", 0.1)),
        N("albumin", "liver", "Albumin", "g/dL", 3.5, 5.2,
            "The main blood protein, made by the liver.",
            ["S. Albumin", "Serum albumin"], alt: Alt("g/L", 0.1)),
        N("globulin", "liver", "Globulin", "g/dL", 2.0, 3.5,
            "Proteins that include antibodies: total protein minus albumin.",
            ["S. Globulin", "Serum globulin"], alt: Alt("g/L", 0.1)),

        // ── Blood sugar ──────────────────────────────────────────────────────────────────────
        N("fbs", "diabetes", "Fasting blood glucose", "mg/dL", 70, 99,
            "Blood sugar after at least 8 hours without food.",
            ["FBS", "FBG", "FPG", "Fasting blood sugar", "Fasting plasma glucose", "Glucose fasting", "Fasting glucose", "Blood glucose fasting", "Plasma glucose fasting", "Blood sugar fasting"],
            alt: Alt("mmol/L", 18.016)),
        N("ppbs", "diabetes", "Glucose 2 hours after meal", "mg/dL", 70, 140,
            "Blood sugar two hours after a meal or after a 75 g glucose drink.",
            ["PPBS", "2HABF", "2 hours ABF", "2 hrs ABF", "Post prandial blood sugar", "Postprandial glucose", "Glucose 2 hours after breakfast", "2 hours after 75 g glucose", "Blood glucose 2 hours after breakfast", "Plasma glucose 2 hours after breakfast", "Glucose PP", "PP glucose", "2h OGTT"],
            alt: Alt("mmol/L", 18.016)),
        N("rbs", "diabetes", "Random blood glucose", "mg/dL", 70, 140,
            "Blood sugar at any time of day, regardless of meals.",
            ["RBS", "RBG", "Random blood sugar", "Random plasma glucose", "Glucose random", "Blood glucose random", "Plasma glucose random", "Blood sugar random"],
            alt: Alt("mmol/L", 18.016)),
        N("hba1c", "diabetes", "HbA1c", "%", null, 5.6,
            "Average blood sugar over roughly the last three months. 5.7–6.4% is the prediabetes range; 6.5% or more is the diabetes range.",
            ["Hb A1c", "HbA1C", "A1c", "Glycated haemoglobin", "Glycated hemoglobin", "Glycosylated hemoglobin", "Glycosylated haemoglobin"], decimals: 1),
        N("insulin_fasting", "diabetes", "Fasting insulin", "µIU/mL", 2.6, 24.9,
            "Insulin level after fasting; read together with fasting glucose.",
            ["Insulin fasting", "S. Insulin", "Serum insulin", "Insulin"]),

        // ── Complete blood count ─────────────────────────────────────────────────────────────
        N("hemoglobin", "cbc", "Hemoglobin", "g/dL", 11.6, 16.6,
            "The protein in red cells that carries oxygen. Low values mean anaemia.",
            ["Hb", "Hgb", "Haemoglobin", "Hemoglobin Hb", "Haemoglobin Hb"],
            male: (13.2, 16.6), female: (11.6, 15.0), alt: Alt("g/L", 0.1)),
        N("rbc", "cbc", "RBC count", "×10⁶/µL", 3.92, 5.65,
            "Number of red blood cells.",
            ["RBC", "Red blood cells", "Red blood cell count", "Total RBC", "Total RBC count", "Erythrocytes"],
            male: (4.35, 5.65), female: (3.92, 5.13), decimals: 2),
        N("wbc", "cbc", "WBC count", "×10³/µL", 4.0, 11.0,
            "Number of white blood cells, the infection-fighting cells.",
            ["WBC", "White blood cells", "White blood cell count", "Total WBC", "Total WBC count", "Total count", "TC of WBC", "TLC", "Total leucocyte count", "Total leukocyte count", "Leucocytes"],
            alt: Alt("/cmm", 0.001)),
        N("platelets", "cbc", "Platelet count", "×10³/µL", 150, 450,
            "Cell fragments that help blood clot.",
            ["Platelets", "Platelet", "PLT", "Total platelet count", "Thrombocytes"],
            alt: Alt("/cmm", 0.001), decimals: 0),
        N("hematocrit", "cbc", "Hematocrit (PCV)", "%", 35.5, 48.6,
            "Share of the blood volume taken up by red cells.",
            ["HCT", "PCV", "Haematocrit", "Hematocrit", "Packed cell volume", "HCT PCV"],
            male: (38.3, 48.6), female: (35.5, 44.9)),
        N("mcv", "cbc", "MCV", "fL", 80, 100, "Average size of a red cell.", ["Mean corpuscular volume"]),
        N("mch", "cbc", "MCH", "pg", 27, 33, "Average amount of hemoglobin in a red cell.", ["Mean corpuscular hemoglobin", "Mean corpuscular haemoglobin"]),
        N("mchc", "cbc", "MCHC", "g/dL", 32, 36, "Average concentration of hemoglobin in red cells.", ["Mean corpuscular hemoglobin concentration"]),
        N("rdw", "cbc", "RDW", "%", 11.5, 14.5, "How much red cells vary in size.", ["RDW-CV", "Red cell distribution width"]),
        N("esr", "cbc", "ESR", "mm/hr", null, 20,
            "How fast red cells settle in a tube; a non-specific sign of inflammation.",
            ["Erythrocyte sedimentation rate", "ESR Westergren"],
            male: (null, 15), female: (null, 20), decimals: 0),
        N("neutrophils", "cbc", "Neutrophils", "%", 40, 75, "White cells that respond first to bacterial infection.", ["Neutrophil", "Polymorphs", "Neutrophils %"]),
        N("lymphocytes", "cbc", "Lymphocytes", "%", 20, 45, "White cells involved in viral defence and immunity.", ["Lymphocyte", "Lymphocytes %"]),
        N("monocytes", "cbc", "Monocytes", "%", 2, 10, "White cells that clear debris and help fight infection.", ["Monocyte", "Monocytes %"]),
        N("eosinophils", "cbc", "Eosinophils", "%", 1, 6, "White cells that rise with allergy and parasites.", ["Eosinophil", "Eosinophils %"]),
        N("basophils", "cbc", "Basophils", "%", null, 1, "The rarest white cells, involved in allergic reactions.", ["Basophil", "Basophils %"]),

        // ── Thyroid ──────────────────────────────────────────────────────────────────────────
        N("tsh", "thyroid", "TSH", "mIU/L", 0.4, 4.0,
            "The pituitary signal that tells the thyroid to work. High usually means an underactive thyroid, low an overactive one.",
            ["Thyroid stimulating hormone", "S. TSH", "TSH ultrasensitive", "Thyrotropin"], decimals: 2),
        N("ft4", "thyroid", "Free T4", "ng/dL", 0.8, 1.8,
            "The active, unbound form of the main thyroid hormone.",
            ["FT4", "Free thyroxine", "T4 free", "Free T4 FT4"], alt: Alt("pmol/L", 0.0777), decimals: 2),
        N("ft3", "thyroid", "Free T3", "pg/mL", 2.3, 4.2,
            "The active, unbound form of the most potent thyroid hormone.",
            ["FT3", "Free triiodothyronine", "T3 free", "Free T3 FT3"], alt: Alt("pmol/L", 0.651), decimals: 2),
        N("t4", "thyroid", "Total T4", "µg/dL", 5.0, 12.0, "All circulating thyroxine, bound and free.",
            ["T4", "Thyroxine", "T4 total", "Total thyroxine"], alt: Alt("nmol/L", 0.0777)),
        N("t3", "thyroid", "Total T3", "ng/dL", 80, 200, "All circulating triiodothyronine, bound and free.",
            ["T3", "Triiodothyronine", "T3 total"], alt: Alt("nmol/L", 65.1), decimals: 0),

        // ── Electrolytes & minerals ──────────────────────────────────────────────────────────
        N("sodium", "electrolytes", "Sodium", "mmol/L", 135, 145, "The main salt outside cells; controls fluid balance.", ["Na", "Na+", "S. Sodium", "Serum sodium"], decimals: 0),
        N("potassium", "electrolytes", "Potassium", "mmol/L", 3.5, 5.1, "Essential for heart rhythm and muscles.", ["K", "K+", "S. Potassium", "Serum potassium"]),
        N("chloride", "electrolytes", "Chloride", "mmol/L", 98, 107, "Works with sodium to balance fluids and acidity.", ["Cl", "Cl-", "S. Chloride", "Serum chloride"], decimals: 0),
        N("bicarbonate", "electrolytes", "Bicarbonate", "mmol/L", 22, 29, "The blood's main acid buffer.", ["HCO3", "HCO3-", "TCO2", "Total CO2", "S. Bicarbonate"], decimals: 0),
        N("calcium", "electrolytes", "Calcium", "mg/dL", 8.6, 10.2, "Needed for bones, nerves and muscles.", ["Ca", "S. Calcium", "Serum calcium", "Calcium total"], alt: Alt("mmol/L", 4.008)),
        N("magnesium", "electrolytes", "Magnesium", "mg/dL", 1.7, 2.2, "Supports muscles, nerves and heart rhythm.", ["Mg", "S. Magnesium", "Serum magnesium"], alt: Alt("mmol/L", 2.431)),
        N("phosphate", "electrolytes", "Phosphate", "mg/dL", 2.5, 4.5, "Works with calcium in bones; cleared by the kidneys.", ["Phosphorus", "PO4", "S. Phosphate", "Inorganic phosphate", "S. Phosphorus"], alt: Alt("mmol/L", 3.097)),

        // ── Vitamins & iron ──────────────────────────────────────────────────────────────────
        N("vitamin_d", "vitamins", "Vitamin D (25-OH)", "ng/mL", 30, 100,
            "The storage form of vitamin D. Below 20 ng/mL is deficient, 20–29 insufficient.",
            ["Vitamin D", "25 OH vitamin D", "25-Hydroxy vitamin D", "Vitamin D total", "Vit D", "25 hydroxyvitamin D", "Vitamin D3"],
            alt: Alt("nmol/L", 0.4006)),
        N("vitamin_b12", "vitamins", "Vitamin B12", "pg/mL", 200, 900, "Needed for nerves and for making red cells.",
            ["B12", "Vit B12", "Cobalamin", "S. Vitamin B12"], alt: Alt("pmol/L", 1.355), decimals: 0),
        N("folate", "vitamins", "Folate", "ng/mL", 3.0, null, "A B vitamin needed to make red cells.", ["Folic acid", "S. Folate", "Serum folate"], alt: Alt("nmol/L", 0.4413)),
        N("ferritin", "vitamins", "Ferritin", "ng/mL", 11, 336, "Reflects the body's iron stores.",
            ["S. Ferritin", "Serum ferritin"], male: (24, 336), female: (11, 307), alt: Alt("µg/L", 1), decimals: 0),
        N("iron", "vitamins", "Serum iron", "µg/dL", 60, 170, "Iron circulating in the blood right now.",
            ["Iron", "S. Iron"], alt: Alt("µmol/L", 5.585), decimals: 0),
        N("tibc", "vitamins", "TIBC", "µg/dL", 240, 450, "How much iron the blood could carry; rises when iron is short.",
            ["Total iron binding capacity"], alt: Alt("µmol/L", 5.585), decimals: 0),
        N("transferrin_sat", "vitamins", "Transferrin saturation", "%", 20, 50, "Share of iron-carrying protein that is loaded with iron.", ["TSAT", "Iron saturation"], decimals: 0),

        // ── Heart & inflammation ─────────────────────────────────────────────────────────────
        N("crp", "cardiac", "CRP", "mg/L", null, 5, "A protein that rises quickly with infection or inflammation.",
            ["C-reactive protein", "C reactive protein", "CRP quantitative"], alt: Alt("mg/dL", 10)),
        N("hs_crp", "cardiac", "hs-CRP", "mg/L", null, 3, "A sensitive CRP used to judge long-term heart risk. Below 1 mg/L is lower risk.",
            ["High sensitivity CRP", "hsCRP", "High sensitive CRP"], decimals: 2),
        N("troponin_i", "cardiac", "Troponin I", "ng/mL", null, 0.04, "A heart-muscle protein released when the heart is injured. Cut-offs depend on the assay; use the one printed on your report.",
            ["Trop I", "Troponin-I", "cTnI", "hs Troponin I", "High sensitive troponin I"], decimals: 3),
        N("ck_mb", "cardiac", "CK-MB", "U/L", null, 25, "A heart-muscle enzyme.", ["CKMB", "CK MB", "Creatine kinase MB"], decimals: 0),
        N("cpk", "cardiac", "CPK (total CK)", "U/L", 26, 308, "A muscle enzyme; rises after muscle injury or hard exercise.",
            ["CPK", "CK", "Creatine kinase", "Creatine phosphokinase", "CK total"], male: (39, 308), female: (26, 192), decimals: 0),
        N("nt_probnp", "cardiac", "NT-proBNP", "pg/mL", null, 125, "Released when the heart is under strain.", ["NT pro BNP", "proBNP", "N-terminal pro BNP"], decimals: 0),
        N("d_dimer", "cardiac", "D-dimer", "µg/mL FEU", null, 0.5, "A fragment left when blood clots break down.", ["D dimer", "DDimer"], decimals: 2),
        N("ra_factor", "cardiac", "RA factor", "IU/mL", null, 14, "An antibody often raised in rheumatoid arthritis.", ["Rheumatoid factor", "RF", "RA test", "RA factor quantitative"], decimals: 0),
        N("aso", "cardiac", "ASO titre", "IU/mL", null, 200, "Antibodies made after a streptococcal infection.", ["ASO", "ASOT", "Anti streptolysin O", "ASO titer"], decimals: 0),

        // ── Hepatitis & infections (qualitative) ─────────────────────────────────────────────
        Q("hbsag", "infection", "HBsAg", NegPosBorderline, "Negative",
            "Hepatitis B surface antigen. Positive means the hepatitis B virus is present in the blood.",
            ["HBs Ag", "Hepatitis B surface antigen", "HBsAg screening", "HBsAg ELISA", "HBsAg ICT", "Australia antigen"]),
        Q("anti_hbs", "infection", "Anti-HBs", NegPos, null,
            "Antibody to hepatitis B surface antigen. Positive usually means immunity from vaccination or a past infection.",
            ["Anti HBs", "HBsAb", "Hepatitis B surface antibody", "Anti-HBs antibody"]),
        Q("anti_hbc", "infection", "Anti-HBc (total)", NegPos, "Negative",
            "Antibody to the hepatitis B core. Positive means a current or past hepatitis B infection.",
            ["Anti HBc", "HBcAb", "Anti HBc total", "Hepatitis B core antibody"]),
        Q("hbeag", "infection", "HBeAg", NegPos, "Negative",
            "A hepatitis B marker of active viral replication.", ["HBe Ag", "Hepatitis B e antigen"]),
        Q("anti_hcv", "infection", "Anti-HCV", NegPosBorderline, "Negative",
            "Antibody to hepatitis C. Positive means exposure to the virus; a follow-up RNA test shows whether it is active.",
            ["Anti HCV", "HCV antibody", "Hepatitis C antibody", "HCV Ab", "Anti-HCV antibody"]),
        Q("anti_hav_igm", "infection", "Anti-HAV IgM", NegPos, "Negative", "Marker of a recent hepatitis A infection.", ["HAV IgM", "Hepatitis A IgM", "Anti HAV IgM"]),
        Q("anti_hev_igm", "infection", "Anti-HEV IgM", NegPos, "Negative", "Marker of a recent hepatitis E infection.", ["HEV IgM", "Hepatitis E IgM", "Anti HEV IgM"]),
        Q("hiv", "infection", "HIV 1 & 2", Reactive, "Non-reactive", "Screening test for HIV antibodies and antigen.",
            ["HIV", "HIV 1 2", "HIV I II", "HIV antibody", "HIV Ag Ab", "Anti HIV", "HIV screening"]),
        Q("vdrl", "infection", "VDRL / RPR", Reactive, "Non-reactive", "Screening test for syphilis.", ["VDRL", "RPR", "VDRL test"]),
        Q("tpha", "infection", "TPHA", NegPos, "Negative", "Confirmatory test for syphilis antibodies.", ["TPHA test", "Treponema pallidum haemagglutination"]),
        Q("dengue_ns1", "infection", "Dengue NS1", NegPos, "Negative", "Dengue virus antigen, detectable in the first days of fever.", ["NS1", "Dengue NS1 antigen", "Dengue NS1 Ag"]),
        Q("dengue_igm", "infection", "Dengue IgM", NegPos, "Negative", "Antibody that appears a few days into a dengue infection.", ["Dengue IgM antibody", "Anti dengue IgM"]),
        Q("dengue_igg", "infection", "Dengue IgG", NegPos, null, "Antibody showing a past (or late-stage) dengue infection.", ["Dengue IgG antibody", "Anti dengue IgG"]),
        Q("malaria", "infection", "Malaria parasite", NegPos, "Negative", "Looks for malaria parasites or their antigens.", ["MP", "Malaria", "Malarial parasite", "Malaria ICT", "Malaria antigen"]),
        Q("widal", "infection", "Widal test", NegPos, "Negative", "Antibody test used for typhoid fever.", ["Widal"]),
        Q("typhoid_igm", "infection", "Typhoid IgM", NegPos, "Negative", "Rapid antibody test for recent typhoid infection.", ["Typhidot IgM", "Salmonella typhi IgM", "Typhoid IgM antibody"]),
        Q("h_pylori", "infection", "H. pylori", NegPos, "Negative", "Tests for the stomach bacterium linked to ulcers.", ["H pylori", "Helicobacter pylori", "H. pylori antibody", "H. pylori antigen"]),
        Q("covid_pcr", "infection", "COVID-19 RT-PCR", ["Not detected", "Detected"], "Not detected", "Detects genetic material of SARS-CoV-2.", ["SARS-CoV-2 RT-PCR", "COVID 19", "COVID RT PCR", "SARS CoV 2"]),

        // ── Urine routine ────────────────────────────────────────────────────────────────────
        N("urine_ph", "urine", "Urine pH", "", 4.5, 8.0, "How acidic the urine is.", ["pH urine", "Reaction"], decimals: 1),
        N("urine_sg", "urine", "Urine specific gravity", "", 1.005, 1.030, "How concentrated the urine is.", ["Specific gravity", "Sp. gravity", "SG"], decimals: 3),
        Q("urine_protein", "urine", "Urine protein", Graded, "Nil", "Protein (albumin) in urine; healthy kidneys keep it out.", ["Urine albumin", "Albumin urine", "Protein urine", "Urinary protein"]),
        Q("urine_sugar", "urine", "Urine sugar", Graded, "Nil", "Glucose spilling into urine.", ["Urine glucose", "Sugar urine", "Glucose urine", "Reducing substance"]),
        Q("urine_ketone", "urine", "Urine ketones", Graded, "Nil", "Produced when the body burns fat instead of sugar.", ["Ketone bodies", "Ketones", "Urine ketone", "Acetone"]),
        Q("urine_blood", "urine", "Urine blood", Graded, "Nil", "Blood detected on the urine dipstick.", ["Blood urine", "Occult blood urine"]),
        Q("urine_nitrite", "urine", "Urine nitrite", NegPos, "Negative", "Suggests bacteria in the urine.", ["Nitrite"]),
        N("urine_pus_cells", "urine", "Urine pus cells", "/HPF", null, 5, "White cells seen under the microscope; many suggest infection.", ["Pus cells", "Pus cell", "WBC urine", "Leucocytes urine"], decimals: 0),
        N("urine_rbc", "urine", "Urine RBC", "/HPF", null, 2, "Red cells seen under the microscope.", ["RBC urine", "Red blood cells urine"], decimals: 0),
        Q("urine_culture", "urine", "Urine culture", ["No growth", "Growth"], "No growth", "Checks whether bacteria grow from the urine sample.", ["Urine C/S", "Urine culture sensitivity", "Urine CS"]),

        // ── Other ────────────────────────────────────────────────────────────────────────────
        N("inr", "other", "INR", "", 0.8, 1.2, "Standardised clotting time. People on warfarin are given a higher personal target.", ["International normalised ratio", "International normalized ratio", "PT INR"], decimals: 2),
        N("pt", "other", "Prothrombin time", "sec", 11, 13.5, "How long blood takes to clot.", ["PT", "Prothrombin time PT"]),
        N("aptt", "other", "APTT", "sec", 25, 35, "Another measure of clotting time.", ["aPTT", "PTT", "Activated partial thromboplastin time"]),
        N("amylase", "other", "Amylase", "U/L", 28, 100, "A digestive enzyme from the pancreas and salivary glands.", ["S. Amylase", "Serum amylase"], decimals: 0),
        N("lipase", "other", "Lipase", "U/L", 13, 60, "A pancreatic enzyme; rises in pancreatitis.", ["S. Lipase", "Serum lipase"], decimals: 0),
        N("psa", "other", "PSA (total)", "ng/mL", null, 4.0, "Prostate-specific antigen.", ["PSA", "Total PSA", "Prostate specific antigen", "S. PSA"], decimals: 2),
        Q("blood_group", "other", "Blood group", ["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"], null, "ABO group and Rh factor.", ["Blood grouping", "ABO Rh", "Blood group and Rh", "Blood grouping and Rh typing"]),
        Q("pregnancy_test", "other", "Pregnancy test", NegPos, null, "Detects the pregnancy hormone hCG.", ["Urine pregnancy test", "UPT", "Beta hCG qualitative", "hCG urine"]),
        Q("occult_blood", "other", "Stool occult blood", NegPos, "Negative", "Hidden blood in a stool sample.", ["OBT", "Occult blood test", "Stool OBT", "FOBT", "Faecal occult blood"]),
    ];
}
