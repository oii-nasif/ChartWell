using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HealthTracker.Constants;

namespace HealthTracker.Services.Cloud;

public interface IGoogleAuthService
{
    /// <summary>True when a real OAuth client id has been compiled in (see CloudConstants).</summary>
    bool IsConfigured { get; }

    /// <summary>True when a refresh token is stored, i.e. the user has connected a Google account.</summary>
    bool IsSignedIn { get; }

    /// <summary>Email of the connected account, or null when signed out / unknown.</summary>
    string? AccountEmail { get; }

    /// <summary>
    /// Opens the Google sign-in page in the browser and stores the resulting tokens.
    /// Returns false (never throws) when the user cancels, the device is offline or the exchange fails.
    /// </summary>
    Task<bool> SignInAsync();

    /// <summary>Returns a valid access token, silently refreshing an expired one. Null when signed out or the refresh fails.</summary>
    Task<string?> GetAccessTokenAsync();

    /// <summary>Revokes the Google grant (best effort) and clears all locally stored tokens.</summary>
    Task SignOutAsync();
}

/// <summary>
/// Google OAuth 2.0 "installed app" flow with PKCE, driven by MAUI WebAuthenticator. Android OAuth
/// clients have no client secret, so the token endpoint is called with client_id + code_verifier only.
/// Tokens live in SecureStorage; the in-memory copies below are just a cache so IsSignedIn/AccountEmail
/// can be plain synchronous properties for data binding.
/// </summary>
public class GoogleAuthService : IGoogleAuthService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Refresh the access token this long before Google says it expires, to avoid racing the clock.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly object _loadLock = new();

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime? _expiryUtc;
    private string? _email;
    private bool _loaded;

    public bool IsConfigured => CloudConstants.IsClientIdConfigured;

    public bool IsSignedIn
    {
        get
        {
            EnsureLoaded();
            return !string.IsNullOrEmpty(_refreshToken);
        }
    }

    public string? AccountEmail
    {
        get
        {
            EnsureLoaded();
            return _email;
        }
    }

    public async Task<bool> SignInAsync()
    {
        if (!IsConfigured)
        {
            System.Diagnostics.Debug.WriteLine("GoogleAuth: client id not configured");
            return false;
        }

        // PKCE: a random verifier, its SHA-256 hash (base64url) goes in the auth request.
        var codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

        var authUrl = CloudConstants.AuthorizationEndpoint +
            "?response_type=code" +
            "&client_id=" + Uri.EscapeDataString(CloudConstants.GoogleClientId) +
            "&redirect_uri=" + Uri.EscapeDataString(CloudConstants.RedirectUri) +
            "&scope=" + Uri.EscapeDataString(CloudConstants.Scopes) +
            "&code_challenge=" + codeChallenge +
            "&code_challenge_method=S256" +
            "&state=" + state +
            // offline + consent: guarantees Google returns a refresh_token (it only does so on the
            // first consent otherwise, and we may have revoked a previous one on sign-out).
            "&access_type=offline" +
            "&prompt=consent";

        WebAuthenticatorResult result;
        try
        {
            result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = new Uri(authUrl),
                CallbackUrl = new Uri(CloudConstants.RedirectUri),
                PrefersEphemeralWebBrowserSession = false
            });
        }
        catch (TaskCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("GoogleAuth: user cancelled sign-in");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GoogleAuth: browser flow failed: {ex}");
            return false;
        }

        if (result.Properties.TryGetValue("error", out var error))
        {
            System.Diagnostics.Debug.WriteLine($"GoogleAuth: authorization error: {error}");
            return false;
        }
        if (!result.Properties.TryGetValue("state", out var returnedState) || returnedState != state)
        {
            System.Diagnostics.Debug.WriteLine("GoogleAuth: state mismatch, rejecting response");
            return false;
        }
        if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
        {
            System.Diagnostics.Debug.WriteLine("GoogleAuth: no authorization code in redirect");
            return false;
        }

        try
        {
            var tokens = await PostTokenRequestAsync(new Dictionary<string, string>
            {
                ["client_id"] = CloudConstants.GoogleClientId,
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = CloudConstants.RedirectUri
            });
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
                return false;

            // Google only returns a refresh_token on consent; with prompt=consent we always get one,
            // but fall back to any previously stored token rather than ending up half signed-in.
            var refreshToken = tokens.RefreshToken ?? _refreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                System.Diagnostics.Debug.WriteLine("GoogleAuth: no refresh_token returned");
                return false;
            }

            var email = tokens.IdToken is null ? null : TryReadEmailFromIdToken(tokens.IdToken);
            await StoreTokensAsync(tokens.AccessToken, refreshToken, tokens.ExpiresIn, email ?? _email);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GoogleAuth: code exchange failed: {ex}");
            return false;
        }
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(_refreshToken))
            return null;

        // Serialize so two concurrent callers (e.g. auto-sync and a manual backup) don't both refresh.
        await _tokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrEmpty(_accessToken) && _expiryUtc is DateTime exp && DateTime.UtcNow < exp - ExpirySkew)
                return _accessToken;

            var tokens = await PostTokenRequestAsync(new Dictionary<string, string>
            {
                ["client_id"] = CloudConstants.GoogleClientId,
                ["refresh_token"] = _refreshToken,
                ["grant_type"] = "refresh_token"
            });
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
                return null;

            await StoreTokensAsync(tokens.AccessToken, tokens.RefreshToken ?? _refreshToken, tokens.ExpiresIn, _email);
            return _accessToken;
        }
        catch (InvalidGrantException)
        {
            // The refresh token was revoked or expired (user removed the app's access, password change,
            // long inactivity). It will never work again, so drop it: IsSignedIn becomes false and the
            // Settings page offers "Sign in" instead of failing silently on every sync.
            System.Diagnostics.Debug.WriteLine("GoogleAuth: refresh token rejected (invalid_grant); signing out locally");
            ClearLocalTokens();
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GoogleAuth: token refresh failed: {ex}");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task SignOutAsync()
    {
        EnsureLoaded();
        var tokenToRevoke = _refreshToken ?? _accessToken;

        // Clear locally first so the UI is consistent even if the network call below fails.
        ClearLocalTokens();

        if (string.IsNullOrEmpty(tokenToRevoke))
            return;

        try
        {
            // Revoking the refresh token also invalidates every access token issued from it.
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokenToRevoke });
            using var response = await Http.PostAsync(CloudConstants.RevokeEndpoint, content);
            System.Diagnostics.Debug.WriteLine($"GoogleAuth: revoke returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GoogleAuth: revoke failed (ignored): {ex.Message}");
        }
    }

    // ---- internals -------------------------------------------------------------------------

    /// <summary>Forgets every stored token and the cached Drive file id; the account must be connected again.</summary>
    private void ClearLocalTokens()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiryUtc = null;
        _email = null;
        SecureStorage.Default.Remove(CloudConstants.SecureAccessToken);
        SecureStorage.Default.Remove(CloudConstants.SecureRefreshToken);
        SecureStorage.Default.Remove(CloudConstants.SecureTokenExpiryUtc);
        SecureStorage.Default.Remove(CloudConstants.SecureAccountEmail);
        Preferences.Default.Remove(CloudConstants.PrefBackupFileId);
    }

    private sealed class TokenResponse
    {
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public string? IdToken { get; init; }
        public int ExpiresIn { get; init; }
    }

    /// <summary>The token endpoint answered 400 "invalid_grant": the code/refresh token is permanently unusable.</summary>
    private sealed class InvalidGrantException : Exception
    {
        public InvalidGrantException(string body) : base("invalid_grant: " + body) { }
    }

    private static async Task<TokenResponse?> PostTokenRequestAsync(Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, CloudConstants.TokenEndpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // Body is e.g. {"error":"invalid_grant","error_description":"Token has been expired or revoked."}
            System.Diagnostics.Debug.WriteLine($"GoogleAuth: token endpoint {(int)response.StatusCode}: {body}");
            if ((int)response.StatusCode == 400 && IsInvalidGrant(body))
                throw new InvalidGrantException(body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new TokenResponse
        {
            AccessToken = GetString(root, "access_token"),
            RefreshToken = GetString(root, "refresh_token"),
            IdToken = GetString(root, "id_token"),
            ExpiresIn = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 3600
        };
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsInvalidGrant(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return GetString(doc.RootElement, "error") == "invalid_grant";
        }
        catch (JsonException)
        {
            return body.Contains("\"invalid_grant\"", StringComparison.Ordinal);
        }
    }

    private async Task StoreTokensAsync(string accessToken, string refreshToken, int expiresInSeconds, string? email)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _expiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresInSeconds));
        _email = email;

        await SecureStorage.Default.SetAsync(CloudConstants.SecureAccessToken, accessToken);
        await SecureStorage.Default.SetAsync(CloudConstants.SecureRefreshToken, refreshToken);
        await SecureStorage.Default.SetAsync(CloudConstants.SecureTokenExpiryUtc, _expiryUtc.Value.ToString("o", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(email))
            await SecureStorage.Default.SetAsync(CloudConstants.SecureAccountEmail, email);
    }

    /// <summary>
    /// Loads stored tokens into memory once. SecureStorage is async-only, so this blocks on first
    /// access; the values are tiny and it only happens once per process.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_loadLock)
        {
            if (_loaded) return;
            try
            {
                _accessToken = SecureStorage.Default.GetAsync(CloudConstants.SecureAccessToken).GetAwaiter().GetResult();
                _refreshToken = SecureStorage.Default.GetAsync(CloudConstants.SecureRefreshToken).GetAwaiter().GetResult();
                _email = SecureStorage.Default.GetAsync(CloudConstants.SecureAccountEmail).GetAwaiter().GetResult();
                var expiry = SecureStorage.Default.GetAsync(CloudConstants.SecureTokenExpiryUtc).GetAwaiter().GetResult();
                _expiryUtc = DateTime.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                    ? dt.ToUniversalTime()
                    : null;
            }
            catch (Exception ex)
            {
                // SecureStorage can throw if the keystore was reset (e.g. after a device restore);
                // treat as signed out rather than crashing.
                System.Diagnostics.Debug.WriteLine($"GoogleAuth: failed to read SecureStorage: {ex.Message}");
                _accessToken = _refreshToken = _email = null;
                _expiryUtc = null;
            }
            _loaded = true;
        }
    }

    /// <summary>
    /// Reads the "email" claim from an OpenID Connect id_token without a JWT library. The token was
    /// just received over TLS directly from Google's token endpoint, so signature verification is
    /// not needed for the purely cosmetic purpose of showing the account name.
    /// </summary>
    private static string? TryReadEmailFromIdToken(string idToken)
    {
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            return GetString(doc.RootElement, "email");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GoogleAuth: could not decode id_token: {ex.Message}");
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
