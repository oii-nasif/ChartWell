using SQLite;

namespace HealthTracker.Models;

[Table("MedicineCourse")]
public class MedicineCourse
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull]
    public int MedicineId { get; set; }

    [NotNull]
    public string StartDate { get; set; } = string.Empty;

    public string? EndDate { get; set; }

    public int PrescribedDays { get; set; }

    public int SchedulesPerDay { get; set; }

    public int TotalTaken { get; set; }

    public int TotalScheduled { get; set; }

    public string Status { get; set; } = "Active"; // Active, Completed, Stopped

    public string? Notes { get; set; }

    [NotNull]
    public string CreatedAt { get; set; } = string.Empty;
}
