namespace HealthTracker.ViewModels;

/// <summary>
/// Material icon file names and colour token names used by the medicine ViewModels. ViewModels never
/// expose hex colours or emoji (see docs/DESIGN_SYSTEM.md); pages bind tokens through ThemeColorConverter.
/// No MAUI dependency: this file is linked into HealthTracker.Tests.
/// </summary>
public static class MedicineIcons
{
    public const string Medication = "ic_medication.png";
    public const string Check = "ic_check_circle.png";
    public const string Cancel = "ic_cancel.png";
    public const string Schedule = "ic_schedule.png";
    public const string Warning = "ic_warning.png";
    public const string Pending = "ic_pending_actions.png";

    /// <summary>Icon for a medicine's stock unit ("Tablet", "Drop", ...).</summary>
    public static string ForStockUnit(string? stockUnit) => stockUnit switch
    {
        "Drop" => "ic_water_drop.png",
        "Injection" => "ic_vaccines.png",
        "Spray" or "Inhaler" => "ic_air.png",
        "Syrup" => "ic_water_drop.png",
        "Tablet" or "Capsule" => Medication,
        _ => "ic_inventory_2.png",
    };

    public const string TokenSuccess = "Success";
    public const string TokenWarning = "Warning";
    public const string TokenDanger = "Danger";
    public const string TokenPrimary = "Primary";
    public const string TokenSecondary = "TextSecondary";
    public const string TokenOutline = "Outline";
}
