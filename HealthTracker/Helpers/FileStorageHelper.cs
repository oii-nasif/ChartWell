using HealthTracker.Constants;

namespace HealthTracker.Helpers;

/// <summary>
/// MAUI-facing file layout. All path arithmetic lives in <see cref="StoredPathHelper"/> (MAUI-free).
/// File paths are persisted relative to <see cref="GetBaseFolder"/> so a restored backup still resolves.
/// </summary>
public static class FileStorageHelper
{
    public static string GetBaseFolder() =>
        Path.Combine(FileSystem.AppDataDirectory, AppConstants.HealthAppFolder);

    public static string GetSubFolder(string reportType) => reportType switch
    {
        "Lab" => Path.Combine(GetBaseFolder(), AppConstants.ReportsFolder),
        "Prescription" => Path.Combine(GetBaseFolder(), AppConstants.PrescriptionsFolder),
        "Xray" or "Scan" => Path.Combine(GetBaseFolder(), AppConstants.ScansFolder),
        _ => Path.Combine(GetBaseFolder(), AppConstants.OtherFolder)
    };

    public static void EnsureFoldersExist()
    {
        Directory.CreateDirectory(GetBaseFolder());
        Directory.CreateDirectory(Path.Combine(GetBaseFolder(), AppConstants.ReportsFolder));
        Directory.CreateDirectory(Path.Combine(GetBaseFolder(), AppConstants.PrescriptionsFolder));
        Directory.CreateDirectory(Path.Combine(GetBaseFolder(), AppConstants.ScansFolder));
        Directory.CreateDirectory(Path.Combine(GetBaseFolder(), AppConstants.OtherFolder));
    }

    /// <summary>"{type}_{date}_{8-char guid}.{ext}" with user-supplied parts sanitised for the file system.</summary>
    public static string BuildFileName(string reportType, string reportDate, string ext)
    {
        var guid = Guid.NewGuid().ToString("N")[..8];
        var type = StoredPathHelper.SanitizeFileNamePart(reportType, "Report");
        var date = StoredPathHelper.SanitizeFileNamePart(reportDate, "undated");
        var extension = StoredPathHelper.SanitizeFileNamePart(ext.TrimStart('.'), "bin");
        return $"{type}_{date}_{guid}.{extension}";
    }

    /// <summary>Path to store in the DB: relative to the HealthApp base folder, forward slashes.</summary>
    public static string ToStoredPath(string absolute) =>
        StoredPathHelper.ToStoredPath(absolute, GetBaseFolder());

    /// <summary>
    /// Absolute path for a stored value. Legacy absolute paths are returned as-is when they exist and
    /// otherwise re-rooted onto the current base folder.
    /// </summary>
    public static string ResolvePath(string? stored) =>
        StoredPathHelper.ResolvePath(stored, GetBaseFolder());

    public static string GetDatabasePath() =>
        Path.Combine(FileSystem.AppDataDirectory, AppConstants.DatabaseFilename);
}
