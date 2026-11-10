using System.Text;

namespace HealthTracker.Services.Ai;

/// <summary>
/// Reads a prescription (photo and/or OCR text) into structured medicines via Gemini.
/// <see cref="Services.PrescriptionParserService"/> uses this when an API key exists and falls
/// back to its regex heuristics otherwise.
/// </summary>
public class PrescriptionAiParser
{
    private readonly GeminiClient _client;

    public PrescriptionAiParser(GeminiClient client)
    {
        _client = client;
    }

    private const string SystemInstruction =
        "You read handwritten and printed medical prescriptions for a personal medicine-tracking app. " +
        "Extract exactly what is written; do not invent medicines, doses or durations. " +
        "If an image is provided, trust the image over any OCR text, which may contain recognition errors. " +
        "Expand common abbreviations: OD once daily, BD/BID twice daily, TDS/TID three times daily, QID four times daily, " +
        "HS at bedtime, SOS/PRN as needed, AC before food, PC after food. " +
        "Patterns like 1-0-1 or 1+0+1 are morning-noon-night dose counts. " +
        "Dates must be in yyyy-MM-dd; when a day-month order is ambiguous assume day first. " +
        "Use null for anything you cannot determine.";

    /// <summary>
    /// Parses a prescription. Pass <paramref name="image"/> when you have the photo (much more accurate);
    /// <paramref name="ocrText"/> is optional supporting text.
    /// </summary>
    public async Task<GeminiResult<AiPrescriptionExtraction>> ParseAsync(
        string? ocrText, GeminiImage? image = null, CancellationToken ct = default)
    {
        if (image == null && string.IsNullOrWhiteSpace(ocrText))
            return GeminiResult<AiPrescriptionExtraction>.Fail(GeminiErrorKind.BadResponse, "Nothing to parse.");

        var prompt = new StringBuilder();
        prompt.AppendLine("Extract the prescription header and every medicine from this prescription.");
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            prompt.AppendLine(image != null
                ? "OCR text from the same image (may contain errors):"
                : "OCR text of the prescription:");
            prompt.AppendLine(ocrText.Trim());
        }

        var result = await _client.GenerateJsonAsync<AiPrescriptionExtraction>(
            SystemInstruction, prompt.ToString(), Schema(),
            image != null ? new[] { image } : null, ct);

        if (result.Succeeded && result.Value != null)
            AiFieldNormalizer.Clean(result.Value);
        return result;
    }

    private static Dictionary<string, object> Schema() => GeminiSchema.Object(new Dictionary<string, object>
    {
        ["doctorName"] = GeminiSchema.String("Prescribing doctor's name including 'Dr.' if printed.", nullable: true),
        ["hospital"] = GeminiSchema.String("Clinic or hospital name.", nullable: true),
        ["prescriptionDate"] = GeminiSchema.String("Date on the prescription in yyyy-MM-dd, or null.", nullable: true),
        ["medicines"] = AiFieldNormalizer.MedicinesArraySchema(),
    });
}
