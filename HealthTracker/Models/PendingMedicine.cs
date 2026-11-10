using SQLite;

namespace HealthTracker.Models;

[Table("PendingMedicine")]
public class PendingMedicine
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [NotNull]
    public string Name { get; set; } = string.Empty;

    public string? Dosage { get; set; }

    public string? Frequency { get; set; }

    public string? Form { get; set; }

    [NotNull]
    public string ScannedAt { get; set; } = string.Empty;
}
