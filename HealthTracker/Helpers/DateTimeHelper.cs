using System.Globalization;

namespace HealthTracker.Helpers;

/// <summary>
/// Every date/time string the app stores goes through here. Storage formats are culture-invariant:
///   date only        "yyyy-MM-dd"
///   date + minute    "yyyy-MM-dd HH:mm"      (Medicine.StartDate, MedicineSchedule.CreatedAt)
///   date + second    "yyyy-MM-ddTHH:mm:ss"   (CreatedAt / ScheduledFor / TakenAt)
///   time of day      "HH:mm"                 (MedicineSchedule.ScheduleTime)
/// Parsing is lenient across all stored shapes so older rows keep working after a format change.
/// </summary>
public static class DateTimeHelper
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Exact formats accepted by <see cref="TryParseStored"/>, most common first.</summary>
    private static readonly string[] StoredFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
    ];

    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"];

    public static string ToDateString(DateTime dt) => dt.ToString("yyyy-MM-dd", Inv);

    public static string ToDateTimeString(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss", Inv);

    /// <summary>"yyyy-MM-dd HH:mm" — the canonical StartDate / schedule CreatedAt format.</summary>
    public static string ToDateTimeMinuteString(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm", Inv);

    public static string ToTimeString(TimeSpan time) =>
        new DateTime(2000, 1, 1).Add(time).ToString("HH:mm", Inv);

    public static DateTime ParseDate(string s) =>
        TryParseStored(s, out var dt) ? dt : throw new FormatException($"Not a stored date: '{s}'");

    public static DateTime ParseDateTime(string s) =>
        TryParseStored(s, out var dt) ? dt : throw new FormatException($"Not a stored date-time: '{s}'");

    /// <summary>
    /// Culture-invariant parse of any string this app has ever stored (date-only or date-time, with
    /// 'T' or space separator). Falls back to an invariant general parse for anything else.
    /// </summary>
    public static bool TryParseStored(string? s, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (DateTime.TryParseExact(s, StoredFormats, Inv, DateTimeStyles.None, out result)) return true;
        return DateTime.TryParse(s, Inv, DateTimeStyles.None, out result);
    }

    /// <summary>Alias of <see cref="TryParseStored"/> kept for existing callers.</summary>
    public static bool TryParse(string? s, out DateTime result) => TryParseStored(s, out result);

    /// <summary>Culture-invariant parse of a stored "HH:mm" time of day.</summary>
    public static bool TryParseTime(string? s, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (DateTime.TryParseExact(s.Trim(), TimeFormats, Inv, DateTimeStyles.None, out var dt))
        {
            result = dt.TimeOfDay;
            return true;
        }
        return TimeSpan.TryParse(s, Inv, out result) && result >= TimeSpan.Zero && result < TimeSpan.FromDays(1);
    }

    /// <summary>The "yyyy-MM-dd" part of any stored date or date-time string.</summary>
    public static string DatePart(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        return stored.Length >= 10 ? stored[..10] : stored;
    }

    /// <summary>
    /// Normalises a start-date string (any accepted format) to the canonical "yyyy-MM-dd HH:mm".
    /// Returns the input unchanged when it cannot be parsed.
    /// </summary>
    public static string NormalizeStartDate(string? s)
    {
        if (!TryParseStored(s, out var dt)) return s ?? string.Empty;
        return ToDateTimeMinuteString(dt);
    }

    public static string ToMonthYearGroup(string dateStr)
    {
        if (!TryParseStored(dateStr, out var dt)) return "Unknown";
        return dt.ToString("MMMM yyyy", Inv);
    }
}
