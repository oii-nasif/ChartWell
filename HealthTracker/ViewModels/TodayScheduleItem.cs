using System.Globalization;
using HealthTracker.Helpers;

namespace HealthTracker.ViewModels;

/// <summary>Which section of the Today list a dose row belongs to.</summary>
public enum TodayDoseGroupKind
{
    DueNow = 0,
    Later = 1,
    Done = 2,
}

/// <summary>
/// One dose slot for today (also reused by the medicine detail page's "Today's schedule" section).
/// Exposes colour token names and icon file names, never hex or emoji. Linked into HealthTracker.Tests,
/// so it must not reference MAUI types.
/// </summary>
public class TodayScheduleItem : BaseViewModel
{
    private static readonly TimeSpan DueWindowBefore = TimeSpan.FromHours(1);
    private static readonly TimeSpan OverdueAfter = TimeSpan.FromHours(2);

    public int MedicineId { get; set; }
    public int ScheduleId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string StockUnit { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public string ScheduleTime { get; set; } = string.Empty;

    /// <summary>Legacy emoji glyph; not bound by any page (kept for the shared helper and its tests).</summary>
    public string StockUnitIcon => StockUnitIcons.For(StockUnit);

    /// <summary>Material icon for the stock unit, used in the row's icon circle.</summary>
    public string StockUnitImage => MedicineIcons.ForStockUnit(StockUnit);

    public string ScheduleTimeDisplay => DateTimeHelper.TryParseTime(ScheduleTime, out var t)
        ? DateTime.Today.Add(t).ToString("HH:mm", CultureInfo.InvariantCulture)
        : ScheduleTime;

    public bool HasInstructions => !string.IsNullOrWhiteSpace(Instructions);

    private bool _isLogged;
    private bool _wasTaken;
    private bool _isOutOfStock;

    public bool IsLogged
    {
        get => _isLogged;
        set
        {
            _isLogged = value;
            OnPropertyChanged();
            RaiseStateChanged();
        }
    }

    public bool WasTaken
    {
        get => _wasTaken;
        set
        {
            _wasTaken = value;
            OnPropertyChanged();
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Soft warning: the medicine's stock is below one dose. Taking the dose is still allowed
    /// (stock is clamped at 0 by MedicineService); the UI shows a hint, not a disabled button.
    /// </summary>
    public bool IsOutOfStock
    {
        get => _isOutOfStock;
        set
        {
            _isOutOfStock = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasInsufficientStock));
            OnPropertyChanged(nameof(ShowOutOfStockWarning));
            OnPropertyChanged(nameof(OutOfStockText));
        }
    }

    /// <summary>Alias of <see cref="IsOutOfStock"/> kept for existing bindings; it no longer blocks taking a dose.</summary>
    public bool HasInsufficientStock => IsOutOfStock;

    public bool ShowOutOfStockWarning => IsOutOfStock && !IsLogged;

    public string OutOfStockText => IsOutOfStock ? "Out of stock. Refill soon." : string.Empty;

    /// <summary>Taken today (logged and WasTaken).</summary>
    public bool IsDone => IsLogged && WasTaken;

    /// <summary>Auto-logged as missed but not taken; the Take button stays available and flips the log.</summary>
    public bool IsMissed => IsLogged && !WasTaken;

    /// <summary>Take is offered for every slot that has not been taken yet (early, due, overdue or missed).</summary>
    public bool CanTake => !IsDone;

    public bool CanActNow
    {
        get
        {
            if (IsLogged) return false;
            if (!DateTimeHelper.TryParseTime(ScheduleTime, out var scheduled)) return false;
            var now = DateTime.Now.TimeOfDay;
            return now >= scheduled.Subtract(DueWindowBefore) && now <= scheduled.Add(OverdueAfter);
        }
    }

    /// <summary>Not logged and more than two hours past the slot.</summary>
    public bool IsOverdue
    {
        get
        {
            if (IsLogged) return false;
            if (!DateTimeHelper.TryParseTime(ScheduleTime, out var scheduled)) return false;
            return DateTime.Now.TimeOfDay > scheduled.Add(OverdueAfter);
        }
    }

    public TodayDoseGroupKind GroupKind
    {
        get
        {
            if (IsLogged) return TodayDoseGroupKind.Done;
            if (IsOverdue || CanActNow) return TodayDoseGroupKind.DueNow;
            return TodayDoseGroupKind.Later;
        }
    }

    public string StatusText
    {
        get
        {
            if (IsLogged) return WasTaken ? "Taken" : "Missed";
            if (!DateTimeHelper.TryParseTime(ScheduleTime, out _)) return "Upcoming";
            if (IsOverdue) return "Overdue";
            if (CanActNow) return "Due now";
            return "Upcoming";
        }
    }

    /// <summary>Material icon file for the current state.</summary>
    public string StatusIcon
    {
        get
        {
            if (IsLogged) return WasTaken ? MedicineIcons.Check : MedicineIcons.Cancel;
            if (!DateTimeHelper.TryParseTime(ScheduleTime, out _)) return MedicineIcons.Schedule;
            if (IsOverdue) return MedicineIcons.Warning;
            if (CanActNow) return StockUnitImage;
            return MedicineIcons.Schedule;
        }
    }

    /// <summary>Colour token name for the current state; bind with ThemeColorConverter.</summary>
    public string StatusColor
    {
        get
        {
            if (IsLogged) return WasTaken ? MedicineIcons.TokenSuccess : MedicineIcons.TokenDanger;
            if (!DateTimeHelper.TryParseTime(ScheduleTime, out _)) return MedicineIcons.TokenSecondary;
            if (IsOverdue) return MedicineIcons.TokenWarning;
            if (CanActNow) return MedicineIcons.TokenPrimary;
            return MedicineIcons.TokenSecondary;
        }
    }

    public string TimeUntilText
    {
        get
        {
            if (IsLogged) return WasTaken ? "Taken" : "Missed";
            if (!DateTimeHelper.TryParseTime(ScheduleTime, out var scheduled)) return ScheduleTime;
            var diff = scheduled - DateTime.Now.TimeOfDay;
            var mins = diff.TotalMinutes;
            if (mins > 120) return $"in {(int)diff.TotalHours}h {diff.Minutes}m";
            if (mins > 60) return $"in {(int)diff.TotalHours}h";
            if (mins > 5) return $"in {(int)mins} min";
            if (mins >= -5) return "Due now";
            if (mins >= -60) return $"{(int)Math.Abs(mins)} min ago";
            if (mins >= -120) return $"{(int)Math.Abs(diff.TotalHours)}h ago";
            return $"{(int)Math.Abs(diff.TotalHours)}h overdue";
        }
    }

    public string TimeUntilLabel
    {
        get
        {
            var t = TimeUntilText;
            if (string.IsNullOrEmpty(t) || t == "Due now") return string.Empty;
            return $"({t})";
        }
    }

    public void RefreshWindow()
    {
        OnPropertyChanged(nameof(CanActNow));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(GroupKind));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(TimeUntilText));
        OnPropertyChanged(nameof(TimeUntilLabel));
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsMissed));
        OnPropertyChanged(nameof(CanTake));
        OnPropertyChanged(nameof(ShowOutOfStockWarning));
        RefreshWindow();
    }
}
