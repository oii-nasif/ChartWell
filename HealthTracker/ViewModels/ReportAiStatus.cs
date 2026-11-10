using HealthTracker.Services.Ai;

namespace HealthTracker.ViewModels;

/// <summary>
/// Maps Gemini failure kinds to the short, friendly notices shown by the Report and Doctor-visit
/// forms, and names the two extraction modes shown in their progress rows.
/// </summary>
public static class ReportAiStatus
{
    public const string AiModeLabel = "AI";
    public const string OcrModeLabel = "On-device OCR";

    public static string Describe(GeminiErrorKind kind) => kind switch
    {
        GeminiErrorKind.NoApiKey => "AI is off; add a Gemini key in Settings. Fields were read with on-device text recognition.",
        GeminiErrorKind.Network => "AI is unreachable right now; fields were read with on-device text recognition.",
        GeminiErrorKind.Unauthorized => "Your Gemini key was rejected. Check it in Settings. Fields were read with on-device text recognition.",
        GeminiErrorKind.RateLimited => "AI is busy right now; fields were read with on-device text recognition.",
        GeminiErrorKind.Cancelled => "AI reading was cancelled.",
        _ => "AI could not read this page; fields were read with on-device text recognition.",
    };

    /// <summary>True when the fix lives in Settings (missing or rejected key).</summary>
    public static bool NeedsSettings(GeminiErrorKind kind) =>
        kind is GeminiErrorKind.NoApiKey or GeminiErrorKind.Unauthorized;
}
