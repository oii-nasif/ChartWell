using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HealthTracker.Services.Ai;

/// <summary>
/// The single place that talks to the Gemini REST API.
///
/// - One static <see cref="HttpClient"/> (60 s timeout) shared by every caller.
/// - The API key is read from <see cref="GeminiApiKeyStore"/> and sent in the <c>x-goog-api-key</c>
///   header, never in the URL (URLs end up in logs and crash reports).
/// - Model name comes from Preferences "gemini_model", defaulting to <see cref="DefaultModel"/>.
/// - 429 and 5xx responses are retried with backoff, then other available models are tried.
/// - Every failure is reported through <see cref="GeminiResult{T}"/>; nothing throws to callers
///   except <see cref="OperationCanceledException"/> is also folded into <see cref="GeminiErrorKind.Cancelled"/>.
/// </summary>
public class GeminiClient
{
    /// <summary>
    /// Google's recommended stable text model as of Oct 2026. Gemini models are retired often (the 2.x
    /// family is being shut down and is unavailable to new keys), so this is only the starting point:
    /// <see cref="ListModelsAsync"/> asks the API what the user's key can actually use, and a call that
    /// hits a retired model switches to an available one automatically.
    /// </summary>
    public const string DefaultModel = "gemini-3.8-flash";
    public const string ModelPrefKey = "gemini_model";

    /// <summary>Shown in Settings when the live list cannot be fetched (no key yet, offline).</summary>
    public static readonly IReadOnlyList<string> FallbackModels =
        ["gemini-3.8-flash", "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite"];

    private const string ApiRoot = "https://generativelanguage.googleapis.com/v1beta/";
    private const string BaseUrl = ApiRoot + "models/";

    private static List<string>? _availableModels;
    private static readonly SemaphoreSlim ModelsLock = new(1, 1);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Options for reading the model's JSON output into our typed result classes.</summary>
    public static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Model used for all calls. Setting null/blank restores the default.</summary>
    public static string Model
    {
        get
        {
            var m = Preferences.Get(ModelPrefKey, DefaultModel);
            if (string.IsNullOrWhiteSpace(m)) return DefaultModel;
            m = m.Trim();
            // Earlier versions defaulted to the now-retired 1.x/2.x models; move those users forward.
            if (IsRetiredGeneration(m))
            {
                Preferences.Remove(ModelPrefKey);
                return DefaultModel;
            }
            return m;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) Preferences.Remove(ModelPrefKey);
            else Preferences.Set(ModelPrefKey, value.Trim());
        }
    }

    private static bool IsRetiredGeneration(string model) =>
        model.StartsWith("gemini-1.", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gemini-2.", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("gemini-pro", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Text-generation models the stored key can use, best first (newest version, Flash before Pro
    /// before Lite). Cached for the process; pass <paramref name="refresh"/> to re-query. Returns
    /// <see cref="FallbackModels"/> when there is no key or the request fails.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _availableModels is { Count: > 0 }) return _availableModels;

        await ModelsLock.WaitAsync(ct);
        try
        {
            if (!refresh && _availableModels is { Count: > 0 }) return _availableModels;

            var apiKey = await GeminiApiKeyStore.GetAsync();
            if (string.IsNullOrWhiteSpace(apiKey)) return FallbackModels;

            var found = new List<(string Name, double Version, int Tier)>();
            string? pageToken = null;
            do
            {
                var url = $"{ApiRoot}models?pageSize=1000" + (pageToken != null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-goog-api-key", apiKey);
                using var response = await Http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return _availableModels ?? (IReadOnlyList<string>)FallbackModels;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name == null) continue;
                        if (name.StartsWith("models/", StringComparison.Ordinal)) name = name["models/".Length..];
                        var supportsText = m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                            methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
                        if (supportsText && IsChatCapable(name)) found.Add((name, ParseVersion(name), Tier(name)));
                    }
                }
                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
            } while (!string.IsNullOrEmpty(pageToken));

            if (found.Count == 0) return FallbackModels;
            _availableModels = found
                .OrderByDescending(x => x.Version)
                .ThenBy(x => x.Tier)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => x.Name)
                .Distinct()
                .ToList();
            return _availableModels;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Gemini model list failed: {ex.Message}");
            return _availableModels ?? (IReadOnlyList<string>)FallbackModels;
        }
        finally
        {
            ModelsLock.Release();
        }
    }

    /// <summary>
    /// Keeps general text/vision models: drops TTS, live/audio, image generation, embeddings, robotics,
    /// transcription, retired generations and dated previews that tend to vanish.
    /// </summary>
    private static bool IsChatCapable(string name)
    {
        if (!name.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) || IsRetiredGeneration(name)) return false;
        string[] excluded = ["tts", "live", "audio", "image", "embedding", "robotics", "transcribe", "translate",
                             "computer-use", "banana", "omni", "exp", "learnlm", "aqa"];
        return !excluded.Any(e => name.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    private static double ParseVersion(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"gemini-(\d+(?:\.\d+)?)");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // Lower is preferred: stable Flash, then Pro, then Lite, then previews.
    private static int Tier(string name)
    {
        var preview = name.Contains("preview", StringComparison.OrdinalIgnoreCase) ? 10 : 0;
        if (name.Contains("lite", StringComparison.OrdinalIgnoreCase)) return 2 + preview;
        if (name.Contains("flash", StringComparison.OrdinalIgnoreCase)) return 0 + preview;
        if (name.Contains("pro", StringComparison.OrdinalIgnoreCase)) return 1 + preview;
        return 3 + preview;
    }

    /// <summary>
    /// Asks the model for JSON matching <paramref name="responseSchema"/> and deserialises it to <typeparamref name="T"/>.
    /// Images (if any) are sent inline before the prompt text so the model reads the actual page.
    /// </summary>
    public async Task<GeminiResult<T>> GenerateJsonAsync<T>(
        string systemInstruction,
        string userPrompt,
        object responseSchema,
        IEnumerable<GeminiImage>? images = null,
        CancellationToken ct = default)
    {
        var parts = new List<object>();
        if (images != null)
        {
            foreach (var img in images)
            {
                parts.Add(new
                {
                    inlineData = new { mimeType = img.MimeType, data = Convert.ToBase64String(img.Data) }
                });
            }
        }
        parts.Add(new { text = userPrompt });

        var model = Model;
        var body = new Dictionary<string, object?>
        {
            ["systemInstruction"] = new { parts = new[] { new { text = systemInstruction } } },
            ["contents"] = new[] { new { role = "user", parts } },
            ["generationConfig"] = BuildGenerationConfig(model, temperature: 0.1, maxOutputTokens: 8192,
                responseMimeType: "application/json", responseSchema: responseSchema),
        };

        var raw = await SendAsync(model, body, ct);
        if (!raw.Succeeded) return raw.As<T>();

        try
        {
            var value = JsonSerializer.Deserialize<T>(StripCodeFences(raw.Value!), ResponseJson);
            if (value == null)
                return GeminiResult<T>.Fail(GeminiErrorKind.BadResponse, "Model returned empty JSON.");
            return GeminiResult<T>.Ok(value);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Gemini JSON parse failed: {ex.Message}\nRaw: {raw.Value}");
            return GeminiResult<T>.Fail(GeminiErrorKind.BadResponse, "Model returned JSON that did not match the expected shape.");
        }
    }

    /// <summary>
    /// Multi-turn text generation. <paramref name="history"/> roles are "user" or "model"
    /// (anything that is not "user" is sent as "model").
    /// </summary>
    public async Task<GeminiResult<string>> GenerateTextAsync(
        string systemInstruction,
        IEnumerable<(string role, string text)> history,
        string userMessage,
        CancellationToken ct = default)
    {
        var contents = new List<object>();
        foreach (var (role, text) in history)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            contents.Add(new
            {
                role = role == "user" ? "user" : "model",
                parts = new[] { new { text } }
            });
        }
        contents.Add(new { role = "user", parts = new[] { new { text = userMessage } } });

        var model = Model;
        var body = new Dictionary<string, object?>
        {
            ["systemInstruction"] = new { parts = new[] { new { text = systemInstruction } } },
            ["contents"] = contents,
            ["generationConfig"] = BuildGenerationConfig(model, temperature: 0.3, maxOutputTokens: 4096,
                responseMimeType: null, responseSchema: null),
        };

        var raw = await SendAsync(model, body, ct);
        return raw.Succeeded ? GeminiResult<string>.Ok(raw.Value!.Trim()) : raw;
    }

    private static Dictionary<string, object?> BuildGenerationConfig(string model, double temperature,
        int maxOutputTokens, string? responseMimeType, object? responseSchema)
    {
        var cfg = new Dictionary<string, object?>
        {
            ["temperature"] = temperature,
            ["maxOutputTokens"] = maxOutputTokens,
            ["responseMimeType"] = responseMimeType,
            ["responseSchema"] = responseSchema,
        };
        // 2.5 models "think" before answering and the thinking tokens come out of maxOutputTokens.
        // Extraction does not benefit from it, so turn it off there; older models reject the field and
        // the 2.5 Pro models reject a zero budget (thinking cannot be disabled on them), so only Flash.
        if (responseSchema != null && model.Contains("2.5", StringComparison.Ordinal)
            && model.Contains("flash", StringComparison.OrdinalIgnoreCase))
            cfg["thinkingConfig"] = new { thinkingBudget = 0 };
        return cfg;
    }

    /// <summary>What went wrong on a failed attempt, so <see cref="SendAsync"/> can decide to switch models.</summary>
    private sealed class SendOutcome
    {
        /// <summary>The model was retired or never existed (404 / "not found").</summary>
        public bool ModelMissing;
        /// <summary>The model is overloaded or its per-model quota is used up (503/500/502/504/429).</summary>
        public bool Overloaded;
    }

    /// <summary>
    /// Sends the request on <paramref name="model"/>, then:
    /// - retired model: switches to the best model the key can use, saves it in <see cref="Model"/>, retries;
    /// - overloaded / per-model quota: tries up to two other models for this request only (the saved
    ///   choice is kept, since overload is usually temporary).
    /// </summary>
    private static async Task<GeminiResult<string>> SendAsync(string model, Dictionary<string, object?> body, CancellationToken ct)
    {
        var outcome = new SendOutcome();
        var result = await SendCoreAsync(model, body, outcome, maxAttempts: 3, ct);
        if (result.Succeeded || (!outcome.ModelMissing && !outcome.Overloaded)) return result;

        var available = await ListModelsAsync(refresh: outcome.ModelMissing, ct);
        var alternatives = available
            .Where(m => !m.Equals(model, StringComparison.OrdinalIgnoreCase))
            .Take(outcome.ModelMissing ? 1 : 2)
            .ToList();

        foreach (var alternative in alternatives)
        {
            if (ct.IsCancellationRequested) break;
            System.Diagnostics.Debug.WriteLine($"Gemini model '{model}' failed ({(outcome.ModelMissing ? "retired" : "overloaded")}); trying '{alternative}'.");
            var next = await SendCoreAsync(alternative, body, new SendOutcome(), maxAttempts: 2, ct);
            if (next.Succeeded)
            {
                if (outcome.ModelMissing) Model = alternative;
                return next;
            }
        }

        if (outcome.Overloaded)
            return GeminiResult<string>.Fail(GeminiErrorKind.RateLimited,
                "Gemini is overloaded right now (Google returned 503). The app retried and tried other models. Please try again in a minute.");
        return result;
    }

    /// <summary>
    /// Posts the request with up to <paramref name="maxAttempts"/> tries on 429/5xx/timeouts (waits 1 s,
    /// then 3 s, or the server's Retry-After up to 10 s) and returns the concatenated text parts.
    /// </summary>
    private static async Task<GeminiResult<string>> SendCoreAsync(string model, Dictionary<string, object?> body,
        SendOutcome outcome, int maxAttempts, CancellationToken ct)
    {
        var apiKey = await GeminiApiKeyStore.GetAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
            return GeminiResult<string>.Fail(GeminiErrorKind.NoApiKey, "Gemini API key is not configured.");

        var url = $"{BaseUrl}{Uri.EscapeDataString(model)}:generateContent";
        var json = JsonSerializer.Serialize(body, RequestJson);
        TimeSpan? serverRetryAfter = null;
        var last = GeminiResult<string>.Fail(GeminiErrorKind.BadResponse, "No attempt was made.");

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var wait = serverRetryAfter ?? TimeSpan.FromSeconds(attempt == 1 ? 1 : 3);
                if (wait > TimeSpan.FromSeconds(10)) wait = TimeSpan.FromSeconds(10);
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { return GeminiResult<string>.Fail(GeminiErrorKind.Cancelled, "Cancelled."); }
            }
            var isLastAttempt = attempt == maxAttempts - 1;

            HttpResponseMessage response;
            string responseText;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("x-goog-api-key", apiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                response = await Http.SendAsync(request, ct);
                responseText = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return GeminiResult<string>.Fail(GeminiErrorKind.Cancelled, "Cancelled.");
            }
            catch (OperationCanceledException)
            {
                // HttpClient timeout surfaces as TaskCanceledException without our token being set.
                last = GeminiResult<string>.Fail(GeminiErrorKind.Network, "The AI service did not respond in time.");
                if (!isLastAttempt) continue;
                return last;
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Gemini network error: {ex.Message}");
                last = GeminiResult<string>.Fail(GeminiErrorKind.Network, "Could not reach the AI service. Check your internet connection.");
                if (!isLastAttempt) continue;
                return last;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Gemini call failed: {ex}");
                return GeminiResult<string>.Fail(GeminiErrorKind.Network, $"Request failed: {ex.GetType().Name}");
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
                {
                    System.Diagnostics.Debug.WriteLine($"Gemini {model} {status}: {Truncate(responseText, 300)}");
                    serverRetryAfter = response.Headers.RetryAfter?.Delta;
                    outcome.Overloaded = true;
                    last = response.StatusCode == HttpStatusCode.TooManyRequests
                        ? GeminiResult<string>.Fail(GeminiErrorKind.RateLimited, "The AI service is busy (rate limited). Try again in a minute.")
                        : GeminiResult<string>.Fail(GeminiErrorKind.RateLimited, $"The AI service is temporarily unavailable ({status}).");
                    if (!isLastAttempt) continue;
                    return last;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return GeminiResult<string>.Fail(GeminiErrorKind.Unauthorized, "The Gemini API key was rejected. Check it in Settings.");

                if (!response.IsSuccessStatusCode)
                {
                    var apiMessage = ExtractApiErrorMessage(responseText);
                    // A retired or unknown model comes back as 404, or 400 "... is not found / not supported".
                    if (response.StatusCode == HttpStatusCode.NotFound ||
                        (apiMessage != null && (apiMessage.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                                                apiMessage.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ||
                                                apiMessage.Contains("not supported for generateContent", StringComparison.OrdinalIgnoreCase))))
                        outcome.ModelMissing = true;
                    System.Diagnostics.Debug.WriteLine($"Gemini {model} {status}: {Truncate(responseText, 500)}");
                    // Gemini reports a bad key as 400 with reason API_KEY_INVALID.
                    if (apiMessage != null && apiMessage.Contains("API key", StringComparison.OrdinalIgnoreCase))
                        return GeminiResult<string>.Fail(GeminiErrorKind.Unauthorized, "The Gemini API key was rejected. Check it in Settings.");
                    return GeminiResult<string>.Fail(GeminiErrorKind.BadResponse,
                        apiMessage != null ? $"AI service error: {apiMessage}" : $"AI service error ({status}).");
                }

                return ExtractText(responseText);
            }
        }

        return last;
    }

    /// <summary>Pulls the text out of a successful generateContent payload, honouring block/finish reasons.</summary>
    private static GeminiResult<string> ExtractText(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            if (root.TryGetProperty("promptFeedback", out var feedback) &&
                feedback.TryGetProperty("blockReason", out var blockReason))
            {
                return GeminiResult<string>.Fail(GeminiErrorKind.BadResponse,
                    $"The AI service declined this request ({blockReason.GetString()}).");
            }

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                return GeminiResult<string>.Fail(GeminiErrorKind.BadResponse, "The AI service returned no answer.");

            var candidate = candidates[0];
            var sb = new StringBuilder();
            if (candidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    // Skip "thought" parts if a model ever returns them.
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                    if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                }
            }

            var text = sb.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                var finish = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;
                return GeminiResult<string>.Fail(GeminiErrorKind.BadResponse,
                    finish != null ? $"The AI service returned an empty answer ({finish})." : "The AI service returned an empty answer.");
            }
            return GeminiResult<string>.Ok(text);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Gemini response unparsable: {ex.Message}\n{Truncate(responseText, 500)}");
            return GeminiResult<string>.Fail(GeminiErrorKind.BadResponse, "The AI service returned an unreadable response.");
        }
    }

    private static string? ExtractApiErrorMessage(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Structured output should be bare JSON, but be lenient about ```json fences.</summary>
    private static string StripCodeFences(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        else if (text.StartsWith("```", StringComparison.Ordinal)) text = text[3..];
        if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
        return text.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
