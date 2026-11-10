using System.Globalization;

namespace HealthTracker.Services.Ai;

/// <summary>One medicine as read from a prescription or doctor-visit document.</summary>
public class AiPrescribedMedicine
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Strength as written, e.g. "500 mg", "5 ml".</summary>
    public string? Dosage { get; set; }
    /// <summary>Tablet, Capsule, Syrup, Injection, Drops, Cream, Inhaler, Other.</summary>
    public string? Form { get; set; }
    public int? FrequencyPerDay { get; set; }
    /// <summary>Suggested clock times, "HH:mm" 24-hour. Validated by <see cref="AiFieldNormalizer"/>.</summary>
    public List<string> Times { get; set; } = [];
    public int? DurationDays { get; set; }
    /// <summary>Free text: "after food", "SOS for fever", etc.</summary>
    public string? Instructions { get; set; }
}

/// <summary>A single lab value or observation lifted from a report.</summary>
public class AiKeyFinding
{
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Unit { get; set; }
    public string? ReferenceRange { get; set; }
    /// <summary>"normal", "high" or "low".</summary>
    public string? Flag { get; set; }
}

/// <summary>Rich result of <see cref="Services.AiParserService.ExtractReportAsync"/>.</summary>
public class AiReportExtraction
{
    public string? Title { get; set; }
    /// <summary>One of <see cref="AiFieldNormalizer.ReportTypes"/>.</summary>
    public string? ReportType { get; set; }
    /// <summary>"yyyy-MM-dd" or null.</summary>
    public string? ReportDate { get; set; }
    public string? DoctorName { get; set; }
    public string? Hospital { get; set; }
    public string? Summary { get; set; }
    public List<AiKeyFinding> KeyFindings { get; set; } = [];
}

/// <summary>Rich result of <see cref="Services.AiParserService.ExtractDoctorVisitAsync"/>.</summary>
public class AiDoctorVisitExtraction
{
    public string? DoctorName { get; set; }
    public string? Hospital { get; set; }
    /// <summary>"yyyy-MM-dd" or null.</summary>
    public string? VisitDate { get; set; }
    public string? Diagnosis { get; set; }
    public string? Notes { get; set; }
    /// <summary>"yyyy-MM-dd" or null.</summary>
    public string? FollowUpDate { get; set; }
    public List<AiPrescribedMedicine> Medicines { get; set; } = [];
}

/// <summary>Result of <see cref="PrescriptionAiParser.ParseAsync"/>: medicines plus the header fields.</summary>
public class AiPrescriptionExtraction
{
    public string? DoctorName { get; set; }
    public string? Hospital { get; set; }
    /// <summary>"yyyy-MM-dd" or null.</summary>
    public string? PrescriptionDate { get; set; }
    public List<AiPrescribedMedicine> Medicines { get; set; } = [];
}

/// <summary>
/// Post-processing applied to everything the model returns: dates must be exactly "yyyy-MM-dd",
/// times exactly "HH:mm", enums must be from our lists. Anything else becomes null/dropped
/// rather than being guessed at.
/// </summary>
public static class AiFieldNormalizer
{
    /// <summary>Same list as AddReportViewModel.ReportTypes; kept here so the schema enum matches the picker.</summary>
    public static readonly string[] ReportTypes = ["Lab", "Prescription", "Xray", "Scan", "Other"];

    public static readonly string[] FindingFlags = ["normal", "high", "low"];

    public static readonly string[] MedicineForms =
        ["Tablet", "Capsule", "Syrup", "Injection", "Drops", "Cream", "Inhaler", "Other"];

    public static string? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    public static string? Time(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt)
            ? dt.ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;
    }

    public static string? Text(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : t;
    }

    public static string? OneOf(string? s, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return allowed.FirstOrDefault(a => a.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static int? PositiveInt(int? n) => n is > 0 ? n : null;

    public static void Clean(AiPrescribedMedicine m)
    {
        m.Name = Text(m.Name) ?? "Unknown";
        m.Dosage = Text(m.Dosage);
        m.Form = OneOf(m.Form, MedicineForms);
        m.FrequencyPerDay = PositiveInt(m.FrequencyPerDay);
        m.DurationDays = PositiveInt(m.DurationDays);
        m.Instructions = Text(m.Instructions);
        m.Times = (m.Times ?? [])
            .Select(Time)
            .Where(t => t != null)
            .Select(t => t!)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    public static void Clean(AiKeyFinding f)
    {
        f.Name = Text(f.Name) ?? string.Empty;
        f.Value = Text(f.Value);
        f.Unit = Text(f.Unit);
        f.ReferenceRange = Text(f.ReferenceRange);
        f.Flag = OneOf(f.Flag, FindingFlags);
    }

    public static void Clean(AiReportExtraction r)
    {
        r.Title = Text(r.Title);
        r.ReportType = OneOf(r.ReportType, ReportTypes);
        r.ReportDate = Date(r.ReportDate);
        r.DoctorName = Text(r.DoctorName);
        r.Hospital = Text(r.Hospital);
        r.Summary = Text(r.Summary);
        r.KeyFindings ??= [];
        foreach (var f in r.KeyFindings) Clean(f);
        r.KeyFindings.RemoveAll(f => string.IsNullOrEmpty(f.Name));
    }

    public static void Clean(AiDoctorVisitExtraction v)
    {
        v.DoctorName = Text(v.DoctorName);
        v.Hospital = Text(v.Hospital);
        v.VisitDate = Date(v.VisitDate);
        v.Diagnosis = Text(v.Diagnosis);
        v.Notes = Text(v.Notes);
        v.FollowUpDate = Date(v.FollowUpDate);
        v.Medicines ??= [];
        foreach (var m in v.Medicines) Clean(m);
        v.Medicines.RemoveAll(m => m.Name == "Unknown");
    }

    public static void Clean(AiPrescriptionExtraction p)
    {
        p.DoctorName = Text(p.DoctorName);
        p.Hospital = Text(p.Hospital);
        p.PrescriptionDate = Date(p.PrescriptionDate);
        p.Medicines ??= [];
        foreach (var m in p.Medicines) Clean(m);
        p.Medicines.RemoveAll(m => m.Name == "Unknown");
    }

    /// <summary>Schema fragment for the medicines[] array, shared by the prescription and visit extractors.</summary>
    public static Dictionary<string, object> MedicinesArraySchema() => GeminiSchema.Array(
        "Every medicine prescribed on the document, in the order written. Empty array if none.",
        GeminiSchema.Object(new Dictionary<string, object>
        {
            ["name"] = GeminiSchema.String("Brand or generic medicine name as written, without the form prefix (no 'Tab.'/'Cap.')."),
            ["dosage"] = GeminiSchema.String("Strength per dose as written, e.g. '500 mg', '5 ml', '1 puff'.", nullable: true),
            ["form"] = GeminiSchema.Enum("Dosage form.", MedicineForms, nullable: true),
            ["frequencyPerDay"] = GeminiSchema.Integer(
                "Doses per day. 1-0-1 means 2; 1-1-1 means 3; OD=1, BD/BID=2, TDS/TID=3, QID=4, HS=1 at night. Null if unclear or SOS/as-needed.",
                nullable: true),
            ["times"] = GeminiSchema.Array(
                "Suggested clock times in 24-hour 'HH:mm'. Morning 08:00, noon 13:00, evening 18:00, night 21:00. Empty if frequency unknown.",
                GeminiSchema.String("Time in HH:mm")),
            ["durationDays"] = GeminiSchema.Integer("Course length in days ('x 7 days', 'for 2 weeks' = 14, '1 month' = 30). Null if not stated or continuous.", nullable: true),
            ["instructions"] = GeminiSchema.String("Timing/food instructions and any notes, e.g. 'after food', 'SOS for fever', 'before breakfast'.", nullable: true),
        }));
}
