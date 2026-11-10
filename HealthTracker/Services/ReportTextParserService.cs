using System.Globalization;
using System.Text.RegularExpressions;

namespace HealthTracker.Services;

public class ParsedReportFields
{
    public string? Title { get; set; }
    public string? DoctorName { get; set; }
    public string? Hospital { get; set; }
    public string? ReportType { get; set; }
    public string? ReportDate { get; set; }
    public string? Notes { get; set; }
}

public partial class ReportTextParserService
{
    private readonly PrescriptionParserService _ocrService;

    public ReportTextParserService(PrescriptionParserService ocrService)
    {
        _ocrService = ocrService;
    }

    /// <summary>OCR via <see cref="PrescriptionParserService"/>, which initialises the OCR plugin once and uses tryHard.</summary>
    public async Task<string> ExtractTextFromImage(byte[] imageData)
        => await _ocrService.ExtractTextFromImageAsync(imageData);

    public async Task<string> ExtractTextFromImageAsync(byte[] imageData, CancellationToken ct = default)
        => await _ocrService.ExtractTextFromImageAsync(imageData, ct);

    public ParsedReportFields ParseReportFields(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
            return new ParsedReportFields();

        var fields = new ParsedReportFields();

        fields.DoctorName = ExtractDoctor(ocrText);
        fields.Hospital = ExtractHospital(ocrText);
        fields.ReportDate = ExtractDate(ocrText);
        fields.ReportType = DetectReportType(ocrText);
        fields.Title = BuildTitle(ocrText, fields.ReportType);

        return fields;
    }

    private static string? ExtractDoctor(string text)
    {
        // "Dr. John Smith", "Doctor: John Smith", "Physician: Dr. Patel"
        var match = DoctorPattern().Match(text);
        if (match.Success)
        {
            var name = match.Groups["name"].Value.Trim().TrimEnd(',', '.', ';');
            if (name.Length > 2 && name.Length < 60) return name;
        }

        // Fallback: line containing "Dr." followed by a name
        var fallback = DrFallbackPattern().Match(text);
        if (fallback.Success)
        {
            var name = fallback.Value.Trim().TrimEnd(',', '.', ';');
            if (name.Length > 4 && name.Length < 60) return name;
        }

        return null;
    }

    private static string? ExtractHospital(string text)
    {
        // "Hospital: XYZ Medical Center"
        var labeled = HospitalLabelPattern().Match(text);
        if (labeled.Success)
        {
            var name = labeled.Groups["name"].Value.Trim().TrimEnd(',', '.', ';');
            if (name.Length > 2) return name;
        }

        // Lines containing hospital-related keywords
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (HospitalKeywordPattern().IsMatch(trimmed) && trimmed.Length > 4 && trimmed.Length < 80)
                return trimmed.TrimEnd(',', '.', ';');
        }

        return null;
    }

    /// <summary>
    /// Accepted formats (InvariantCulture, TryParseExact only): dd/MM/yyyy, dd-MM-yyyy, dd.MM.yyyy
    /// (and their one-digit / two-digit-year variants), yyyy-MM-dd, yyyy/MM/dd, "dd MMM yyyy",
    /// "MMM dd, yyyy" and the full month-name forms.
    ///
    /// Day-first assumption: an all-numeric date such as 03/04/2024 is read as 3 April 2024, never
    /// 4 March. Reports this app targets use day-month-year; US month-first is not attempted, so a
    /// value like 04/15/2024 fails validation and yields null instead of a wrong date.
    /// </summary>
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd",
        "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy",
        "dd-MM-yyyy", "d-M-yyyy", "dd-MM-yy", "d-M-yy",
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy",
        "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy", "d MMMM yyyy",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yy", "d-MMM-yy",
        "MMM dd, yyyy", "MMM d, yyyy", "MMMM dd, yyyy", "MMMM d, yyyy",
        "MMM dd yyyy", "MMM d yyyy",
    ];

    /// <summary>Normalises any accepted date string to "yyyy-MM-dd"; null when it is not a valid date.</summary>
    public static string? NormalizeDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // "15th Mar 2024" → "15 Mar 2024"; collapse runs of whitespace.
        var cleaned = Regex.Replace(raw.Trim(), @"(?<=\d)(?:st|nd|rd|th)\b", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").TrimEnd('.', ',', ';', ':');

        if (!DateTime.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return null;

        // Reject obvious OCR garbage (e.g. year 0124) while allowing old historical reports.
        if (parsed.Year < 1900 || parsed > DateTime.Today.AddYears(1)) return null;
        return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? ExtractDate(string text)
    {
        // A labelled date ("Report Date: 12/03/2024", "Collected on 12 Mar 2024") is the most reliable.
        foreach (Match labeled in LabeledDatePattern().Matches(text))
        {
            var candidate = DatePattern().Match(labeled.Groups["date"].Value);
            if (candidate.Success && NormalizeDate(candidate.Value) is { } labelledDate)
                return labelledDate;
        }

        // Otherwise the first thing in the text that parses as a date.
        foreach (Match match in DatePattern().Matches(text))
        {
            if (NormalizeDate(match.Value) is { } date) return date;
        }

        return null;
    }

    private static string? DetectReportType(string text)
    {
        var lower = text.ToLowerInvariant();

        if (LabKeywords().IsMatch(lower)) return "Lab";
        if (lower.Contains("x-ray") || lower.Contains("xray") || lower.Contains("radiograph")) return "Xray";
        if (lower.Contains("mri") || lower.Contains("magnetic resonance")) return "Scan";
        if (lower.Contains("ct scan") || lower.Contains("computed tomography")) return "Scan";
        if (lower.Contains("ultrasound") || lower.Contains("sonograph")) return "Scan";
        if (lower.Contains("ecg") || lower.Contains("electrocardiog")) return "Lab";
        if (PrescriptionKeywords().IsMatch(lower)) return "Prescription";

        return null;
    }

    private static string? BuildTitle(string text, string? reportType)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Look for common report title patterns
        foreach (var line in lines.Take(8))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 3 || trimmed.Length > 60) continue;

            // Skip lines that are clearly addresses, phone numbers, dates
            if (PhoneOrAddressPattern().IsMatch(trimmed)) continue;

            // Skip patient/header fields: "Patient: Test User" must not win just because it contains "test".
            if (PatientFieldPattern().IsMatch(trimmed)) continue;
            if (trimmed.All(c => char.IsDigit(c) || c == '/' || c == '-' || c == '.')) continue;

            // Lines containing "report", "test", "result" are good title candidates
            if (ReportTitleKeyword().IsMatch(trimmed))
                return trimmed.TrimEnd(',', '.', ';');
        }

        // Fallback: if we detected a type, use it as part of the title
        if (reportType != null)
        {
            return reportType switch
            {
                "Lab" => "Lab Report",
                "Xray" => "X-Ray Report",
                "Scan" => "Scan Report",
                "Prescription" => "Prescription",
                _ => null
            };
        }

        return null;
    }

    // Name character classes deliberately use [ \t] rather than \s so a capture never runs onto the next OCR line.
    [GeneratedRegex(@"(?:doctor|physician|consultant|referred[ \t]+by|attending)[ \t]*[:\-]?[ \t]*(?:Dr\.?[ \t]+)?(?<name>[A-Z][a-zA-Z \t\.]{2,40})", RegexOptions.IgnoreCase)]
    private static partial Regex DoctorPattern();

    [GeneratedRegex(@"Dr\.?[ \t]+[A-Z][a-zA-Z \t\.]{2,40}", RegexOptions.None)]
    private static partial Regex DrFallbackPattern();

    [GeneratedRegex(@"(?:hospital|clinic|centre|center|laboratory|lab|institute)[ \t]*[:\-][ \t]*(?<name>[A-Za-z][A-Za-z \t\.&]{2,60})", RegexOptions.IgnoreCase)]
    private static partial Regex HospitalLabelPattern();

    [GeneratedRegex(@"\b(?:hospital|medical\s+cent(?:er|re)|clinic|diagnostics?|laborator(?:y|ies)|patholog(?:y|ies)|healthcare|health\s+care|institute)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HospitalKeywordPattern();

    // Numeric d/M/y, ISO y-M-d, "12 Mar 2024", "12-Mar-2024", "Mar 12, 2024". Month names may be abbreviated or full.
    [GeneratedRegex(@"\b\d{1,2}[/\-\.]\d{1,2}[/\-\.]\d{2,4}\b|\b\d{4}[/\-]\d{1,2}[/\-]\d{1,2}\b|\b\d{1,2}(?:st|nd|rd|th)?[\s\-]+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*[\s\-]+\d{2,4}\b|\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2},?\s+\d{2,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?:date|dated|reported\s+on|collected\s+on|report\s+date|sample\s+date|collection\s+date|test\s+date|visit\s+date)\s*[:\-]?\s*(?<date>[^\n]{6,24})", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledDatePattern();

    [GeneratedRegex(@"\b(?:blood|haemoglobin|hemoglobin|cbc|wbc|rbc|platelet|cholesterol|glucose|sugar|hba1c|creatinine|urea|thyroid|tsh|lipid|liver\s+function|kidney\s+function|urine|serum|bilirubin|albumin|iron|ferritin|vitamin|triglyceride)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LabKeywords();

    [GeneratedRegex(@"\b(?:prescription|rx|prescribed|medication)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrescriptionKeywords();

    [GeneratedRegex(@"(?:\+\d|tel|phone|ph\.|fax|address|pin\s*code|zip)", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneOrAddressPattern();

    [GeneratedRegex(@"\b(?:report|test|result|investigation|examination|analysis|profile|panel|blood\s+count|cbc|lipid|thyroid|liver\s+function|kidney\s+function|renal|urine|urinalysis|glucose|hba1c|culture|x-?ray|ultrasound|ultrasonography|usg|mri|ct\s+scan|ecg|echo(?:cardiogram)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReportTitleKeyword();

    [GeneratedRegex(@"^(?:patient|name|age|sex|gender|dob|date|ref(?:erred)?(?:\s+by)?|consultant|doctor|dr\.?|id|uhid|mrn|sample|collected|received)\b[^:]{0,15}:", RegexOptions.IgnoreCase)]
    private static partial Regex PatientFieldPattern();
}
