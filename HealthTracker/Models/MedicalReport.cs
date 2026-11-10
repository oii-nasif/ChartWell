using SQLite;

namespace HealthTracker.Models;

[Table("MedicalReport")]
public class MedicalReport
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    public int? DoctorVisitId { get; set; }

    [NotNull]
    public string Title { get; set; } = string.Empty;

    [NotNull]
    public string ReportType { get; set; } = string.Empty;

    [NotNull]
    public string FilePath { get; set; } = string.Empty;

    [NotNull]
    public string FileType { get; set; } = string.Empty;

    [NotNull]
    public string ReportDate { get; set; } = string.Empty;

    public string? DoctorName { get; set; }
    public string? Hospital { get; set; }
    public string? Notes { get; set; }

    [NotNull]
    public string CreatedAt { get; set; } = string.Empty;
}
