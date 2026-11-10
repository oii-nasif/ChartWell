namespace HealthTracker.Constants;

public static class MetricTypes
{
    public const string Weight = "Weight";
    public const string BloodPressure = "BloodPressure";
    public const string HeartRate = "HeartRate";
    public const string BloodSugar = "BloodSugar";
    public const string SpO2 = "SpO2";
    public const string Temperature = "Temperature";

    public static readonly string[] All =
    [
        Weight, BloodPressure, HeartRate, BloodSugar, SpO2, Temperature
    ];

    public static string GetUnit(string metricType) => metricType switch
    {
        Weight => "kg",
        BloodPressure => "mmHg",
        HeartRate => "bpm",
        BloodSugar => "mg/dL",
        SpO2 => "%",
        Temperature => "°C",
        _ => ""
    };

    public static (double Min, double Max) GetRange(string metricType) => metricType switch
    {
        Weight => (1.0, 500.0),
        HeartRate => (20, 300),
        BloodSugar => (10, 1000),
        SpO2 => (50, 100),
        Temperature => (25.0, 45.0),
        _ => (0, double.MaxValue)
    };

    public static (double SysMin, double SysMax, double DiaMin, double DiaMax) GetBloodPressureRange()
        => (50, 300, 30, 200);
}
