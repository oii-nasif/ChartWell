using HealthTracker.Constants;
using SkiaSharp;

namespace HealthTracker.Helpers;

/// <summary>
/// Bridges the XAML colour tokens to SkiaSharp colours for LiveCharts, so charts follow the active
/// theme. Resolve colours when building series (ViewModels rebuild series on theme change).
/// </summary>
public static class ChartTheme
{
    public static SKColor Resolve(string token, string fallbackHex = "#0E7C6B")
    {
        var resources = Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(token, out var value) && value is Color c)
            return new SKColor((byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255), (byte)(c.Alpha * 255));
        return SKColor.Parse(fallbackHex);
    }

    public static string MetricToken(string metricType) => metricType switch
    {
        MetricTypes.Weight => "MetricWeight",
        MetricTypes.BloodPressure => "MetricBloodPressure",
        MetricTypes.HeartRate => "MetricHeartRate",
        MetricTypes.BloodSugar => "MetricBloodSugar",
        MetricTypes.SpO2 => "MetricSpO2",
        MetricTypes.Temperature => "MetricTemperature",
        _ => "Primary"
    };

    public static string MetricIcon(string metricType) => metricType switch
    {
        MetricTypes.Weight => "ic_scale.png",
        MetricTypes.BloodPressure => "ic_bloodtype.png",
        MetricTypes.HeartRate => "ic_favorite.png",
        MetricTypes.BloodSugar => "ic_water_drop.png",
        MetricTypes.SpO2 => "ic_air.png",
        MetricTypes.Temperature => "ic_thermostat.png",
        _ => "ic_monitor_heart.png"
    };

    public static SKColor Metric(string metricType) => Resolve(MetricToken(metricType));
    public static SKColor TextSecondary => Resolve("TextSecondary", "#4A605A");
    public static SKColor Outline => Resolve("Outline", "#DDE6E2");
    public static SKColor Surface => Resolve("Surface", "#FFFFFF");
}
