using System.Globalization;
using System.Text;
using HealthTracker.Services.Ai;

namespace HealthTracker.Services;

/// <summary>
/// "Ask AI" chat over the user's own health data.
///
/// - <see cref="AskAsync"/> keeps the signature AskAiViewModel uses and always returns display text
///   (friendly error sentences on failure).
/// - <see cref="AskTypedAsync"/> returns <see cref="GeminiResult{T}"/> so the redesigned UI can branch
///   on <see cref="GeminiErrorKind"/> (e.g. offer the Settings page on NoApiKey/Unauthorized).
///
/// The health context is rebuilt from the database on every question via <see cref="HealthContextBuilder"/>
/// and placed in the system instruction; the conversation history is sent as proper multi-turn content.
/// </summary>
public class AiChatService
{
    private readonly HealthContextBuilder _contextBuilder;
    private readonly GeminiClient _client;

    public AiChatService(HealthContextBuilder contextBuilder, GeminiClient client)
    {
        _contextBuilder = contextBuilder;
        _client = client;
    }

    /// <summary>Legacy entry point: never throws, never returns an empty string.</summary>
    public async Task<string> AskAsync(string question, List<(string Role, string Text)> history)
    {
        var result = await AskTypedAsync(question, history);
        if (result.Succeeded && !string.IsNullOrWhiteSpace(result.Value)) return result.Value;
        return ToUserMessage(result);
    }

    public async Task<GeminiResult<string>> AskTypedAsync(string question,
        IEnumerable<(string Role, string Text)> history, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return GeminiResult<string>.Fail(GeminiErrorKind.BadResponse, "Please type a question.");

        if (!await GeminiApiKeyStore.HasKeyAsync())
            return GeminiResult<string>.Fail(GeminiErrorKind.NoApiKey, "Gemini API key is not configured.");

        string context;
        try { context = await _contextBuilder.BuildAsync(ct); }
        catch (OperationCanceledException) { return GeminiResult<string>.Fail(GeminiErrorKind.Cancelled, "Cancelled."); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HealthContextBuilder failed: {ex}");
            context = "(The app could not load the health data for this question. Say so if the user asks about their data.)";
        }

        var turns = history
            .Where(h => !string.IsNullOrWhiteSpace(h.Text))
            .Select(h => (role: h.Role == "user" ? "user" : "model", text: h.Text))
            .ToList();

        return await _client.GenerateTextAsync(BuildSystemInstruction(context), turns, question.Trim(), ct);
    }

    /// <summary>Maps a failed result to the sentence shown in the chat bubble.</summary>
    public static string ToUserMessage(GeminiResult<string> result) => result.Kind switch
    {
        GeminiErrorKind.NoApiKey => "Gemini API key is not configured. Please go to Settings and enter your API key.",
        GeminiErrorKind.Unauthorized => "Your Gemini API key was rejected. Please check it in Settings.",
        GeminiErrorKind.RateLimited => string.IsNullOrWhiteSpace(result.Error)
            ? "The AI service is busy right now. Please wait a minute and try again."
            : result.Error,
        GeminiErrorKind.Network => "Could not reach the AI service. Check your internet connection and try again.",
        GeminiErrorKind.Cancelled => "The request was cancelled.",
        _ => string.IsNullOrWhiteSpace(result.Error)
            ? "Sorry, no response was received. Please try again."
            : $"Sorry, something went wrong: {result.Error}",
    };

    private static string BuildSystemInstruction(string context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the personal health assistant inside Chartwell, an offline medicine and health-record app used by exactly one person: the user you are talking to. All data below is theirs.");
        sb.AppendLine($"Today's date is {DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({DateTime.Today:dddd}).");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Answer from the health data below. When you use it, say which section or record you relied on (for example \"based on your dose history\" or \"from the 2024-03-15 lab report\").");
        sb.AppendLine("- If the data does not contain what is needed, say so plainly instead of guessing.");
        sb.AppendLine("- Lab results carry the reference range each value was judged against and a flag ([in range], [LOW], [HIGH], [NOT the expected result]). Report those flags as they are; never re-judge a value against a different range, and say when a result is marked as calculated by the app.");
        sb.AppendLine("- Do not diagnose conditions, do not tell the user to start, stop or change a dose, and do not interpret results as a doctor would. You may explain what a term or a reference range means in general and suggest discussing specifics with their doctor.");
        sb.AppendLine("- If the user describes an emergency (chest pain, difficulty breathing, overdose, suicidal thoughts), tell them to contact emergency services immediately before anything else.");
        sb.AppendLine("- Be concise: usually under 150 words. Plain text with simple markdown only: short paragraphs, '-' bullet lists, **bold** for key numbers. No tables, no headings, no code blocks, no links.");
        sb.AppendLine("- Use the same date and time formats as the data (yyyy-MM-dd, 24-hour HH:mm).");
        sb.AppendLine();
        sb.AppendLine("=== USER HEALTH DATA ===");
        sb.AppendLine(context);
        return sb.ToString();
    }
}
