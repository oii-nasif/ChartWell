using System.Text;
using HealthTracker.Services.Ai;

namespace HealthTracker.Services;

/// <summary>Legacy flat shape bound by AddDoctorVisitViewModel. New code should use <see cref="AiDoctorVisitExtraction"/>.</summary>
public class AiParsedDoctorVisit
{
    public string? DoctorName { get; set; }
    public string? Hospital { get; set; }
    public string? VisitDate { get; set; }
    public string? Diagnosis { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Legacy flat shape bound by AddReportViewModel. New code should use <see cref="AiReportExtraction"/>.</summary>
public class AiParsedReport
{
    public string? Title { get; set; }
    public string? DoctorName { get; set; }
    public string? Hospital { get; set; }
    public string? ReportType { get; set; }
    public string? ReportDate { get; set; }
}

/// <summary>
/// Gemini-backed extraction of medical reports and doctor visits, plus the API-key facade the
/// Settings/Add* ViewModels use.
///
/// Two API layers coexist on purpose (the UI is being redesigned):
/// - Old: <see cref="ParseReportAsync(string)"/> / <see cref="ParseDoctorVisitAsync(string)"/> return the
///   flat legacy classes or null on any failure.
/// - New: <see cref="ExtractReportAsync"/> / <see cref="ExtractDoctorVisitAsync"/> return
///   <see cref="GeminiResult{T}"/> with rich typed values, accept the page image, and report why a call failed.
/// </summary>
public class AiParserService
{
    private readonly GeminiClient _client;

    public AiParserService(GeminiClient client)
    {
        _client = client;
    }

    /// <summary>Kept so the existing parameterless DI registration keeps working until MauiProgram registers <see cref="GeminiClient"/>.</summary>
    public AiParserService() : this(new GeminiClient()) { }

    // ----- API key facade -------------------------------------------------------------------

    public static Task<bool> HasApiKeyAsync() => GeminiApiKeyStore.HasKeyAsync();
    public static Task<string?> GetApiKeyAsync() => GeminiApiKeyStore.GetAsync();
    public static Task SaveApiKeyAsync(string key) => GeminiApiKeyStore.SaveAsync(key);
    public static Task ClearApiKeyAsync() => GeminiApiKeyStore.ClearAsync();

    /// <summary>Synchronous check used by current ViewModels; backed by a Preferences flag, never the key itself.</summary>
    public static bool IsConfigured => GeminiApiKeyStore.IsConfigured;

    /// <summary>Legacy synchronous getter. Prefer <see cref="GetApiKeyAsync"/>.</summary>
    [Obsolete("Use GetApiKeyAsync(); SecureStorage is asynchronous.")]
    public static string? GetApiKey() => GeminiApiKeyStore.GetBlocking();

    /// <summary>Legacy setter: null/blank clears. Fire-and-forget; prefer <see cref="SaveApiKeyAsync"/> / <see cref="ClearApiKeyAsync"/>.</summary>
    [Obsolete("Use SaveApiKeyAsync()/ClearApiKeyAsync() and await them.")]
    public static void SetApiKey(string? key)
    {
        var task = string.IsNullOrWhiteSpace(key) ? GeminiApiKeyStore.ClearAsync() : GeminiApiKeyStore.SaveAsync(key);
        task.ContinueWith(t => System.Diagnostics.Debug.WriteLine($"SetApiKey failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // ----- Reports ----------------------------------------------------------------------------

    private const string ReportSystemInstruction =
        "You read medical documents (lab reports, imaging reports, discharge summaries, prescriptions) for a personal " +
        "health-tracking app and fill a form. Extract only what the document says; never invent values. " +
        "If an image is provided, trust it over the OCR text, which may contain recognition errors. " +
        "Dates must be yyyy-MM-dd; when day/month order is ambiguous assume day first. " +
        "Use null for anything not determinable.";

    /// <summary>
    /// Rich extraction. Pass <paramref name="image"/> (page photo) when available: it is far more accurate than
    /// OCR text alone. <paramref name="ocrText"/> may be null when an image is given.
    /// </summary>
    public async Task<GeminiResult<AiReportExtraction>> ExtractReportAsync(
        string? ocrText, GeminiImage? image = null, CancellationToken ct = default)
    {
        if (image == null && string.IsNullOrWhiteSpace(ocrText))
            return GeminiResult<AiReportExtraction>.Fail(GeminiErrorKind.BadResponse, "Nothing to parse.");

        var prompt = new StringBuilder();
        prompt.AppendLine("Extract the report metadata and key findings from this medical report.");
        prompt.AppendLine("reportType: Lab for blood/urine/pathology/ECG results, Xray for plain radiographs, " +
                          "Scan for CT/MRI/ultrasound/echo, Prescription for a doctor's prescription, Other otherwise.");
        prompt.AppendLine("title: a short human title such as 'Complete Blood Count' or 'Chest X-Ray'.");
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            prompt.AppendLine(image != null ? "OCR text from the same page (may contain errors):" : "OCR text of the report:");
            prompt.AppendLine(ocrText.Trim());
        }

        var result = await _client.GenerateJsonAsync<AiReportExtraction>(
            ReportSystemInstruction, prompt.ToString(), ReportSchema(),
            image != null ? new[] { image } : null, ct);

        if (result.Succeeded && result.Value != null)
            AiFieldNormalizer.Clean(result.Value);
        return result;
    }

    /// <summary>Legacy adapter: flat result or null on any failure (callers fall back to regex parsing).</summary>
    public async Task<AiParsedReport?> ParseReportAsync(string ocrText)
    {
        var result = await ExtractReportAsync(ocrText);
        if (!result.Succeeded || result.Value == null)
        {
            System.Diagnostics.Debug.WriteLine($"AI parse report failed ({result.Kind}): {result.Error}");
            return null;
        }
        return ToLegacy(result.Value);
    }

    public static AiParsedReport ToLegacy(AiReportExtraction r) => new()
    {
        Title = r.Title,
        DoctorName = r.DoctorName,
        Hospital = r.Hospital,
        ReportType = r.ReportType,
        ReportDate = r.ReportDate,
    };

    private static Dictionary<string, object> ReportSchema() => GeminiSchema.Object(new Dictionary<string, object>
    {
        ["title"] = GeminiSchema.String("Short title of the report, e.g. 'Complete Blood Count', 'Chest X-Ray PA view'.", nullable: true),
        ["reportType"] = GeminiSchema.Enum("Category of the document.", AiFieldNormalizer.ReportTypes),
        ["reportDate"] = GeminiSchema.String("Report/collection date in yyyy-MM-dd, or null.", nullable: true),
        ["doctorName"] = GeminiSchema.String("Referring or reporting doctor, including 'Dr.' if printed.", nullable: true),
        ["hospital"] = GeminiSchema.String("Hospital, clinic or laboratory name.", nullable: true),
        ["summary"] = GeminiSchema.String("One or two plain-language sentences summarising the findings. No diagnosis beyond what is written.", nullable: true),
        ["keyFindings"] = GeminiSchema.Array(
            "Measured values or notable observations. For lab reports list every analyte; for imaging list the impression items. Empty if none.",
            GeminiSchema.Object(new Dictionary<string, object>
            {
                ["name"] = GeminiSchema.String("Test or observation name, e.g. 'Hemoglobin'."),
                ["value"] = GeminiSchema.String("Measured value as written, e.g. '13.2'. Null for qualitative findings.", nullable: true),
                ["unit"] = GeminiSchema.String("Unit as written, e.g. 'g/dL'.", nullable: true),
                ["referenceRange"] = GeminiSchema.String("Reference range as written, e.g. '12.0 - 15.5'.", nullable: true),
                ["flag"] = GeminiSchema.Enum("Whether the value is flagged relative to the reference range.", AiFieldNormalizer.FindingFlags, nullable: true),
            })),
    });

    // ----- Doctor visits ----------------------------------------------------------------------

    private const string VisitSystemInstruction =
        "You read doctors' prescriptions and consultation notes for a personal health-tracking app and fill a " +
        "doctor-visit form. Extract only what is written; never invent. If an image is provided, trust it over the OCR " +
        "text. Expand abbreviations: OD once daily, BD/BID twice daily, TDS/TID three times daily, QID four times daily, " +
        "HS at bedtime, SOS/PRN as needed, AC before food, PC after food; 1-0-1 style patterns are morning-noon-night counts. " +
        "Dates must be yyyy-MM-dd; when day/month order is ambiguous assume day first. Use null when unknown.";

    /// <summary>Rich extraction of a doctor visit (header, diagnosis, follow-up and the prescribed medicines).</summary>
    public async Task<GeminiResult<AiDoctorVisitExtraction>> ExtractDoctorVisitAsync(
        string? ocrText, GeminiImage? image = null, CancellationToken ct = default)
    {
        if (image == null && string.IsNullOrWhiteSpace(ocrText))
            return GeminiResult<AiDoctorVisitExtraction>.Fail(GeminiErrorKind.BadResponse, "Nothing to parse.");

        var prompt = new StringBuilder();
        prompt.AppendLine("Extract the doctor visit details and every prescribed medicine from this prescription or consultation note.");
        prompt.AppendLine("diagnosis: the condition, complaint or reason for the visit as written. " +
                          "notes: other relevant advice (tests ordered, diet, rest) but NOT the medicine list, which goes in medicines[].");
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            prompt.AppendLine(image != null ? "OCR text from the same page (may contain errors):" : "OCR text of the prescription:");
            prompt.AppendLine(ocrText.Trim());
        }

        var result = await _client.GenerateJsonAsync<AiDoctorVisitExtraction>(
            VisitSystemInstruction, prompt.ToString(), VisitSchema(),
            image != null ? new[] { image } : null, ct);

        if (result.Succeeded && result.Value != null)
            AiFieldNormalizer.Clean(result.Value);
        return result;
    }

    /// <summary>Legacy adapter: flat result or null. Medicines are folded into Notes as the old prompt did.</summary>
    public async Task<AiParsedDoctorVisit?> ParseDoctorVisitAsync(string ocrText)
    {
        var result = await ExtractDoctorVisitAsync(ocrText);
        if (!result.Succeeded || result.Value == null)
        {
            System.Diagnostics.Debug.WriteLine($"AI parse doctor visit failed ({result.Kind}): {result.Error}");
            return null;
        }
        return ToLegacy(result.Value);
    }

    public static AiParsedDoctorVisit ToLegacy(AiDoctorVisitExtraction v)
    {
        var notes = new StringBuilder();
        if (!string.IsNullOrEmpty(v.Notes)) notes.AppendLine(v.Notes);
        if (v.Medicines.Count > 0)
        {
            notes.AppendLine("Prescribed: " + string.Join("; ", v.Medicines.Select(DescribeMedicine)));
        }
        if (!string.IsNullOrEmpty(v.FollowUpDate)) notes.AppendLine($"Follow-up: {v.FollowUpDate}");

        return new AiParsedDoctorVisit
        {
            DoctorName = v.DoctorName,
            Hospital = v.Hospital,
            VisitDate = v.VisitDate,
            Diagnosis = v.Diagnosis,
            Notes = notes.Length > 0 ? notes.ToString().Trim() : null,
        };
    }

    /// <summary>"Amoxicillin 500 mg, 3x/day for 5 days (after food)"</summary>
    public static string DescribeMedicine(AiPrescribedMedicine m)
    {
        var sb = new StringBuilder(m.Name);
        if (!string.IsNullOrEmpty(m.Dosage)) sb.Append(' ').Append(m.Dosage);
        if (m.FrequencyPerDay is { } f) sb.Append($", {f}x/day");
        if (m.DurationDays is { } d) sb.Append($" for {d} days");
        if (!string.IsNullOrEmpty(m.Instructions)) sb.Append($" ({m.Instructions})");
        return sb.ToString();
    }

    private static Dictionary<string, object> VisitSchema() => GeminiSchema.Object(new Dictionary<string, object>
    {
        ["doctorName"] = GeminiSchema.String("Doctor's name including 'Dr.' if printed.", nullable: true),
        ["hospital"] = GeminiSchema.String("Clinic or hospital name.", nullable: true),
        ["visitDate"] = GeminiSchema.String("Visit/prescription date in yyyy-MM-dd, or null.", nullable: true),
        ["diagnosis"] = GeminiSchema.String("Diagnosis, complaint or reason for the visit as written.", nullable: true),
        ["notes"] = GeminiSchema.String("Other advice or findings, excluding the medicine list.", nullable: true),
        ["followUpDate"] = GeminiSchema.String("Follow-up/review date in yyyy-MM-dd if stated, else null. Convert 'review after 2 weeks' using the visit date when both are known.", nullable: true),
        ["medicines"] = AiFieldNormalizer.MedicinesArraySchema(),
    });
}
