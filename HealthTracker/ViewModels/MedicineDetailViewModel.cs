using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

/// <summary>One row of the course history section.</summary>
public class CourseDisplayItem
{
    public string DateRangeText { get; set; } = string.Empty;
    public string SummaryText { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string StatusIcon { get; set; } = MedicineIcons.Check;
    /// <summary>Colour token name; bind through ThemeColorConverter.</summary>
    public string StatusColor { get; set; } = MedicineIcons.TokenSecondary;
    public bool IsActive { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsIncomplete { get; set; }
}

/// <summary>One row of the recent logs section.</summary>
public class DoseLogDisplayItem
{
    public string DateText { get; set; } = string.Empty;
    public string TimeText { get; set; } = string.Empty;
    public bool IsTaken { get; set; }
    public string StatusText => IsTaken ? "Taken" : "Missed";
    public string StatusIcon => IsTaken ? MedicineIcons.Check : MedicineIcons.Cancel;
    public string StatusColor => IsTaken ? MedicineIcons.TokenSuccess : MedicineIcons.TokenDanger;
    public string StockAfterText { get; set; } = string.Empty;
}

public class MedicineDetailViewModel : BaseViewModel
{
    private const int RecentLogDays = 14;

    private readonly MedicineService _service;
    private readonly IReminderService _reminderService;
    private Medicine? _medicine;
    private bool _isInactive;
    private bool _isRefreshing;

    public Medicine? Medicine { get => _medicine; set { _medicine = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(HasInstructions)); OnPropertyChanged(nameof(HasNotes)); } }
    public string Title => Medicine?.Name ?? "Medicine";
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Medicine?.Instructions);
    public bool HasNotes => !string.IsNullOrWhiteSpace(Medicine?.Notes);

    public bool IsInactive { get => _isInactive; set { _isInactive = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsActive)); } }
    public bool IsActive => !IsInactive;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { if (_isRefreshing == value) return; _isRefreshing = value; OnPropertyChanged(); }
    }

    // ── Header ───────────────────────────────────────────────────────────────

    private string _statusChipText = string.Empty;
    public string StatusChipText { get => _statusChipText; private set { _statusChipText = value; OnPropertyChanged(); } }

    /// <summary>Token for the status chip: Success (active), Warning (low stock), Danger (out of stock), TextSecondary (completed).</summary>
    private string _statusChipColor = MedicineIcons.TokenSuccess;
    public string StatusChipColor { get => _statusChipColor; private set { _statusChipColor = value; OnPropertyChanged(); } }

    private string _stockUnitImage = MedicineIcons.Medication;
    public string StockUnitImage { get => _stockUnitImage; private set { _stockUnitImage = value; OnPropertyChanged(); } }

    private string _scheduleSummaryText = string.Empty;
    public string ScheduleSummaryText { get => _scheduleSummaryText; private set { _scheduleSummaryText = value; OnPropertyChanged(); } }

    // ── Key facts ────────────────────────────────────────────────────────────

    private string _stockValueText = "0";
    public string StockValueText { get => _stockValueText; private set { _stockValueText = value; OnPropertyChanged(); } }
    private string _stockUnitText = string.Empty;
    public string StockUnitText { get => _stockUnitText; private set { _stockUnitText = value; OnPropertyChanged(); } }
    private string _stockDaysText = string.Empty;
    public string StockDaysText { get => _stockDaysText; private set { _stockDaysText = value; OnPropertyChanged(); } }

    private string _daysLeftValueText = "–";
    public string DaysLeftValueText { get => _daysLeftValueText; private set { _daysLeftValueText = value; OnPropertyChanged(); } }
    private string _daysLeftCaption = "days left";
    public string DaysLeftCaption { get => _daysLeftCaption; private set { _daysLeftCaption = value; OnPropertyChanged(); } }

    private string _adherenceValueText = "–";
    public string AdherenceValueText { get => _adherenceValueText; private set { _adherenceValueText = value; OnPropertyChanged(); } }

    private double _courseProgress;
    public double CourseProgress { get => _courseProgress; private set { _courseProgress = value; OnPropertyChanged(); } }
    private string _courseProgressText = string.Empty;
    public string CourseProgressText { get => _courseProgressText; private set { _courseProgressText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCourseProgress)); } }
    public bool HasCourseProgress => !string.IsNullOrEmpty(CourseProgressText);

    private bool _canBackfill;
    public bool CanBackfill { get => _canBackfill; private set { _canBackfill = value; OnPropertyChanged(); } }

    // ── Sections ─────────────────────────────────────────────────────────────

    public ObservableCollection<TodayScheduleItem> Schedules { get; } = new();
    public bool HasSchedules => Schedules.Count > 0;
    public ObservableCollection<DoseLogDisplayItem> RecentLogs { get; } = new();
    public bool HasRecentLogs => RecentLogs.Count > 0;
    public ObservableCollection<CourseDisplayItem> Courses { get; } = new();
    public bool HasCourses => Courses.Count > 0;

    public ICommand LoadCommand { get; }
    public ICommand TakeDoseCommand { get; }
    public ICommand NewCourseCommand { get; }
    public ICommand BackfillPastDosesCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>Raised after a dose was logged (argument: medicine name) so the page can show a toast.</summary>
    public event EventHandler<string>? DoseLogged;
    /// <summary>Raised after the medicine was deleted; the page pops itself.</summary>
    public event EventHandler? Deleted;
    /// <summary>Raised with a short message after a non-navigating action (new course, backfill, deactivate).</summary>
    public event EventHandler<string>? ActionCompleted;

    public MedicineDetailViewModel(MedicineService service, IReminderService reminderService)
    {
        _service = service;
        _reminderService = reminderService;
        LoadCommand = new Command(async () => { if (Medicine != null) await Load(Medicine.Id); else IsRefreshing = false; });
        TakeDoseCommand = new Command<TodayScheduleItem>(async item => await TakeDose(item));
        NewCourseCommand = new Command(async () => await StartNewCourse());
        BackfillPastDosesCommand = new Command(async () => await BackfillPastDoses());
        DeactivateCommand = new Command(async () => await Deactivate());
        DeleteCommand = new Command(async () => await Delete());
    }

    public async Task Load(int medicineId)
    {
        if (IsBusy) { IsRefreshing = false; return; }
        IsBusy = true; ClearError();
        try
        {
            Medicine = await _service.GetMedicineById(medicineId);
            if (Medicine is null) { SetError("This medicine no longer exists."); return; }
            IsInactive = Medicine.IsActive == 0;
            StockUnitImage = MedicineIcons.ForStockUnit(Medicine.StockUnit);

            var today = DateTime.Today;
            var schedules = await _service.GetSchedulesForMedicine(medicineId);
            var activeSchedules = schedules.Where(s => s.IsActive == 1).OrderBy(s => s.ScheduleTime, StringComparer.Ordinal).ToList();
            var todayLogs = await _service.GetLogsForMedicine(medicineId, today, today.AddDays(1).AddSeconds(-1));
            var outOfStock = _service.IsOutOfStock(Medicine);

            // Today's schedule (active medicines only)
            Schedules.Clear();
            if (!IsInactive)
            {
                foreach (var s in MedicineService.GetReachableSlotsForDay(Medicine, schedules, today))
                {
                    var log = todayLogs.FirstOrDefault(l => l.ScheduleId == s.Id);
                    Schedules.Add(new TodayScheduleItem
                    {
                        ScheduleId = s.Id,
                        MedicineId = s.MedicineId,
                        MedicineName = Medicine.Name,
                        StockUnit = Medicine.StockUnit,
                        Instructions = Medicine.Instructions,
                        ScheduleTime = s.ScheduleTime,
                        IsLogged = log != null,
                        WasTaken = log?.WasTaken == 1,
                        IsOutOfStock = outOfStock,
                    });
                }
            }
            OnPropertyChanged(nameof(HasSchedules));

            ScheduleSummaryText = activeSchedules.Count == 0
                ? "No schedule"
                : $"{activeSchedules.Count}x daily · " + string.Join(", ", activeSchedules.Select(s =>
                    DateTimeHelper.TryParseTime(s.ScheduleTime, out var t) ? DateTimeHelper.ToTimeString(t) : s.ScheduleTime));

            // Key facts
            var stockDays = _service.GetStockDaysRemaining(Medicine, activeSchedules.Count);
            StockValueText = Medicine.StockCount.ToString("0.##", CultureInfo.InvariantCulture);
            StockUnitText = string.IsNullOrEmpty(Medicine.StockUnit) ? "in stock" : Medicine.StockUnit.ToLowerInvariant() + (Medicine.StockCount == 1 ? "" : "s");
            StockDaysText = stockDays == double.MaxValue ? string.Empty
                : stockDays < 1 ? "Refill needed"
                : $"about {Math.Floor(stockDays):0} day{(Math.Floor(stockDays) == 1 ? "" : "s")} of doses";

            if (Medicine.IsOngoing == 1)
            {
                DaysLeftValueText = "Ongoing";
                DaysLeftCaption = "no end date";
            }
            else if (DateTimeHelper.TryParseStored(Medicine.EndDate, out var end))
            {
                var left = (end.Date - today).Days + 1;
                DaysLeftValueText = IsInactive ? "Done" : Math.Max(0, left).ToString();
                DaysLeftCaption = IsInactive ? $"ended {end.ToString("dd MMM", CultureInfo.InvariantCulture)}" : $"days left · ends {end.ToString("dd MMM", CultureInfo.InvariantCulture)}";
            }
            else
            {
                DaysLeftValueText = "–";
                DaysLeftCaption = "days left";
            }

            var logs30 = await _service.GetLogsForMedicine(medicineId, today.AddDays(-29), today.AddDays(1).AddSeconds(-1));
            AdherenceValueText = logs30.Count > 0
                ? $"{(int)Math.Round(100.0 * logs30.Count(l => l.WasTaken == 1) / logs30.Count)}%"
                : "–";

            // Course history + active course progress
            var courseList = await _service.GetCoursesForMedicine(medicineId);
            var activeCourse = courseList.FirstOrDefault(c => c.Status == "Active");
            if (activeCourse is { TotalScheduled: > 0 })
            {
                CourseProgress = Math.Min(1.0, (double)activeCourse.TotalTaken / activeCourse.TotalScheduled);
                CourseProgressText = $"{activeCourse.TotalTaken} of {activeCourse.TotalScheduled} doses in this course";
            }
            else
            {
                CourseProgress = 0;
                CourseProgressText = string.Empty;
            }

            Courses.Clear();
            foreach (var c in courseList.OrderByDescending(c => c.StartDate, StringComparer.Ordinal))
                Courses.Add(BuildCourseItem(c));
            OnPropertyChanged(nameof(HasCourses));

            // Recent logs (last 14 days), newest first
            var logs = await _service.GetLogsForMedicine(medicineId, today.AddDays(-(RecentLogDays - 1)), today.AddDays(1).AddSeconds(-1));
            RecentLogs.Clear();
            foreach (var l in logs.OrderByDescending(l => l.ScheduledFor, StringComparer.Ordinal))
            {
                var ok = DateTimeHelper.TryParseStored(l.ScheduledFor, out var when);
                RecentLogs.Add(new DoseLogDisplayItem
                {
                    DateText = ok ? when.ToString("ddd, dd MMM", CultureInfo.InvariantCulture) : l.ScheduledFor,
                    TimeText = ok ? when.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty,
                    IsTaken = l.WasTaken == 1,
                    StockAfterText = l.WasTaken == 1 ? $"{l.StockAfter.ToString("0.##", CultureInfo.InvariantCulture)} left" : string.Empty,
                });
            }
            OnPropertyChanged(nameof(HasRecentLogs));

            // Status chip
            if (IsInactive) { StatusChipText = "Completed"; StatusChipColor = MedicineIcons.TokenSecondary; }
            else if (outOfStock) { StatusChipText = "Out of stock"; StatusChipColor = MedicineIcons.TokenDanger; }
            else if (_service.IsLowStock(Medicine, schedules)) { StatusChipText = "Low stock"; StatusChipColor = MedicineIcons.TokenWarning; }
            else { StatusChipText = "Active"; StatusChipColor = MedicineIcons.TokenSuccess; }

            CanBackfill = !IsInactive
                && DateTimeHelper.TryParseStored(Medicine.StartDate, out var start)
                && start.Date < today;
        }
        catch (Exception ex) { SetError("Failed to load medicine details."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; IsRefreshing = false; }
    }

    private static CourseDisplayItem BuildCourseItem(MedicineCourse c)
    {
        var startDisplay = DateTimeHelper.TryParseStored(c.StartDate, out var sd) ? sd.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : c.StartDate;
        var endDisplay = DateTimeHelper.TryParseStored(c.EndDate, out var ed) ? ed.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : (c.Status == "Active" ? "Ongoing" : "–");

        var dosesText = c.TotalScheduled > 0
            ? $"{c.TotalTaken}/{c.TotalScheduled} doses taken"
            : $"{c.TotalTaken} doses taken";

        var parts = new List<string> { c.PrescribedDays > 0 ? $"{c.PrescribedDays} days" : "Ongoing" };
        if (c.SchedulesPerDay > 0) parts.Add($"{c.SchedulesPerDay}x daily");
        parts.Add(dosesText);

        var item = new CourseDisplayItem
        {
            DateRangeText = $"{startDisplay} – {endDisplay}",
            SummaryText = string.Join(" · ", parts),
            IsActive = c.Status == "Active",
        };

        if (c.Status == "Active")
        {
            item.StatusText = "Active"; item.StatusIcon = MedicineIcons.Schedule; item.StatusColor = MedicineIcons.TokenPrimary;
        }
        else if (c.TotalScheduled > 0 && c.TotalTaken >= c.TotalScheduled)
        {
            item.StatusText = "Completed"; item.StatusIcon = MedicineIcons.Check; item.StatusColor = MedicineIcons.TokenSuccess; item.IsCompleted = true;
        }
        else
        {
            item.StatusText = "Incomplete"; item.StatusIcon = MedicineIcons.Warning; item.StatusColor = MedicineIcons.TokenWarning; item.IsIncomplete = true;
        }
        return item;
    }

    private async Task TakeDose(TodayScheduleItem? item)
    {
        if (item is null || !item.CanTake || item.IsBusy) return;
        item.IsBusy = true; ClearError();
        try
        {
            var log = await _service.LogDoseTaken(item.MedicineId, item.ScheduleId);
            await _reminderService.CancelDoseReminders(item.MedicineId, item.ScheduleId);
            item.WasTaken = true;
            item.IsLogged = true;
#if ANDROID
            HealthTracker.HealthWidget.RequestUpdate();
#endif
            if (log != null) DoseLogged?.Invoke(this, item.MedicineName);
            // Stock and logs changed; refresh the facts without blocking the row.
            if (Medicine != null) await Load(Medicine.Id);
        }
        catch (Exception ex) { SetError("Couldn't log the dose. Please try again."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { item.IsBusy = false; }
    }

    private async Task StartNewCourse()
    {
        if (Medicine is null) return;
        try
        {
            var result = await Shell.Current.DisplayPromptAsync(
                "Start new course",
                "How many days did the doctor prescribe?",
                "Start", "Cancel",
                placeholder: "e.g. 5",
                keyboard: Keyboard.Numeric);

            if (string.IsNullOrWhiteSpace(result)) return;
            if (!int.TryParse(result, out var days) || days <= 0)
            {
                SetError("Enter a whole number of days greater than zero.");
                return;
            }

            IsBusy = true; ClearError();
            await _service.StartNewCourse(Medicine.Id, days, DateTimeHelper.ToDateString(DateTime.Today));
            IsBusy = false;
            await Load(Medicine.Id);
#if ANDROID
            HealthTracker.HealthWidget.RequestUpdate();
#endif
            ActionCompleted?.Invoke(this, $"New {days}-day course started");
        }
        catch (Exception ex) { SetError("Failed to start new course."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    private async Task BackfillPastDoses()
    {
        if (Medicine is null) return;
        try
        {
            if (!DateTimeHelper.TryParseStored(Medicine.StartDate, out var startDate)) return;

            if (startDate.Date >= DateTime.Today)
            {
                await Shell.Current.DisplayAlert("No past dates", "This medicine started today, so there are no past doses to log.", "OK");
                return;
            }

            var pastDays = (DateTime.Today - startDate.Date).Days;
            var confirm = await Shell.Current.DisplayAlert(
                "Log past doses",
                $"Mark every dose from {startDate.ToString("dd MMM", CultureInfo.InvariantCulture)} to yesterday ({pastDays} day{(pastDays == 1 ? "" : "s")}) as taken?",
                "Mark as taken", "Cancel");
            if (!confirm) return;

            IsBusy = true; ClearError();
            var count = await _service.BackfillPastDoses(Medicine.Id, startDate, DateTime.Today.AddDays(-1));
            IsBusy = false;
            await Load(Medicine.Id);
#if ANDROID
            HealthTracker.HealthWidget.RequestUpdate();
#endif
            ActionCompleted?.Invoke(this, count == 0 ? "No past doses needed logging" : $"{count} past dose{(count == 1 ? "" : "s")} logged");
        }
        catch (Exception ex) { SetError("Failed to log past doses."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    private async Task Deactivate()
    {
        if (Medicine is null || IsInactive) return;
        try
        {
            var confirm = await Shell.Current.DisplayAlert(
                "Stop this medicine?",
                $"\"{Medicine.Name}\" will move to Past medicines and its reminders will be cancelled. History is kept and you can start a new course later.",
                "Stop", "Cancel");
            if (!confirm) return;

            IsBusy = true; ClearError();
            await _service.DeactivateMedicine(Medicine.Id);
            IsBusy = false;
            await Load(Medicine.Id);
#if ANDROID
            HealthTracker.HealthWidget.RequestUpdate();
#endif
            ActionCompleted?.Invoke(this, "Medicine stopped");
        }
        catch (Exception ex) { SetError("Failed to stop the medicine."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    private async Task Delete()
    {
        if (Medicine is null) return;
        try
        {
            var courses = await _service.GetCoursesForMedicine(Medicine.Id);
            var totalTaken = courses.Sum(c => c.TotalTaken);
            var message = totalTaken > 0
                ? $"\"{Medicine.Name}\" has {totalTaken} recorded dose{(totalTaken == 1 ? "" : "s")}. Deleting erases all of its history. This cannot be undone."
                : $"Delete \"{Medicine.Name}\"? Its schedule and reminders will be removed. This cannot be undone.";
            var confirm = await Shell.Current.DisplayAlert("Delete medicine", message, "Delete", "Cancel");
            if (!confirm) return;

            IsBusy = true; ClearError();
            await _service.DeleteMedicine(Medicine.Id);
#if ANDROID
            HealthTracker.HealthWidget.RequestUpdate();
#endif
            Deleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { SetError("Failed to delete the medicine."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }
}
