namespace HealthTracker.Constants;

/// <summary>
/// Settings for "Cloud backup to the user's own Google Drive".
///
/// The app never talks to a HealthTracker server: it signs the user in with Google (OAuth 2.0 +
/// PKCE, no client secret, as required for Android/native clients) and stores a single backup
/// ZIP in the hidden Drive "appDataFolder" space that only this app can see.
///
/// The client id is set once per app (not per user): everyone who installs a build signs in with
/// their own Google account against the same client. Full walkthrough: docs/CLOUD_SETUP.md.
///
/// An Android OAuth client is bound to one package name + one signing-certificate SHA-1, so debug
/// and release builds need separate clients:
///  - Debug builds deployed from Visual Studio are signed with
///      %LOCALAPPDATA%\Xamarin\Mono for Android\debug.keystore   (NOT %USERPROFILE%\.android\debug.keystore)
///  - Release builds are signed with your release keystore, or, when distributed through Google
///    Play, re-signed with the Play App Signing key (SHA-1 in Play Console → App integrity).
/// Each client must have "Enable custom URI scheme" ticked under Advanced settings (off by default
/// for new Android clients); the redirect below is a custom scheme.
///
/// Paste the part of each client id before ".apps.googleusercontent.com" into
/// <see cref="DebugClientIdPrefix"/> / <see cref="ReleaseClientIdPrefix"/>. Both values are
/// compile-time constants because <see cref="RedirectScheme"/> is used in an [IntentFilter]
/// attribute on Platforms/Android/WebAuthenticatorCallbackActivity.cs.
///
/// While the active configuration's prefix is the placeholder, <c>GoogleAuthService.IsConfigured</c>
/// is false and the Settings card reports cloud backup as not configured.
///
/// Consent-screen status matters: while it is "Testing", only listed test users can sign in and
/// Google expires refresh tokens after 7 days (drive.appdata is beyond basic profile scopes), so
/// automatic backup silently stops until the user signs in again. Publish it "In production".
/// </summary>
public static class CloudConstants
{
    private const string ClientIdPlaceholder = "REPLACE_WITH_ANDROID_OAUTH_CLIENT_ID";

    /// <summary>Client id prefix of the Android OAuth client registered with the debug keystore SHA-1.</summary>
    private const string DebugClientIdPrefix = ClientIdPlaceholder;

    /// <summary>Client id prefix of the Android OAuth client registered with the release (or Play App Signing) SHA-1.</summary>
    private const string ReleaseClientIdPrefix = ClientIdPlaceholder;

#if DEBUG
    private const string ClientIdPrefix = DebugClientIdPrefix;
#else
    private const string ClientIdPrefix = ReleaseClientIdPrefix;
#endif

    /// <summary>Android OAuth 2.0 client id for the active build configuration.</summary>
    public const string GoogleClientId = ClientIdPrefix + ".apps.googleusercontent.com";

    /// <summary>
    /// Custom URL scheme Google redirects back to. By Google's convention it is the client id
    /// reversed: "com.googleusercontent.apps.&lt;id-prefix&gt;". Must stay a const (used in an attribute).
    /// </summary>
    public const string RedirectScheme = "com.googleusercontent.apps." + ClientIdPrefix;

    /// <summary>Full redirect URI registered with the WebAuthenticator callback activity.</summary>
    public const string RedirectUri = RedirectScheme + ":/oauth2redirect";

    /// <summary>
    /// drive.appdata: read/write only the app's hidden folder in the user's Drive (does not grant
    /// access to any other Drive file). openid + email: lets us show which account is connected.
    /// </summary>
    public const string Scopes = "https://www.googleapis.com/auth/drive.appdata openid email";

    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>Name of the single backup file kept in the Drive appDataFolder.</summary>
    public const string BackupFileName = "healthtracker-backup.zip";

    /// <summary>Automatic backups run at most once per this interval (see GoogleDriveSyncService.AutoSyncIfDueAsync).</summary>
    public const int AutoBackupIntervalHours = 24;

    /// <summary>True when a real client id has been pasted in for the active configuration (see class remarks).</summary>
    public static bool IsClientIdConfigured => ClientIdPrefix != ClientIdPlaceholder;

    // Preference keys (MAUI Preferences)
    public const string PrefLastBackupUtc = "cloud_last_backup_utc";
    public const string PrefAutoBackup = "cloud_auto_backup";
    public const string PrefBackupFileId = "cloud_backup_file_id";

    // SecureStorage keys
    public const string SecureAccessToken = "google_access_token";
    public const string SecureRefreshToken = "google_refresh_token";
    public const string SecureTokenExpiryUtc = "google_token_expiry_utc";
    public const string SecureAccountEmail = "google_account_email";
}
