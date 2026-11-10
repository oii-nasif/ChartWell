namespace HealthTracker.Helpers;

/// <summary>Single source of the emoji shown for a medicine's stock unit.</summary>
public static class StockUnitIcons
{
    public const string Default = "📦";

    public static string For(string? stockUnit) => stockUnit switch
    {
        "Tablet"    => "💊",
        "Capsule"   => "💊",
        "Drop"      => "💧",
        "Syrup"     => "🧴",
        "Injection" => "💉",
        "Spray"     => "🚿",
        "Inhaler"   => "🫁",
        _           => Default
    };
}
