namespace HealthTracker.Constants;

public static class AppConstants
{
    public const string DatabaseFilename = "healthapp.db";
    public const string HealthAppFolder = "HealthApp";
    public const string ReportsFolder = "reports";
    public const string PrescriptionsFolder = "prescriptions";
    public const string ScansFolder = "scans";
    public const string OtherFolder = "other";

    /// <summary>Stock covering fewer than this many days of doses is "low" (refill nudge, orange badge).</summary>
    public const int LowStockDays = 5;
    /// <summary>Stock covering fewer than this many days of doses is "critical" (red badge).</summary>
    public const int CriticalStockDays = 2;

    /// <summary>The only report types accepted by ReportService; also drives the Add Report picker.</summary>
    public static readonly string[] ReportTypes = ["Lab", "Prescription", "Xray", "Scan", "Other"];

    public const int PinMinLength = 4;
    public const int PinMaxLength = 6;
    public const int MaxFailedPinAttempts = 5;
    public const int PinCooldownSeconds = 30;
    public const int BackgroundResumeThresholdMinutes = 2;
}
