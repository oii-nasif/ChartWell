using HealthTracker.Constants;

namespace HealthTracker.Helpers;

/// <summary>
/// Pure (MAUI-free) path logic behind <see cref="FileStorageHelper"/>. Files are stored in the DB as
/// paths relative to the HealthApp base folder with forward slashes, so a backup restored on another
/// device or a reinstall (new app-data directory) still resolves.
/// </summary>
public static class StoredPathHelper
{
    /// <summary>Converts an absolute path under <paramref name="baseFolder"/> to a stored relative path.</summary>
    public static string ToStoredPath(string? path, string baseFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        if (!Path.IsPathRooted(path))
            return NormalizeSeparators(path);

        var fullBase = TrimTrailingSeparators(Path.GetFullPath(baseFolder));
        var full = Path.GetFullPath(path);
        if (full.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(fullBase + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeSeparators(full[(fullBase.Length + 1)..]);
        }

        // Legacy absolute path from a different install: keep everything after the HealthApp folder.
        var tail = TailAfterBaseFolderName(full);
        return tail ?? full;
    }

    /// <summary>
    /// Resolves a stored path to an absolute one. Relative values are rooted on <paramref name="baseFolder"/>;
    /// absolute values are returned as-is when they exist, otherwise their tail after the HealthApp folder
    /// is re-rooted on the current base folder.
    /// </summary>
    public static string ResolvePath(string? stored, string baseFolder, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(stored)) return string.Empty;
        exists ??= File.Exists;

        if (!Path.IsPathRooted(stored))
            return Path.Combine(baseFolder, stored.Replace('/', Path.DirectorySeparatorChar));

        if (exists(stored)) return stored;

        var tail = TailAfterBaseFolderName(stored);
        if (tail is null) return stored;
        var rerooted = Path.Combine(baseFolder, tail.Replace('/', Path.DirectorySeparatorChar));
        return exists(rerooted) ? rerooted : stored;
    }

    /// <summary>Strips characters that are invalid in a file name (plus '/' and '\') from user text.</summary>
    public static string SanitizeFileNamePart(string? value, string fallback = "file")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var cleaned = new string(value.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>"…/HealthApp/reports/x.pdf" → "reports/x.pdf"; null when the folder name is absent.</summary>
    private static string? TailAfterBaseFolderName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var marker = "/" + AppConstants.HealthAppFolder + "/";
        var idx = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var tail = normalized[(idx + marker.Length)..];
        return tail.Length == 0 ? null : tail;
    }
}
