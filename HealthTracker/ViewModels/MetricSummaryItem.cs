namespace HealthTracker.ViewModels;

/// <summary>One dashboard tile. Colours are theme TOKEN names, resolved in XAML via ThemeColorConverter.</summary>
public class MetricSummaryItem
{
    public string Type { get; set; } = string.Empty;
    /// <summary>Material icon file, e.g. "ic_scale.png" (from ChartTheme.MetricIcon).</summary>
    public string Icon { get; set; } = "ic_monitor_heart.png";
    public string DisplayName { get; set; } = string.Empty;
    public string LatestValueText { get; set; } = "—";
    public string Unit { get; set; } = string.Empty;
    /// <summary>Metric colour token, e.g. "MetricHeartRate".</summary>
    public string AccentToken { get; set; } = "Primary";

    public string TrendText { get; set; } = string.Empty;
    public string TrendIcon { get; set; } = "ic_trending_flat.png";
    public string TrendToken { get; set; } = "TextSecondary";

    /// <summary>"Just now", "3 h ago", "Yesterday", "12 Oct" ...</summary>
    public string LastRecordedText { get; set; } = string.Empty;
    /// <summary>Readings logged in the last 7 days for this type.</summary>
    public int WeekCount { get; set; }

    public bool HasData { get; set; }
    public bool HasNoData => !HasData;
    public bool HasTrend => HasData && !string.IsNullOrEmpty(TrendText);

    /// <summary>Up to the last 12 readings, oldest first, for the row's sparkline (systolic for blood pressure).</summary>
    public double[] Spark { get; set; } = [];
    public bool HasSpark => Spark.Length >= 2;

    /// <summary>"Normal", "Elevated", "Low" ... against general adult ranges; empty when no range applies.</summary>
    public string StatusText { get; set; } = string.Empty;
    /// <summary>Status token for the chip: Success, Warning, Danger or Info.</summary>
    public string StatusToken { get; set; } = "TextSecondary";
    public bool HasStatus => HasData && StatusText.Length > 0;
}

/// <summary>One day of the "last 7 days" strip on the Vitals page.</summary>
public sealed record WeekDayDot(string Label, bool IsLogged, bool IsToday)
{
    public string AccessibilityText => IsLogged ? $"{Label}: reading recorded" : $"{Label}: nothing recorded";
}

/// <summary>Relative-time wording for "last recorded" captions. Kept here because Helpers/ is owned elsewhere.</summary>
public static class MetricTimeFormat
{
    public static string RelativeTo(DateTime recorded, DateTime now)
    {
        var diff = now - recorded;
        if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;

        if (diff.TotalMinutes < 1) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
        if (diff.TotalHours < 24 && recorded.Date == now.Date) return $"{(int)diff.TotalHours} h ago";
        if (recorded.Date == now.Date.AddDays(-1)) return "Yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
        return recorded.Year == now.Year
            ? recorded.ToString("dd MMM", System.Globalization.CultureInfo.InvariantCulture)
            : recorded.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }
}
