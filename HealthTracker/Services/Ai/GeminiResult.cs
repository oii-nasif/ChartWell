namespace HealthTracker.Services.Ai;

/// <summary>
/// Why a Gemini call did not produce a usable value. <see cref="None"/> means success.
/// The UI maps these to user-facing messages; services never return sentinel strings.
/// </summary>
public enum GeminiErrorKind
{
    None,
    /// <summary>No API key is stored. Nothing was sent.</summary>
    NoApiKey,
    /// <summary>DNS/socket failure, timeout, or no connectivity.</summary>
    Network,
    /// <summary>The key was rejected (HTTP 400 "API key not valid", 401, 403).</summary>
    Unauthorized,
    /// <summary>HTTP 429 after the retry.</summary>
    RateLimited,
    /// <summary>Server error, blocked prompt, empty candidate, or a payload we could not parse.</summary>
    BadResponse,
    /// <summary>The caller's <see cref="CancellationToken"/> fired.</summary>
    Cancelled
}

/// <summary>Typed outcome of a Gemini call. Check <see cref="Succeeded"/> before using <see cref="Value"/>.</summary>
public sealed record GeminiResult<T>(bool Succeeded, T? Value, string? Error, GeminiErrorKind Kind)
{
    public static GeminiResult<T> Ok(T value) => new(true, value, null, GeminiErrorKind.None);

    public static GeminiResult<T> Fail(GeminiErrorKind kind, string error) => new(false, default, error, kind);

    /// <summary>Re-wraps a failure as a different value type (the error details are kept).</summary>
    public GeminiResult<TOther> As<TOther>() => new(false, default, Error, Kind);
}

/// <summary>An image sent inline to Gemini. <paramref name="MimeType"/> is "image/jpeg" or "image/png".</summary>
public sealed record GeminiImage(byte[] Data, string MimeType = "image/jpeg")
{
    /// <summary>Sniffs PNG vs JPEG from the magic bytes; anything unrecognised is sent as JPEG.</summary>
    public static GeminiImage FromBytes(byte[] data)
    {
        var isPng = data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
        return new GeminiImage(data, isPng ? "image/png" : "image/jpeg");
    }
}
