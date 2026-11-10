namespace HealthTracker.Services.Ai;

/// <summary>
/// Holds the user's Gemini API key in <see cref="SecureStorage"/> (key "gemini_api_key").
///
/// Earlier builds kept the key in plain <see cref="Preferences"/> under the same name. The first
/// call to any member migrates that value into SecureStorage and removes it from Preferences.
///
/// SecureStorage is async-only, so a small Preferences flag ("gemini_api_key_configured") mirrors
/// whether a key exists. That flag never contains the key; it lets the synchronous
/// <see cref="IsConfigured"/> used by the current ViewModels answer correctly on a cold start.
/// </summary>
public static class GeminiApiKeyStore
{
    public const string SecureKey = "gemini_api_key";
    private const string LegacyPrefsKey = "gemini_api_key";
    private const string ConfiguredFlagKey = "gemini_api_key_configured";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _loaded;
    private static string? _cached;

    /// <summary>Synchronous "is a key saved?" backed by the Preferences flag (or the cache once loaded).</summary>
    public static bool IsConfigured
    {
        get
        {
            if (_loaded) return !string.IsNullOrWhiteSpace(_cached);
            // Not loaded yet: the flag is authoritative for new installs; a legacy plain-text key
            // (pre-migration) also counts so the UI does not briefly claim AI is off.
            return Preferences.Get(ConfiguredFlagKey, false)
                   || !string.IsNullOrWhiteSpace(Preferences.Get(LegacyPrefsKey, null));
        }
    }

    public static async Task<bool> HasKeyAsync()
        => !string.IsNullOrWhiteSpace(await GetAsync());

    public static async Task<string?> GetAsync()
    {
        await EnsureLoadedAsync();
        return _cached;
    }

    public static async Task SaveAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { await ClearAsync(); return; }
        key = key.Trim();
        await Gate.WaitAsync();
        try
        {
            await SecureStorage.Default.SetAsync(SecureKey, key);
            Preferences.Set(ConfiguredFlagKey, true);
            Preferences.Remove(LegacyPrefsKey);
            _cached = key;
            _loaded = true;
        }
        finally { Gate.Release(); }
    }

    public static async Task ClearAsync()
    {
        await Gate.WaitAsync();
        try
        {
            SecureStorage.Default.Remove(SecureKey);
            Preferences.Remove(ConfiguredFlagKey);
            Preferences.Remove(LegacyPrefsKey);
            _cached = null;
            _loaded = true;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Synchronous read for legacy callers. On Android MAUI's SecureStorage completes synchronously
    /// (EncryptedSharedPreferences), so blocking here does not deadlock; prefer <see cref="GetAsync"/>.
    /// </summary>
    public static string? GetBlocking()
    {
        if (_loaded) return _cached;
        return GetAsync().GetAwaiter().GetResult();
    }

    private static async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await Gate.WaitAsync();
        try
        {
            if (_loaded) return;

            string? secure = null;
            try { secure = await SecureStorage.Default.GetAsync(SecureKey); }
            catch (Exception ex)
            {
                // Keystore corruption after a backup/restore can throw; treat as "no key" rather than crash.
                System.Diagnostics.Debug.WriteLine($"SecureStorage read failed: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(secure))
            {
                // One-time migration from the old plain-text Preferences slot.
                var legacy = Preferences.Get(LegacyPrefsKey, null);
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    try
                    {
                        await SecureStorage.Default.SetAsync(SecureKey, legacy.Trim());
                        secure = legacy.Trim();
                        Preferences.Remove(LegacyPrefsKey);
                    }
                    catch (Exception ex)
                    {
                        // Keep the legacy value usable this session; migration retries next launch.
                        System.Diagnostics.Debug.WriteLine($"API key migration failed: {ex.Message}");
                        secure = legacy.Trim();
                    }
                }
            }

            _cached = string.IsNullOrWhiteSpace(secure) ? null : secure;
            Preferences.Set(ConfiguredFlagKey, _cached != null);
            _loaded = true;
        }
        finally { Gate.Release(); }
    }
}
