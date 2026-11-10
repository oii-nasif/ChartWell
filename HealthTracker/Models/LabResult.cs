using SQLite;

namespace HealthTracker.Models;

/// <summary>
/// One lab value. Rows saved together (one sitting at the lab) share <see cref="BatchId"/>.
/// The reference range and expected word are stored on the row: they are what this result was judged
/// against (the catalog default for the user's sex, or the range printed on the report), so a later
/// catalog or profile change never rewrites history.
/// </summary>
[Table("LabResult")]
public class LabResult
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>A <c>LabCatalog</c> code, or "custom:..." for a user-defined test.</summary>
    [NotNull, Indexed]
    public string TestCode { get; set; } = string.Empty;

    /// <summary>Display name at the time of saving (the only name a custom test has).</summary>
    [NotNull]
    public string TestName { get; set; } = string.Empty;

    [NotNull]
    public string PanelCode { get; set; } = string.Empty;

    /// <summary>1 when the result is a word (<see cref="ValueText"/>), 0 when it is a number (<see cref="Value"/>).</summary>
    public int IsQualitative { get; set; }

    public double? Value { get; set; }

    public string? ValueText { get; set; }

    /// <summary>Unit the value was entered in; the range below is in the same unit.</summary>
    [NotNull]
    public string Unit { get; set; } = string.Empty;

    public double? RefLow { get; set; }
    public double? RefHigh { get; set; }

    /// <summary>The unremarkable word for a qualitative test ("Negative"), or null when none applies.</summary>
    public string? ExpectedText { get; set; }

    /// <summary>Sample collection date, "yyyy-MM-dd".</summary>
    [NotNull, Indexed]
    public string CollectedOn { get; set; } = string.Empty;

    public string? LabName { get; set; }
    public string? Notes { get; set; }

    /// <summary>The stored report this value was read from, if any.</summary>
    public int? ReportId { get; set; }

    [NotNull, Indexed]
    public string BatchId { get; set; } = string.Empty;

    /// <summary>1 when the app derived the value from others in the batch (LDL, eGFR, ...).</summary>
    public int IsCalculated { get; set; }

    [NotNull]
    public string CreatedAt { get; set; } = string.Empty;
}
