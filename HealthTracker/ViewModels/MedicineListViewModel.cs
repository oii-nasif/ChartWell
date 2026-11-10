using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

public enum MedicineSegment
{
    Active,
    Past,
    Pending,
}

/// <summary>A medicine detected by the prescription scanner that still needs the user's approval.</summary>
public class PendingMedicineItem
{
    public PendingMedicine Pending { get; }

    public PendingMedicineItem(PendingMedicine pending)
    {
        Pending = pending;
    }

    public string Name => Pending.Name;

    public string DetailText => string.Join(" · ", new[] { Pending.Form, Pending.Dosage, Pending.Frequency }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool HasDetail => !string.IsNullOrEmpty(DetailText);

    public string ScannedText => DateTimeHelper.TryParseStored(Pending.ScannedAt, out var dt)
        ? $"Scanned {dt.ToString("dd MMM", CultureInfo.InvariantCulture)}"
        : "Scanned";
}

public class MedicineListViewModel : BaseViewModel
{
    private readonly MedicineService _service;
    private readonly PendingMedicineRepository _pendingRepo;
    private MedicineSegment _selectedSegment = MedicineSegment.Active;
    private bool _isRefreshing;
    private MedicineItem? _dragging;

    public ObservableCollection<MedicineItem> Medicines { get; } = new();
    public ObservableCollection<MedicineItem> PastMedicines { get; } = new();
    public ObservableCollection<PendingMedicineItem> PendingMedicines { get; } = new();

    public bool HasPastMedicines => PastMedicines.Count > 0;
    public bool HasPendingMedicines => PendingMedicines.Count > 0;

    private bool _hasLoadedOnce;
    // Per-segment empty flags: only true after the first load so the empty state doesn't flash before data arrives.
    public bool IsActiveEmpty => _hasLoadedOnce && !IsBusy && Medicines.Count == 0;
    public bool IsPastEmpty => _hasLoadedOnce && !IsBusy && PastMedicines.Count == 0;
    public bool IsPendingEmpty => _hasLoadedOnce && !IsBusy && PendingMedicines.Count == 0;

    public MedicineSegment SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (_selectedSegment == value) return;
            _selectedSegment = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActiveSegment));
            OnPropertyChanged(nameof(IsPastSegment));
            OnPropertyChanged(nameof(IsPendingSegment));
        }
    }

    public bool IsActiveSegment => SelectedSegment == MedicineSegment.Active;
    public bool IsPastSegment => SelectedSegment == MedicineSegment.Past;
    public bool IsPendingSegment => SelectedSegment == MedicineSegment.Pending;

    public string ActiveSegmentText => Medicines.Count > 0 ? $"Active ({Medicines.Count})" : "Active";
    public string PastSegmentText => PastMedicines.Count > 0 ? $"Past ({PastMedicines.Count})" : "Past";
    public string PendingSegmentText => PendingMedicines.Count > 0 ? $"Review ({PendingMedicines.Count})" : "Review";

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { if (_isRefreshing == value) return; _isRefreshing = value; OnPropertyChanged(); }
    }

    public ICommand LoadCommand { get; }
    public ICommand SelectSegmentCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DismissPendingCommand { get; }
    public ICommand ApprovePendingCommand { get; }
    public ICommand DragStartedCommand { get; }
    public ICommand DropCommand { get; }
    public ICommand AddMedicineCommand { get; }
    public ICommand ScanPrescriptionCommand { get; }

    /// <summary>The page navigates to the medicine form pre-filled with this pending medicine.</summary>
    public event EventHandler<PendingMedicine>? ApprovePendingRequested;
    public event EventHandler? AddMedicineRequested;
    public event EventHandler? ScanPrescriptionRequested;

    public MedicineListViewModel(MedicineService service, PendingMedicineRepository pendingRepo)
    {
        _service = service;
        _pendingRepo = pendingRepo;
        LoadCommand = new Command(async () => await LoadMedicines());
        SelectSegmentCommand = new Command<string>(s =>
        {
            if (Enum.TryParse<MedicineSegment>(s, ignoreCase: true, out var seg)) SelectedSegment = seg;
        });
        DeleteCommand = new Command<MedicineItem>(async m => await Delete(m));
        DismissPendingCommand = new Command<PendingMedicineItem>(async p => await DismissPending(p));
        ApprovePendingCommand = new Command<PendingMedicineItem>(p =>
        {
            if (p != null) ApprovePendingRequested?.Invoke(this, p.Pending);
        });
        DragStartedCommand = new Command<MedicineItem>(item => _dragging = item);
        DropCommand = new Command<MedicineItem>(async target => await OnDrop(target));
        AddMedicineCommand = new Command(() => AddMedicineRequested?.Invoke(this, EventArgs.Empty));
        ScanPrescriptionCommand = new Command(() => ScanPrescriptionRequested?.Invoke(this, EventArgs.Empty));
    }

    public void SetSegment(MedicineSegment segment) => SelectedSegment = segment;

    public async Task LoadMedicines()
    {
        if (IsBusy) { IsRefreshing = false; return; }
        IsBusy = true; ClearError();
        RaiseEmptyFlags();
        try
        {
            await _service.CheckAndCompleteCourses();
            var today = DateTime.Today;

            // Active medicines — sorted by SortOrder descending (highest = first; new medicines get highest)
            var list = (await _service.GetAllActiveMedicines())
                .OrderByDescending(m => m.SortOrder)
                .ThenByDescending(m => m.Id)
                .ToList();
            var active = new List<MedicineItem>();
            foreach (var m in list)
                active.Add(await BuildActiveItem(m, today));

            Medicines.Clear();
            foreach (var item in active) Medicines.Add(item);

            // Past medicines
            var past = await _service.GetAllInactiveMedicines();
            PastMedicines.Clear();
            foreach (var m in past)
            {
                var courses = await _service.GetCoursesForMedicine(m.Id);
                var taken = courses.Sum(c => c.TotalTaken);
                PastMedicines.Add(new MedicineItem
                {
                    Medicine = m,
                    IsPast = true,
                    DoseProgress = 0,
                    DoseProgressText = string.Empty,
                    PastSummaryText = $"{courses.Count} course{(courses.Count == 1 ? "" : "s")} · {taken} dose{(taken == 1 ? "" : "s")} taken",
                    StockDaysRemaining = double.MaxValue,
                });
            }

            // Pending medicines from OCR scan
            var pending = await _pendingRepo.GetAllAsync();
            PendingMedicines.Clear();
            foreach (var p in pending.OrderByDescending(p => p.Id))
                PendingMedicines.Add(new PendingMedicineItem(p));

            RaiseCounts();
        }
        catch (Exception ex) { SetError("Failed to load medicines."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { _hasLoadedOnce = true; IsBusy = false; IsRefreshing = false; RaiseEmptyFlags(); }
    }

    private void RaiseEmptyFlags()
    {
        OnPropertyChanged(nameof(IsActiveEmpty));
        OnPropertyChanged(nameof(IsPastEmpty));
        OnPropertyChanged(nameof(IsPendingEmpty));
    }

    private async Task<MedicineItem> BuildActiveItem(Medicine m, DateTime today)
    {
        var schedules = await _service.GetSchedulesForMedicine(m.Id);
        var reachableToday = MedicineService.GetReachableSlotsForDay(m, schedules, today);
        var todayLogs = await _service.GetLogsForMedicine(m.Id, today, today.AddDays(1).AddSeconds(-1));

        var takenToday = todayLogs.Count(l => l.WasTaken == 1);
        var totalToday = reachableToday.Count;
        var progress = totalToday > 0 ? Math.Min(1.0, (double)takenToday / totalToday) : 0.0;

        var (streak, missedThisWeek) = await _service.GetAdherenceStats(m.Id);

        // Adherence over the last 7 days: taken / (taken + missed) among logged slots.
        var weekLogs = await _service.GetLogsForMedicine(m.Id, today.AddDays(-6), today.AddDays(1).AddSeconds(-1));
        int? adherence = weekLogs.Count > 0
            ? (int)Math.Round(100.0 * weekLogs.Count(l => l.WasTaken == 1) / weekLogs.Count)
            : null;

        // Course progress from the active course (fixed-length courses only)
        var courses = await _service.GetCoursesForMedicine(m.Id);
        var activeCourse = courses.FirstOrDefault(c => c.Status == "Active");
        double courseProgress = 0;
        string courseText = string.Empty;
        if (activeCourse is { TotalScheduled: > 0 })
        {
            courseProgress = Math.Min(1.0, (double)activeCourse.TotalTaken / activeCourse.TotalScheduled);
            courseText = $"Course {activeCourse.TotalTaken}/{activeCourse.TotalScheduled}";
        }
        else if (m.IsOngoing == 1)
        {
            courseText = "Ongoing";
        }

        return new MedicineItem
        {
            Medicine = m,
            DoseProgress = progress,
            DoseProgressText = totalToday > 0 ? $"{takenToday} of {totalToday} today" : "No doses left today",
            ScheduleTimes = schedules
                .Where(s => s.IsActive == 1)
                .OrderBy(s => s.ScheduleTime, StringComparer.Ordinal)
                .Select(s => DateTimeHelper.TryParseTime(s.ScheduleTime, out var t) ? DateTimeHelper.ToTimeString(t) : s.ScheduleTime)
                .ToList(),
            CourseProgress = courseProgress,
            CourseProgressText = courseText,
            AdherencePercent = adherence,
            StockDaysRemaining = _service.GetStockDaysRemaining(m, schedules.Count(s => s.IsActive == 1)),
            Streak = streak,
            MissedThisWeek = missedThisWeek,
        };
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasPastMedicines));
        OnPropertyChanged(nameof(HasPendingMedicines));
        OnPropertyChanged(nameof(ActiveSegmentText));
        OnPropertyChanged(nameof(PastSegmentText));
        OnPropertyChanged(nameof(PendingSegmentText));
        RaiseEmptyFlags();
    }

    private async Task Delete(MedicineItem? item)
    {
        if (item is null) return;
        // Runs inside an async-void Command: every awaited call must be guarded or a DB error crashes the app.
        try
        {
            var courses = await _service.GetCoursesForMedicine(item.Medicine.Id);
            var totalTaken = courses.Sum(c => c.TotalTaken);
            var hasHistory = courses.Any(c => c.Status == "Completed") || totalTaken > 0;

            var message = hasHistory
                ? $"\"{item.Medicine.Name}\" has {totalTaken} recorded dose{(totalTaken == 1 ? "" : "s")}. Deleting erases all of its history. This cannot be undone."
                : $"Delete \"{item.Medicine.Name}\"? Its schedule and reminders will be removed. This cannot be undone.";

            bool confirmed = await Shell.Current.DisplayAlert("Delete medicine", message, "Delete", "Cancel");
            if (!confirmed) return;

            IsBusy = true; ClearError();
            await _service.DeleteMedicine(item.Medicine.Id);
            Medicines.Remove(item);
            PastMedicines.Remove(item);
            RaiseCounts();
#if ANDROID
            HealthTracker.HealthWidget.RequestUpdate();
#endif
        }
        catch (Exception ex) { SetError("Failed to delete medicine."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    private async Task DismissPending(PendingMedicineItem? item)
    {
        if (item is null) return;
        try
        {
            var confirmed = await Shell.Current.DisplayAlert("Dismiss", $"Remove \"{item.Name}\" from the review list?", "Dismiss", "Cancel");
            if (!confirmed) return;
            await _pendingRepo.DeleteAsync(item.Pending);
            PendingMedicines.Remove(item);
            RaiseCounts();
        }
        catch (Exception ex) { SetError("Failed to dismiss pending medicine."); System.Diagnostics.Debug.WriteLine(ex); }
    }

    /// <summary>Removes a pending medicine after it has been saved as a real medicine.</summary>
    public async Task ApprovePending(PendingMedicine pending)
    {
        try
        {
            await _pendingRepo.DeleteAsync(pending);
            var item = PendingMedicines.FirstOrDefault(p => p.Pending.Id == pending.Id);
            if (item != null) PendingMedicines.Remove(item);
            RaiseCounts();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ApprovePending failed: {ex}"); }
    }

    private async Task OnDrop(MedicineItem? target)
    {
        try
        {
            if (_dragging is null || target is null || _dragging == target) return;

            var from = Medicines.IndexOf(_dragging);
            var to = Medicines.IndexOf(target);
            if (from < 0 || to < 0) return;

            Medicines.Move(from, to);

            // Reassign SortOrder: index 0 = highest value so sort DESC keeps this order
            var count = Medicines.Count;
            for (int i = 0; i < count; i++)
                Medicines[i].Medicine.SortOrder = count - i;

            foreach (var item in Medicines)
                await _service.UpdateSortOrder(item.Medicine.Id, item.Medicine.SortOrder);
        }
        catch (Exception ex) { SetError("Failed to save the new order."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { _dragging = null; }
    }
}
