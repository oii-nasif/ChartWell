using System.Globalization;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using HealthTracker.Constants;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace HealthTracker.Services.Cloud;

/// <summary>Outcome of a cloud operation. <see cref="Skipped"/> is true when nothing needed doing (auto-sync not due, offline, ...).</summary>
public record SyncResult(bool Succeeded, string Message, bool Skipped = false);

/// <summary>Metadata of the backup currently stored in the cloud.</summary>
public record CloudBackupInfo(DateTime ModifiedUtc, long SizeBytes, string? Device, string? AppVersion);

public interface ICloudSyncService
{
    /// <summary>When the last successful upload from this device finished, or null if never.</summary>
    DateTime? LastBackupUtc { get; }

    /// <summary>Whether <see cref="AutoSyncIfDueAsync"/> is allowed to upload. Persisted in Preferences, default true.</summary>
    bool AutoBackupEnabled { get; set; }

    /// <summary>Metadata of the backup in Drive, null when there is none or we are signed out / offline.</summary>
    Task<CloudBackupInfo?> GetLatestBackupInfoAsync();

    /// <summary>Creates a fresh backup ZIP and uploads it, replacing the previous one. Progress is 0..1.</summary>
    Task<SyncResult> BackupNowAsync(IProgress<double>? progress = null);

    /// <summary>Downloads the backup from Drive and restores it. See BackupService.RestoreFromZipAsync for what the caller must do afterwards.</summary>
    Task<SyncResult> RestoreLatestAsync(IProgress<double>? progress = null);

    /// <summary>
    /// Uploads a backup if signed in, auto-backup is on, online, a manual backup or restore has already
    /// happened on this device, and the last one is older than 24h. See GoogleDriveSyncService for the
    /// fresh-install guards.
    /// </summary>
    Task<SyncResult> AutoSyncIfDueAsync();
}

/// <summary>
/// Keeps exactly one backup ZIP (CloudConstants.BackupFileName) in the user's Google Drive
/// appDataFolder, a hidden per-app space the user cannot see in the Drive UI but that is deleted
/// when they remove the app's access. All calls return a SyncResult instead of throwing.
/// </summary>
public class GoogleDriveSyncService : ICloudSyncService
{
    private const string AppDataSpace = "appDataFolder";
    private const string ZipMimeType = "application/zip";
    private const string BusyMessage = "Another backup or restore is already running.";

    /// <summary>
    /// A cloud backup at least this large plainly holds real data; an automatic upload of an empty local
    /// database must not replace it (an empty DB plus manifest zips to a few KB).
    /// </summary>
    private const long MeaningfulCloudBackupBytes = 64 * 1024;

    private readonly IGoogleAuthService _auth;
    private readonly BackupService _backupService;

    /// <summary>Shared with BackupService so a local restore and a cloud upload cannot interleave.</summary>
    private static SemaphoreSlim OpLock => BackupService.OperationLock;

    public GoogleDriveSyncService(IGoogleAuthService auth, BackupService backupService)
    {
        _auth = auth;
        _backupService = backupService;
    }

    public DateTime? LastBackupUtc
    {
        get
        {
            var raw = Preferences.Default.Get<string?>(CloudConstants.PrefLastBackupUtc, null);
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToUniversalTime()
                : null;
        }
        private set
        {
            if (value is null)
                Preferences.Default.Remove(CloudConstants.PrefLastBackupUtc);
            else
                Preferences.Default.Set(CloudConstants.PrefLastBackupUtc, value.Value.ToString("o", CultureInfo.InvariantCulture));
        }
    }

    public bool AutoBackupEnabled
    {
        get => Preferences.Default.Get(CloudConstants.PrefAutoBackup, true);
        set => Preferences.Default.Set(CloudConstants.PrefAutoBackup, value);
    }

    public async Task<CloudBackupInfo?> GetLatestBackupInfoAsync()
    {
        try
        {
            using var drive = await CreateDriveServiceAsync();
            if (drive is null) return null;

            var file = await FindBackupFileAsync(drive);
            if (file is null) return null;

            var modified = DateTime.TryParse(file.ModifiedTimeRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToUniversalTime()
                : DateTime.MinValue;
            string? device = null, appVersion = null;
            file.AppProperties?.TryGetValue("device", out device);
            file.AppProperties?.TryGetValue("appVersion", out appVersion);

            return new CloudBackupInfo(modified, file.Size ?? 0, device, appVersion);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CloudSync: GetLatestBackupInfo failed: {ex.Message}");
            return null;
        }
    }

    public Task<SyncResult> BackupNowAsync(IProgress<double>? progress = null) => BackupAsync(progress, fromAutoSync: false);

    /// <summary>
    /// Creates and uploads a backup. When <paramref name="fromAutoSync"/> is true the upload is skipped if
    /// the local database is empty while the cloud file is larger than <see cref="MeaningfulCloudBackupBytes"/>:
    /// that is a fresh install that has not restored yet, and uploading would destroy the only copy.
    /// </summary>
    private async Task<SyncResult> BackupAsync(IProgress<double>? progress, bool fromAutoSync)
    {
        if (!await OpLock.WaitAsync(0))
            return new SyncResult(false, BusyMessage, Skipped: true);
        try
        {
            if (!_auth.IsSignedIn)
                return new SyncResult(false, "Not signed in to Google.");
            if (!IsOnline())
                return new SyncResult(false, "No internet connection.");

            using var drive = await CreateDriveServiceAsync();
            if (drive is null)
                return new SyncResult(false, "Google sign-in has expired. Please sign in again.");

            string? zipPath = null;
            try
            {
                progress?.Report(0);
                var existing = await FindBackupFileAsync(drive);

                if (fromAutoSync && (existing?.Size ?? 0) > MeaningfulCloudBackupBytes
                    && await _backupService.IsLocalDatabaseEmptyAsync())
                {
                    return new SyncResult(true, "Skipped: this device has no data yet but Google Drive holds a backup. Restore it or back up manually.", Skipped: true);
                }

                zipPath = await _backupService.CreateBackupZipAsync(
                    Path.Combine(FileSystem.CacheDirectory, "Backups", "cloud-upload-" + Guid.NewGuid().ToString("N") + ".zip"));

                var metadata = new DriveFile
                {
                    Name = CloudConstants.BackupFileName,
                    AppProperties = new Dictionary<string, string>
                    {
                        ["device"] = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim(),
                        ["appVersion"] = AppInfo.Current.VersionString,
                        ["createdUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    }
                };

                using var stream = File.OpenRead(zipPath);
                var total = (double)Math.Max(1, stream.Length);

                // Both Create and Update are resumable uploads (chunked, retried by the client library).
                ResumableUpload<DriveFile, DriveFile> upload;
                if (existing is null)
                {
                    metadata.Parents = new List<string> { AppDataSpace };
                    var create = drive.Files.Create(metadata, stream, ZipMimeType);
                    create.Fields = "id, modifiedTime";
                    upload = create;
                }
                else
                {
                    // Update must not carry Parents; the file already lives in appDataFolder.
                    var update = drive.Files.Update(metadata, existing.Id, stream, ZipMimeType);
                    update.Fields = "id, modifiedTime";
                    upload = update;
                }
                upload.ProgressChanged += p => progress?.Report(Math.Min(1.0, p.BytesSent / total));

                var uploadResult = await upload.UploadAsync();
                if (uploadResult.Status != UploadStatus.Completed)
                {
                    System.Diagnostics.Debug.WriteLine($"CloudSync: upload failed ({uploadResult.Status}): {uploadResult.Exception}");
                    return new SyncResult(false, "Upload failed: " + Humanize(uploadResult.Exception));
                }

                if (upload.ResponseBody?.Id is string id)
                    Preferences.Default.Set(CloudConstants.PrefBackupFileId, id);

                progress?.Report(1);
                LastBackupUtc = DateTime.UtcNow;
                var sizeMb = stream.Length / (1024.0 * 1024.0);
                return new SyncResult(true, $"Backed up to Google Drive ({sizeMb:0.#} MB).");
            }
            finally
            {
                if (zipPath is not null)
                {
                    try { File.Delete(zipPath); } catch { /* cache, best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CloudSync: BackupNow failed: {ex}");
            return new SyncResult(false, "Backup failed: " + Humanize(ex));
        }
        finally
        {
            OpLock.Release();
        }
    }

    public async Task<SyncResult> RestoreLatestAsync(IProgress<double>? progress = null)
    {
        if (!await OpLock.WaitAsync(0))
            return new SyncResult(false, BusyMessage, Skipped: true);
        try
        {
            if (!_auth.IsSignedIn)
                return new SyncResult(false, "Not signed in to Google.");
            if (!IsOnline())
                return new SyncResult(false, "No internet connection.");

            using var drive = await CreateDriveServiceAsync();
            if (drive is null)
                return new SyncResult(false, "Google sign-in has expired. Please sign in again.");

            var existing = await FindBackupFileAsync(drive);
            if (existing is null)
                return new SyncResult(false, "No backup found in your Google Drive.");

            var downloadPath = Path.Combine(FileSystem.CacheDirectory, "Backups", "cloud-download-" + Guid.NewGuid().ToString("N") + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);
            try
            {
                var total = (double)Math.Max(1, existing.Size ?? 0);
                var get = drive.Files.Get(existing.Id);
                get.MediaDownloader.ProgressChanged += p => progress?.Report(Math.Min(1.0, p.BytesDownloaded / total));

                IDownloadProgress downloadResult;
                using (var dest = File.Create(downloadPath))
                    downloadResult = await get.DownloadAsync(dest);

                if (downloadResult.Status != DownloadStatus.Completed)
                {
                    System.Diagnostics.Debug.WriteLine($"CloudSync: download failed: {downloadResult.Exception}");
                    return new SyncResult(false, "Download failed: " + Humanize(downloadResult.Exception));
                }
                progress?.Report(1);

                // We already hold the operation lock; the plain RestoreFromZipAsync would report "busy".
                var restore = await _backupService.RestoreFromZipHoldingLockAsync(downloadPath);
                if (restore.Succeeded)
                {
                    // Local data now matches the cloud, which also unlocks AutoSyncIfDueAsync on this device.
                    LastBackupUtc = DateTime.UtcNow;
                }
                return new SyncResult(restore.Succeeded, restore.Message);
            }
            finally
            {
                try { File.Delete(downloadPath); } catch { /* cache, best effort */ }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CloudSync: RestoreLatest failed: {ex}");
            return new SyncResult(false, "Restore failed: " + Humanize(ex));
        }
        finally
        {
            OpLock.Release();
        }
    }

    /// <summary>
    /// Throttled background upload. Rules, in order: signed in and configured; auto-backup enabled;
    /// <see cref="LastBackupUtc"/> is set on THIS device, i.e. the user has done a manual backup or a
    /// restore here (a freshly installed, signed-in app whose empty database would otherwise overwrite
    /// the cloud copy before the user taps Restore never auto-uploads); last upload older than 24h;
    /// online. As a second guard <see cref="BackupAsync"/> skips when the local database is empty but
    /// the cloud file is clearly not.
    /// </summary>
    public async Task<SyncResult> AutoSyncIfDueAsync()
    {
        try
        {
            if (!_auth.IsConfigured || !_auth.IsSignedIn)
                return new SyncResult(true, "Not signed in.", Skipped: true);
            if (!AutoBackupEnabled)
                return new SyncResult(true, "Automatic backup is off.", Skipped: true);

            var last = LastBackupUtc;
            if (last is null)
                return new SyncResult(true, "No backup or restore has been done on this device yet; back up manually first.", Skipped: true);
            if (DateTime.UtcNow - last.Value < TimeSpan.FromHours(CloudConstants.AutoBackupIntervalHours))
                return new SyncResult(true, "Backup is up to date.", Skipped: true);

            if (!IsOnline())
                return new SyncResult(true, "Offline; will back up later.", Skipped: true);

            return await BackupAsync(null, fromAutoSync: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CloudSync: AutoSync failed: {ex}");
            return new SyncResult(false, "Automatic backup failed: " + Humanize(ex));
        }
    }

    // ---- internals -------------------------------------------------------------------------

    private static bool IsOnline()
    {
        try { return Connectivity.Current.NetworkAccess == NetworkAccess.Internet; }
        catch { return true; } // if the platform check itself fails, let the request decide
    }

    /// <summary>Returns a DriveService bound to a fresh access token, or null when signed out / refresh failed.</summary>
    private async Task<DriveService?> CreateDriveServiceAsync()
    {
        var token = await _auth.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            return null;

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = GoogleCredential.FromAccessToken(token),
            ApplicationName = "Chartwell"
        });
    }

    /// <summary>Finds the newest file named CloudConstants.BackupFileName in appDataFolder.</summary>
    private static async Task<DriveFile?> FindBackupFileAsync(DriveService drive)
    {
        var list = drive.Files.List();
        list.Spaces = AppDataSpace;
        list.Q = $"name = '{CloudConstants.BackupFileName}' and trashed = false";
        list.Fields = "files(id, name, modifiedTime, size, appProperties)";
        list.OrderBy = "modifiedTime desc";
        list.PageSize = 10;

        var result = await list.ExecuteAsync();
        var files = result.Files;
        if (files is null || files.Count == 0)
            return null;

        // Clean up duplicates left by an interrupted earlier upload so there is always one backup.
        foreach (var stale in files.Skip(1))
        {
            try { await drive.Files.Delete(stale.Id).ExecuteAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CloudSync: could not delete duplicate {stale.Id}: {ex.Message}"); }
        }
        return files[0];
    }

    /// <summary>Turns library exceptions into something a Settings page can show.</summary>
    private static string Humanize(Exception? ex)
    {
        switch (ex)
        {
            case null:
                return "unknown error";
            case GoogleApiException api when (int)api.HttpStatusCode == 401 || (int)api.HttpStatusCode == 403:
                return "Google rejected the request. Sign out and sign in again.";
            case GoogleApiException api when (int)api.HttpStatusCode == 507:
                return "Your Google Drive storage is full.";
            case GoogleApiException api:
                return api.Error?.Message ?? api.Message;
            case HttpRequestException:
            case TaskCanceledException:
                return "network error. Check your connection and try again.";
            default:
                return ex.Message;
        }
    }
}
