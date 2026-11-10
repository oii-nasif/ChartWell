using SQLite;

namespace HealthTracker.Models;

[Table("MedicineSchedule")]
public class MedicineSchedule
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull]
    public int MedicineId { get; set; }

    /// <summary>Time of day in "HH:mm" format</summary>
    [NotNull]
    public string ScheduleTime { get; set; } = string.Empty;

    [NotNull]
    public int IsActive { get; set; } = 1;

    /// <summary>
    /// "yyyy-MM-dd HH:mm" when this schedule row was created. Slots earlier than this are never
    /// auto-logged as missed. Not [NotNull]: the column is appended to existing tables by migration
    /// and SQLite cannot add a NOT NULL column without a default.
    /// </summary>
    public string? CreatedAt { get; set; }
}
