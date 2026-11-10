namespace HealthTracker.Models;

public class TimelineEntry
{
    public int SourceId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EventDate { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;
    public string? TimeText { get; set; }
    public string? SubText { get; set; }
    public string MonthYearGroup { get; set; } = string.Empty;
    public string TagColor { get; set; } = "#7F8C8D";
    public string TagIcon { get; set; } = "📌";
}
