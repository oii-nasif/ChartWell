using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;

namespace HealthTracker.Services;

/// <summary>Outcome of a restore. Expected failures (bad file, cancelled picker) are reported here, not thrown.</summary>
public record RestoreResult(bool Succeeded, string Message, int RestoredFileCount = 0);

/// <summary>Contents of manifest.json inside a backup ZIP.</summary>
public class BackupManifest
{
    /// <summary>Layout version of the ZIP itself (db/healthapp.db + files/ + manifest.json). Bump if the layout changes.</summary>
    public int FormatVersion { get; set; } = BackupService.CurrentFormatVersion;
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Device { get; set; } = "";
    /// <summary>SQLite PRAGMA user_version of the backed-up database.</summary>
    public int SchemaVersion { get; set; }
    public int FileCount { get; set; }
}

/// <summary>
/// Creates and restores full-app backups: a ZIP holding a consistent SQLite snapshot plus every
/// attachment under the HealthApp files folder. Used both by the "Share backup" button and by
/// GoogleDriveSyncService. Layout inside the ZIP (forward slashes):
///   manifest.json
///   db/healthapp.db
///   files/&lt;path relative to FileStorageHelper.GetBaseFolder()&gt;
/// Attachment paths stored in the database are relative to the base folder, so restoring onto a
/// different device/path needs no rewriting.
/// </summary>
public class BackupService
{
    public const int CurrentFormatVersion = 1;

    private const string ManifestEntry = "manifest.json";
    private const string DbEntry = "db/" + AppConstants.DatabaseFilename;
    private const string FilesPrefix = "files/";

    /// <summary>Older backup ZIPs beyond this count are removed from the cache before a new one is created.</summary>
    private const int KeepRecentBackups = 2;

    /// <summary>Cloud upload/download and picked-file ZIPs older than this are leftovers of a crashed operation.</summary>
    private static readonly TimeSpan StaleTransferAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Serializes every backup/restore, local or cloud: GoogleDriveSyncService and this class share it so a
    /// local restore and a cloud upload can never interleave. Entry points take it with WaitAsync(0) and
    /// report "already running" instead of queueing; <see cref="CreateBackupZipAsync"/> and
    /// <see cref="RestoreFromZipHoldingLockAsync"/> expect the caller to hold it.
    /// </summary>
    public static readonly SemaphoreSlim OperationLock = new(1, 1);

    private const string BusyMessage = "Another backup or restore is already running.";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly DatabaseService _databaseService;

    public BackupService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    private static string BackupCacheDir
    {
        get
        {
            var dir = Path.Combine(FileSystem.CacheDirectory, "Backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ---- create ----------------------------------------------------------------------------

    /// <summary>
    /// Builds a backup ZIP and returns its path. When <paramref name="targetPath"/> is null a
    /// timestamped file in the cache "Backups" folder is used. Throws on I/O failure (callers wrap).
    /// The caller must hold <see cref="OperationLock"/>.
    /// </summary>
    public async Task<string> CreateBackupZipAsync(string? targetPath = null)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture);
        var cacheDir = BackupCacheDir;
        PruneOldBackups(cacheDir);

        var zipPath = targetPath ?? Path.Combine(cacheDir, $"HealthBackup_{timestamp}.zip");
        var stagingDir = Path.Combine(cacheDir, "staging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        try
        {
            // 1. Consistent DB snapshot. VACUUM INTO writes a complete, compacted copy of the live
            //    database (including anything still in the WAL) as a single transaction, which a
            //    plain File.Copy of an open database does not guarantee.
            var snapshotDb = Path.Combine(stagingDir, AppConstants.DatabaseFilename);
            var schemaVersion = await SnapshotDatabaseAsync(snapshotDb);

            // 2. Zip snapshot + attachments + manifest.
            if (File.Exists(zipPath)) File.Delete(zipPath);
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

            var fileCount = 0;
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(snapshotDb, DbEntry, CompressionLevel.Optimal);

                var baseFolder = FileStorageHelper.GetBaseFolder();
                if (Directory.Exists(baseFolder))
                {
                    foreach (var file in Directory.EnumerateFiles(baseFolder, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(baseFolder, file).Replace('\\', '/');
                        try
                        {
                            // Images/PDFs are already compressed; store them to keep backups fast.
                            zip.CreateEntryFromFile(file, FilesPrefix + relative, CompressionLevel.NoCompression);
                            fileCount++;
                        }
                        catch (IOException ex)
                        {
                            // A file being written right now (e.g. camera capture) is skipped rather than failing the whole backup.
                            System.Diagnostics.Debug.WriteLine($"Backup: skipped {relative}: {ex.Message}");
                        }
                    }
                }

                var manifest = new BackupManifest
                {
                    AppVersion = AppInfo.Current.VersionString,
                    CreatedUtc = DateTime.UtcNow,
                    Device = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim(),
                    SchemaVersion = schemaVersion,
                    FileCount = fileCount
                };
                var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using var ms = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(ms, manifest, JsonOptions);
            }

            System.Diagnostics.Debug.WriteLine($"Backup: created {zipPath} ({fileCount} files)");
            return zipPath;
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    /// <summary>Writes a consistent copy of the live DB to <paramref name="destinationPath"/> and returns PRAGMA user_version.</summary>
    private async Task<int> SnapshotDatabaseAsync(string destinationPath)
    {
        var conn = await _databaseService.GetConnectionAsync();
        var schemaVersion = await conn.ExecuteScalarAsync<int>("PRAGMA user_version");

        if (File.Exists(destinationPath)) File.Delete(destinationPath); // VACUUM INTO refuses to overwrite
        try
        {
            // Single quotes in the path are escaped by doubling, per SQL string literal rules.
            await conn.ExecuteAsync($"VACUUM INTO '{destinationPath.Replace("'", "''")}'");
        }
        catch (Exception ex)
        {
            // Fallback for a SQLite build without VACUUM INTO (< 3.27): checkpoint the WAL then copy.
            System.Diagnostics.Debug.WriteLine($"Backup: VACUUM INTO failed ({ex.Message}); falling back to file copy");
            try { await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { /* not in WAL mode */ }
            File.Copy(FileStorageHelper.GetDatabasePath(), destinationPath, true);
        }
        return schemaVersion;
    }

    /// <summary>
    /// True when the live database holds no user data at all (no medicines, visits, reports or metrics),
    /// e.g. right after a fresh install. Used to stop an automatic upload replacing a real cloud backup.
    /// </summary>
    public async Task<bool> IsLocalDatabaseEmptyAsync()
    {
        var conn = await _databaseService.GetConnectionAsync();
        return await conn.Table<Medicine>().CountAsync() == 0
            && await conn.Table<DoctorVisit>().CountAsync() == 0
            && await conn.Table<MedicalReport>().CountAsync() == 0
            && await conn.Table<HealthMetric>().CountAsync() == 0;
    }

    /// <summary>Creates a backup ZIP and opens the system Share sheet for it (the original "Backup" button behaviour).</summary>
    public async Task ShareBackupAsync()
    {
        string zipPath;
        if (!await OperationLock.WaitAsync(0))
            throw new InvalidOperationException(BusyMessage);
        try
        {
            zipPath = await CreateBackupZipAsync();
        }
        finally
        {
            OperationLock.Release();
        }
        var title = "Chartwell Backup " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        // The ZIP must stay on disk until the receiving app has read it; PruneOldBackups cleans up later.
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(zipPath)
        });
    }

    /// <summary>Kept for existing callers (SettingsViewModel); same as <see cref="ShareBackupAsync"/>.</summary>
    public Task CreateBackup() => ShareBackupAsync();

    // ---- restore ---------------------------------------------------------------------------

    /// <summary>
    /// Replaces the live database and attachments with the contents of a backup ZIP. The swap is
    /// atomic from the app's point of view: current data is renamed to *.bak, the new data is moved
    /// into place, and any failure rolls the *.bak back. After a successful restore the caller MUST
    /// reopen the app state: DatabaseService re-opens lazily on the next GetConnectionAsync, but
    /// IReminderService.RescheduleAll() has to run and the UI must be reloaded (navigate to root)
    /// because every ViewModel is holding stale data.
    /// </summary>
    public async Task<RestoreResult> RestoreFromZipAsync(string zipPath)
    {
        if (!await OperationLock.WaitAsync(0))
            return new RestoreResult(false, BusyMessage);
        try
        {
            return await RestoreFromZipHoldingLockAsync(zipPath);
        }
        finally
        {
            OperationLock.Release();
        }
    }

    /// <summary>Same as <see cref="RestoreFromZipAsync"/> for a caller that already holds <see cref="OperationLock"/> (the cloud sync).</summary>
    public async Task<RestoreResult> RestoreFromZipHoldingLockAsync(string zipPath)
    {
        if (!File.Exists(zipPath))
            return new RestoreResult(false, "Backup file not found.");

        var extractDir = Path.Combine(BackupCacheDir, "restore_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Validate + extract to a temp folder (nothing live is touched yet).
            BackupManifest manifest;
            int extractedFiles;
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);

                var manifestEntry = zip.GetEntry(ManifestEntry);
                if (manifestEntry is null)
                    return new RestoreResult(false, "This ZIP is not a Chartwell backup (manifest.json missing).");
                using (var ms = manifestEntry.Open())
                    manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms) ?? new BackupManifest();
                if (manifest.FormatVersion > CurrentFormatVersion || manifest.SchemaVersion > DatabaseMigrator.CurrentVersion)
                    return new RestoreResult(false, NewerVersionMessage);

                var dbEntry = zip.GetEntry(DbEntry);
                if (dbEntry is null || dbEntry.Length == 0)
                    return new RestoreResult(false, "The backup does not contain a database.");

                Directory.CreateDirectory(extractDir);
                var newDbPath = Path.Combine(extractDir, AppConstants.DatabaseFilename);
                var newFilesDir = Path.Combine(extractDir, "files");
                Directory.CreateDirectory(newFilesDir);

                dbEntry.ExtractToFile(newDbPath, true);
                if (!IsSqliteFile(newDbPath))
                    return new RestoreResult(false, "The database inside the backup is corrupt.");
                // The manifest can be edited; the database's own PRAGMA user_version is authoritative.
                if (await ReadUserVersionAsync(newDbPath) > DatabaseMigrator.CurrentVersion)
                    return new RestoreResult(false, NewerVersionMessage);

                extractedFiles = 0;
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal) || entry.FullName.EndsWith("/"))
                        continue;
                    var relative = entry.FullName[FilesPrefix.Length..];
                    var dest = Path.GetFullPath(Path.Combine(newFilesDir, relative));
                    // Guard against "zip slip" entries like files/../../x
                    if (!dest.StartsWith(Path.GetFullPath(newFilesDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        System.Diagnostics.Debug.WriteLine($"Restore: skipped suspicious entry {entry.FullName}");
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, true);
                    extractedFiles++;
                }
            }
            catch (InvalidDataException)
            {
                return new RestoreResult(false, "The file is not a valid ZIP archive.");
            }
            catch (JsonException)
            {
                return new RestoreResult(false, "The backup manifest is unreadable.");
            }

            // 2. Swap into place. ReplaceDatabaseAsync holds the DatabaseService init lock across close +
            //    swap, so no other caller can re-open the old file in between.
            string? swapError = null;
            await _databaseService.ReplaceDatabaseAsync(() =>
            {
                swapError = SwapIntoPlace(Path.Combine(extractDir, AppConstants.DatabaseFilename), Path.Combine(extractDir, "files"));
                return Task.CompletedTask;
            });
            if (swapError is not null)
                return new RestoreResult(false, swapError);

            FileStorageHelper.EnsureFoldersExist();
            var created = manifest.CreatedUtc == default ? "" : $" from {manifest.CreatedUtc.ToLocalTime():g}";
            return new RestoreResult(true, $"Backup{created} restored ({extractedFiles} files).", extractedFiles);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Restore failed: {ex}");
            return new RestoreResult(false, $"Restore failed: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(extractDir);
        }
    }

    /// <summary>
    /// Moves current db + files folder to *.bak, moves the new ones in, rolls back on failure.
    /// Returns null on success or an error message.
    /// </summary>
    private static string? SwapIntoPlace(string newDbPath, string newFilesDir)
    {
        var dbPath = FileStorageHelper.GetDatabasePath();
        var filesDir = FileStorageHelper.GetBaseFolder();
        var dbBak = dbPath + ".bak";
        var filesBak = filesDir + ".bak";

        // Leftovers from a crashed previous restore
        TryDeleteFile(dbBak);
        TryDeleteDirectory(filesBak);

        var movedDb = false;
        var movedFiles = false;
        try
        {
            // SQLite sidecar files must go with the main file or the new DB would be paired with a stale WAL.
            foreach (var sidecar in SidecarPaths(dbPath))
                TryDeleteFile(sidecar);

            if (File.Exists(dbPath)) { File.Move(dbPath, dbBak); movedDb = true; }
            if (Directory.Exists(filesDir)) { Directory.Move(filesDir, filesBak); movedFiles = true; }

            File.Move(newDbPath, dbPath);
            Directory.Move(newFilesDir, filesDir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Restore swap failed, rolling back: {ex}");
            try
            {
                TryDeleteFile(dbPath);
                TryDeleteDirectory(filesDir);
                if (movedDb) File.Move(dbBak, dbPath);
                if (movedFiles) Directory.Move(filesBak, filesDir);
            }
            catch (Exception rollbackEx)
            {
                System.Diagnostics.Debug.WriteLine($"Restore rollback failed: {rollbackEx}");
                return "Restore failed and the previous data could not be fully recovered. Please reinstall from a backup.";
            }
            return $"Restore failed: {ex.Message}. Your previous data is unchanged.";
        }

        TryDeleteFile(dbBak);
        TryDeleteDirectory(filesBak);
        return null;
    }

    /// <summary>Lets the user pick a backup ZIP (e.g. from Downloads or Drive) and restores it.</summary>
    public async Task<RestoreResult> RestoreFromPickedFileAsync()
    {
        FileResult? picked;
        try
        {
            picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select a Chartwell backup (.zip)",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "application/zip", "application/x-zip-compressed", "application/octet-stream" } }
                })
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"File picker failed: {ex}");
            return new RestoreResult(false, "Could not open the file picker.");
        }

        if (picked is null)
            return new RestoreResult(false, "Restore cancelled.");

        // Content URIs from the picker are not always readable as plain paths; copy to cache first.
        var localCopy = Path.Combine(BackupCacheDir, "picked_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var source = await picked.OpenReadAsync())
            using (var dest = File.Create(localCopy))
                await source.CopyToAsync(dest);

            return await RestoreFromZipAsync(localCopy);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reading picked file failed: {ex}");
            return new RestoreResult(false, $"Could not read the selected file: {ex.Message}");
        }
        finally
        {
            TryDeleteFile(localCopy);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private const string NewerVersionMessage = "This backup was made by a newer version of Chartwell. Update the app first.";

    /// <summary>PRAGMA user_version of a database file that is not the live one; 0 when it cannot be read.</summary>
    private static async Task<int> ReadUserVersionAsync(string dbPath)
    {
        SQLite.SQLiteAsyncConnection? conn = null;
        try
        {
            conn = new SQLite.SQLiteAsyncConnection(dbPath, SQLite.SQLiteOpenFlags.ReadOnly);
            return await conn.ExecuteScalarAsync<int>("PRAGMA user_version");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Restore: could not read user_version: {ex.Message}");
            return 0;
        }
        finally
        {
            if (conn is not null)
            {
                try { await conn.CloseAsync(); } catch { /* temp file, best effort */ }
            }
        }
    }

    private static IEnumerable<string> SidecarPaths(string dbPath)
    {
        yield return dbPath + "-wal";
        yield return dbPath + "-shm";
        yield return dbPath + "-journal";
    }

    private static bool IsSqliteFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[16];
            var read = fs.Read(header, 0, header.Length);
            return read == 16 && System.Text.Encoding.ASCII.GetString(header, 0, 15) == "SQLite format 3";
        }
        catch
        {
            return false;
        }
    }

    private static void PruneOldBackups(string cacheDir)
    {
        try
        {
            var zips = Directory.GetFiles(cacheDir, "HealthBackup_*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(KeepRecentBackups);
            foreach (var zip in zips)
                File.Delete(zip);

            // Cloud transfer / picked-file ZIPs are deleted by their owners when the operation ends;
            // anything still here after an hour was left behind by a crash or a killed process.
            var cutoff = DateTime.UtcNow - StaleTransferAge;
            foreach (var pattern in new[] { "cloud-upload-*.zip", "cloud-download-*.zip", "picked_*.zip" })
                foreach (var stale in Directory.GetFiles(cacheDir, pattern))
                    if (File.GetLastWriteTimeUtc(stale) < cutoff)
                        TryDeleteFile(stale);

            // Abandoned staging/restore folders from a crash
            foreach (var dir in Directory.GetDirectories(cacheDir))
                if (Path.GetFileName(dir).StartsWith("staging_") || Path.GetFileName(dir).StartsWith("restore_"))
                    TryDeleteDirectory(dir);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Backup prune failed: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not delete {path}: {ex.Message}"); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not delete {path}: {ex.Message}"); }
    }
}
