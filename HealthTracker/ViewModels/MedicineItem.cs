using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;

namespace HealthTracker.ViewModels;

/// <summary>
/// A medicine card on the list page. Exposes colour token names (bind through ThemeColorConverter)
/// and icon file names; no hex, no emoji. Linked into HealthTracker.Tests: no MAUI types here.
/// </summary>
public class MedicineItem
{
    public Medicine Medicine { get; set; } = null!;

    /// <summary>Legacy emoji glyph; not bound by any page.</summary>
    public string StockUnitIcon => StockUnitIcons.For(Medicine.StockUnit);

    /// <summary>Material icon for the stock unit.</summary>
    public string StockUnitImage => MedicineIcons.ForStockUnit(Medicine.StockUnit);

    /// <summary>Today's dose progress (taken / reachable slots).</summary>
    public double DoseProgress { get; set; }
    public string DoseProgressText { get; set; } = string.Empty;

    /// <summary>Schedule times for the chip row, "HH:mm".</summary>
    public List<string> ScheduleTimes { get; set; } = [];
    public bool HasScheduleTimes => ScheduleTimes.Count > 0;

    /// <summary>Active course progress (TotalTaken / TotalScheduled); 0 and hidden for ongoing medicines.</summary>
    public double CourseProgress { get; set; }
    public string CourseProgressText { get; set; } = string.Empty;
    public bool HasCourseProgress => !string.IsNullOrEmpty(CourseProgressText);

    /// <summary>Adherence over the last 7 days as a percentage; null when nothing was due.</summary>
    public int? AdherencePercent { get; set; }
    public bool HasAdherence => AdherencePercent.HasValue;
    public string AdherenceText => AdherencePercent.HasValue ? $"{AdherencePercent}% adherence" : string.Empty;

    /// <summary>Days of doses the stock covers (MedicineService.GetStockDaysRemaining); MaxValue when unknown.</summary>
    public double StockDaysRemaining { get; set; }
    public bool IsLowStock => StockDaysRemaining < AppConstants.LowStockDays;
    public bool IsCriticalStock => StockDaysRemaining < AppConstants.CriticalStockDays;

    public string StockText
    {
        get
        {
            var count = Medicine.StockCount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var unit = string.IsNullOrEmpty(Medicine.StockUnit) ? "left" : Medicine.StockUnit.ToLowerInvariant() + (Medicine.StockCount == 1 ? "" : "s");
            return $"{count} {unit}";
        }
    }

    public string StockChipText => IsCriticalStock
        ? (Medicine.StockCount < 1 ? "Out of stock" : "Critical stock")
        : IsLowStock ? "Low stock" : string.Empty;

    /// <summary>Token for the stock warning: Danger when critical, Warning when low.</summary>
    public string StockWarningColor => IsCriticalStock ? MedicineIcons.TokenDanger : MedicineIcons.TokenWarning;

    /// <summary>Token for today's progress bar.</summary>
    public string ProgressColor => DoseProgress >= 1.0 ? MedicineIcons.TokenSuccess
        : DoseProgress > 0 ? MedicineIcons.TokenPrimary
        : MedicineIcons.TokenOutline;

    public int Streak { get; set; }
    public int MissedThisWeek { get; set; }

    public bool ShowStreak => Streak >= 2;
    public bool ShowMissed => MissedThisWeek > 0;
    public string StreakText => $"{Streak} day streak";
    public string MissedText => $"{MissedThisWeek} missed this week";

    /// <summary>Set for inactive (past) medicines; the card renders muted with a "Completed" chip.</summary>
    public bool IsPast { get; set; }
    public string PastSummaryText { get; set; } = string.Empty;
}
