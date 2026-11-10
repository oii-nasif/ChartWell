using System.Collections;
using System.Globalization;

namespace HealthTracker.Helpers;

public class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && value.ToString() != string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class NullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || value.ToString() == string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class TakenIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 1 ? "ic_check_circle.png" : "ic_cancel.png";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class PinStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "PIN is configured." : "No PIN set. App is unlocked.";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class HexColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try { return Color.FromArgb(value?.ToString() ?? "#7F8C8D"); }
        catch { return Color.FromArgb("#7F8C8D"); }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Resolves a semantic token name ("Success", "Warning", "Danger", "Primary", "MetricHeartRate", ...)
/// to the colour of the active theme. ViewModels expose token names instead of hex strings so that
/// they stay theme-agnostic; the converter looks the token up at bind time.
/// Falls back to TextSecondary when the token is unknown or null.
/// </summary>
public class ThemeColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString();
        var resources = Application.Current?.Resources;
        if (resources is null) return Colors.Gray;

        // ConverterParameter picks the tinted pair of a status token: "Container" → {Key}Container
        // (chip background), "OnContainer" → On{Key}Container (text on that chip). Tokens without a
        // container pair (TextSecondary, ...) fall back to the neutral chip colours.
        switch (parameter?.ToString())
        {
            case "Container":
                if (!string.IsNullOrEmpty(key) && resources.TryGetValue(key + "Container", out var bg) && bg is Color bgc) return bgc;
                return resources.TryGetValue("SurfaceVariant", out var sv) && sv is Color svc ? svc : Colors.LightGray;
            case "OnContainer":
                if (!string.IsNullOrEmpty(key) && resources.TryGetValue("On" + key + "Container", out var fg) && fg is Color fgc) return fgc;
                return resources.TryGetValue("TextSecondary", out var ts) && ts is Color tsc ? tsc : Colors.Gray;
        }

        if (!string.IsNullOrEmpty(key) && resources.TryGetValue(key, out var found) && found is Color c)
            return c;

        // Hex strings still work for any legacy value.
        if (!string.IsNullOrEmpty(key) && key.StartsWith('#'))
        {
            try { return Color.FromArgb(key); } catch { /* fall through */ }
        }

        return resources.TryGetValue("TextSecondary", out var fallback) && fallback is Color fc ? fc : Colors.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class IsPdfConverter : IValueConverter
{
    private static readonly HashSet<string> ViewableTypes = ["pdf", "jpg", "jpeg", "png", "bmp", "gif", "webp"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ViewableTypes.Contains(value?.ToString()?.ToLowerInvariant() ?? "");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>SQLite stores booleans as int; this maps 0/1 to bool. Pass "invert" as parameter to flip.</summary>
public class IntToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool result = value is int i && i != 0;
        return parameter?.ToString() == "invert" ? !result : result;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter?.ToString() == "invert") b = !b;
        return b ? 1 : 0;
    }
}

/// <summary>True when a collection has items (or an int is > 0). Pass "invert" to get the empty check.</summary>
public class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasItems = value switch
        {
            int i => i > 0,
            ICollection c => c.Count > 0,
            IEnumerable e => e.Cast<object>().Any(),
            _ => false
        };
        return parameter?.ToString() == "invert" ? !hasItems : hasItems;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool notEmpty = !string.IsNullOrWhiteSpace(value?.ToString());
        return parameter?.ToString() == "invert" ? !notEmpty : notEmpty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
