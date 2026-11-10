using SQLite;

namespace HealthTracker.Models;

[Table("ReportPage")]
public class ReportPage
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull]
    public int ReportId { get; set; }

    [NotNull]
    public string FilePath { get; set; } = string.Empty;

    [NotNull]
    public string FileType { get; set; } = string.Empty;

    public int PageNumber { get; set; }
}
