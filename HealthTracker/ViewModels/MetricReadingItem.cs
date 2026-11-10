using System.Globalization;
using HealthTracker.Helpers;
using HealthTracker.Models;

namespace HealthTracker.ViewModels;

/// <summary>Row model for the readings list on MetricHistoryPage. Wraps the stored metric with display strings.</summary>
public class MetricReadingItem
{
    public MetricReadingItem(HealthMetric metric, DateTime recorded, string accentToken)
    {
        Metric = metric;
        AccentToken = accentToken;
        Recorded = recorded;
        ValueText = MetricsDashboardViewModel.FormatValue(metric);
        Unit = metric.Unit;
        DateText = recorded == DateTime.MinValue
            ? metric.RecordedAt
            : recorded.ToString("ddd, dd MMM yyyy", CultureInfo.InvariantCulture);
        TimeText = recorded == DateTime.MinValue ? string.Empty : recorded.ToString("HH:mm", CultureInfo.InvariantCulture);
        Notes = metric.Notes?.Trim() ?? string.Empty;
    }

    public HealthMetric Metric { get; }
    /// <summary>Metric colour token (e.g. "MetricHeartRate") for the row accent.</summary>
    public string AccentToken { get; }
    public DateTime Recorded { get; }
    public string ValueText { get; }
    public string Unit { get; }
    public string DateText { get; }
    public string TimeText { get; }
    public string Notes { get; }
    public bool HasNotes => !string.IsNullOrEmpty(Notes);
    public bool HasTime => !string.IsNullOrEmpty(TimeText);
}
