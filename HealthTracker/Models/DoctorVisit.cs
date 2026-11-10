using SQLite;

namespace HealthTracker.Models;

[Table("DoctorVisit")]
public class DoctorVisit
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull]
    public string DoctorName { get; set; } = string.Empty;

    public string? Hospital { get; set; }

    [NotNull]
    public string VisitDate { get; set; } = string.Empty;

    public string? Diagnosis { get; set; }

    public string? Notes { get; set; }

    public string? FollowUpDate { get; set; }

    public string? PrescriptionImagePath { get; set; }

    [NotNull]
    public string CreatedAt { get; set; } = string.Empty;
}
