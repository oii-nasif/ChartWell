using System.Globalization;
using HealthTracker.Helpers;
using SQLite;

namespace HealthTracker.Models;

[Table("MedicineLog")]
public class MedicineLog
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull]
    public int MedicineId { get; set; }

    [NotNull]
    public int ScheduleId { get; set; }

    [NotNull]
    public string ScheduledFor { get; set; } = string.Empty;

    [Ignore]
    public string ScheduledForDisplay => DateTimeHelper.TryParseStored(ScheduledFor, out var dt)
        ? dt.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture) : ScheduledFor;

    public string? TakenAt { get; set; }

    [NotNull]
    public int WasTaken { get; set; }

    public double StockBefore { get; set; }
    public double StockAfter { get; set; }

    public string? SkipReason { get; set; }
}
