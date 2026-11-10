using SQLite;

namespace HealthTracker.Models;

[Table("Medicine")]
public class Medicine
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull]
    public string Name { get; set; } = string.Empty;

    [NotNull]
    public double StockCount { get; set; }

    [NotNull]
    public string StockUnit { get; set; } = string.Empty;

    [NotNull]
    public string StartDate { get; set; } = string.Empty;

    public string? EndDate { get; set; }

    public int PrescribedDays { get; set; }

    public int IsOngoing { get; set; } = 0;

    public string? Instructions { get; set; }

    public string? Notes { get; set; }

    [NotNull]
    public int IsActive { get; set; } = 1;

    public int SortOrder { get; set; } = 0;

    /// <summary>
    /// Whole days added to the course by AutoLogMissedDoses to make up missed doses.
    /// Capped at PrescribedDays. EndDate is always StartDate + PrescribedDays - 1 + ExtensionDays.
    /// Nullable in SQLite (added by migration) so the column can be appended to existing tables.
    /// </summary>
    public int ExtensionDays { get; set; } = 0;

    [NotNull]
    public string CreatedAt { get; set; } = string.Empty;
}
