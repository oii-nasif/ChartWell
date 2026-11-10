using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Repositories;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

public class TodayViewModel : BaseViewModel
{
    private readonly MedicineService _medicineService;
    private readonly IReminderService _reminderService;
    private readonly LabService _labService;
    private readonly DoctorService _doctorService;
    private readonly UserProfileRepository _profileRepo;
    private string _firstName = string.Empty;
    private int _takenDoses;
    private int _totalDoses;
    private bool _isRefreshing;
    private DateTime _loadedDate = DateTime.MinValue;

    /// <summary>Flat list of today's reachable dose slots (source for <see cref="Groups"/> and the counters).</summary>
    public ObservableCollection<TodayScheduleItem> Items { get; } = new();

    /// <summary>Grouped view bound to the CollectionView: Due now / Later today / Done (empty groups omitted).</summary>
    public ObservableCollection<TodayDoseGroup> Groups { get; } = new();

    public string TodayDate => DateTime.Today.ToString("dddd, dd MMM", CultureInfo.InvariantCulture);

    public string Greeting
    {
        get
        {
            var greeting = DateTime.Now.Hour switch
            {
                < 5 => "Good night",
                < 12 => "Good morning",
                < 17 => "Good afternoon",
                _ => "Good evening",
            };
            return _firstName.Length > 0 ? $"{greeting}, {_firstName}" : greeting;
        }
    }

    // ── Insight cards under the hero (each loads independently of the dose list) ────────────

    private string _labAttentionTitle = string.Empty;
    private string _labAttentionDetail = string.Empty;
    /// <summary>"3 lab results to check"; empty when every latest result is in range.</summary>
    public string LabAttentionTitle
    {
        get => _labAttentionTitle;
        private set { _labAttentionTitle = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLabAttention)); }
    }
    public string LabAttentionDetail
    {
        get => _labAttentionDetail;
        private set { _labAttentionDetail = value; OnPropertyChanged(); }
    }
    public bool HasLabAttention => _labAttentionTitle.Length > 0;

    private string _followUpTitle = string.Empty;
    private string _followUpDetail = string.Empty;
    /// <summary>"Follow-up with Dr. Rahman"; empty when no follow-up is due in the next two weeks.</summary>
    public string FollowUpTitle
    {
        get => _followUpTitle;
        private set { _followUpTitle = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFollowUp)); }
    }
    public string FollowUpDetail
    {
        get => _followUpDetail;
        private set { _followUpDetail = value; OnPropertyChanged(); }
    }
    public bool HasFollowUp => _followUpTitle.Length > 0;

    public int TakenDoses
    {
        get => _takenDoses;
        private set { _takenDoses = value; OnPropertyChanged(); OnPropertyChanged(nameof(DayProgress)); OnPropertyChanged(nameof(SummaryText)); }
    }

    public int TotalDoses
    {
        get => _totalDoses;
        private set { _totalDoses = value; OnPropertyChanged(); OnPropertyChanged(nameof(DayProgress)); OnPropertyChanged(nameof(SummaryText)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HasDoses)); }
    }

    public bool HasDoses => TotalDoses > 0;

    public double DayProgress => TotalDoses > 0 ? Math.Min(1.0, (double)TakenDoses / TotalDoses) : 0;

    public string SummaryText => TotalDoses == 0
        ? "No doses scheduled today"
        : $"{TakenDoses} of {TotalDoses} doses taken";

    private bool _hasLoadedOnce;
    /// <summary>Only true after the first load, so the empty state doesn't flash before the data arrives.</summary>
    public bool IsEmpty => _hasLoadedOnce && !IsBusy && TotalDoses == 0;

    private string _nextDoseText = string.Empty;
    public string NextDoseText
    {
        get => _nextDoseText;
        private set { _nextDoseText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasNextDose)); }
    }
    public bool HasNextDose => !string.IsNullOrEmpty(NextDoseText);

    /// <summary>Bound to RefreshView.IsRefreshing; separate from IsBusy so a pull does not short-circuit Load.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { if (_isRefreshing == value) return; _isRefreshing = value; OnPropertyChanged(); }
    }

    /// <summary>True once the calendar day has moved on since the last Load.</summary>
    public bool NeedsReloadForNewDay => _loadedDate != DateTime.Today;

    public ICommand LoadCommand { get; }
    public ICommand TakeDoseCommand { get; }
    public ICommand AddMedicineCommand { get; }

    /// <summary>Raised after a dose was logged (argument: medicine name) so the page can show a toast.</summary>
    public event EventHandler<string>? DoseLogged;

    /// <summary>Raised by the empty-state action; the page navigates (ViewModels never navigate).</summary>
    public event EventHandler? AddMedicineRequested;

    public TodayViewModel(MedicineService medicineService, IReminderService reminderService, LabService labService,
        DoctorService doctorService, UserProfileRepository profileRepo)
    {
        _medicineService = medicineService;
        _reminderService = reminderService;
        _labService = labService;
        _doctorService = doctorService;
        _profileRepo = profileRepo;
        LoadCommand = new Command(async () => await Load());
        TakeDoseCommand = new Command<TodayScheduleItem>(async item => await TakeDose(item));
        AddMedicineCommand = new Command(() => AddMedicineRequested?.Invoke(this, EventArgs.Empty));
    }

    public async Task Load()
    {
        if (IsBusy) { IsRefreshing = false; return; }
        IsBusy = true; ClearError();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TodayDate)); // the Today tab is reused across days
        OnPropertyChanged(nameof(Greeting));
        try
        {
            await _medicineService.AutoLogMissedDoses();
            await _medicineService.CheckAndCompleteCourses();
            var medicines = await _medicineService.GetAllActiveMedicines();
            var allItems = new List<TodayScheduleItem>();
            var today = DateTime.Today;

            foreach (var med in medicines)
            {
                var schedules = await _medicineService.GetSchedulesForMedicine(med.Id);
                var reachable = MedicineService.GetReachableSlotsForDay(med, schedules, today);
                if (reachable.Count == 0) continue;

                var todayLogs = await _medicineService.GetLogsForMedicine(med.Id, today, today.AddDays(1).AddSeconds(-1));
                var outOfStock = _medicineService.IsOutOfStock(med);

                foreach (var sched in reachable)
                {
                    var log = todayLogs.FirstOrDefault(l => l.ScheduleId == sched.Id);
                    allItems.Add(new TodayScheduleItem
                    {
                        MedicineId = med.Id,
                        ScheduleId = sched.Id,
                        MedicineName = med.Name,
                        StockUnit = med.StockUnit,
                        Instructions = med.Instructions,
                        ScheduleTime = sched.ScheduleTime,
                        IsLogged = log != null,
                        WasTaken = log?.WasTaken == 1,
                        IsOutOfStock = outOfStock,
                    });
                }
            }

            Items.Clear();
            foreach (var item in allItems.OrderBy(i => i.GroupKind).ThenBy(i => i.ScheduleTime, StringComparer.Ordinal))
                Items.Add(item);

            RebuildGroups();
            RecalcCounts();
            _loadedDate = today;
        }
        catch (Exception ex) { SetError("Failed to load today's schedule."); System.Diagnostics.Debug.WriteLine(ex); }
        finally
        {
            _hasLoadedOnce = true;
            IsBusy = false;
            IsRefreshing = false;
            OnPropertyChanged(nameof(IsEmpty));
        }

        await LoadInsights();
    }

    /// <summary>
    /// Greeting name, lab results to check and the next follow-up. Each part fails on its own: a problem
    /// here must never hide today's doses.
    /// </summary>
    private async Task LoadInsights()
    {
        try
        {
            var name = (await _profileRepo.GetAsync()).FullName?.Trim() ?? string.Empty;
            _firstName = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            OnPropertyChanged(nameof(Greeting));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Today profile load failed: {ex}"); }

        try
        {
            var attention = await _labService.GetAttention();
            if (attention.Count == 0)
            {
                LabAttentionTitle = string.Empty;
                LabAttentionDetail = string.Empty;
            }
            else
            {
                var names = attention.Select(a => a.Latest.TestName).ToList();
                LabAttentionDetail = names.Count <= 3
                    ? string.Join(", ", names)
                    : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
                LabAttentionTitle = attention.Count == 1 ? "1 lab result to check" : $"{attention.Count} lab results to check";
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Today lab insight failed: {ex}"); }

        try
        {
            var next = (await _doctorService.GetAllVisits())
                .Select(v => (Visit: v, HasDate: DateTimeHelper.TryParseStored(v.FollowUpDate, out var d), Date: d.Date))
                .Where(x => x.HasDate && x.Date >= DateTime.Today && x.Date <= DateTime.Today.AddDays(14))
                .OrderBy(x => x.Date)
                .FirstOrDefault();
            if (next.Visit is null)
            {
                FollowUpTitle = string.Empty;
                FollowUpDetail = string.Empty;
            }
            else
            {
                FollowUpDetail = $"{DoctorVisitText.Countdown(next.Date)} · {next.Date.ToString("ddd, dd MMM", CultureInfo.InvariantCulture)}";
                FollowUpTitle = $"Follow-up with {next.Visit.DoctorName}";
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Today follow-up insight failed: {ex}"); }
    }

    /// <summary>Re-evaluates the time-based state of every row (called by the page's timer).</summary>
    public void RefreshWindows()
    {
        foreach (var item in Items)
            item.RefreshWindow();
        // Rows may have moved between "Later today" and "Due now".
        if (GroupsOutOfDate()) RebuildGroups();
        UpdateNextDose();
    }

    private async Task TakeDose(TodayScheduleItem? item)
    {
        if (item is null || !item.CanTake || item.IsBusy) return;
        item.IsBusy = true; ClearError();
        try
        {
            var log = await _medicineService.LogDoseTaken(item.MedicineId, item.ScheduleId);
            await _reminderService.CancelDoseReminders(item.MedicineId, item.ScheduleId);
            item.WasTaken = true;
            item.IsLogged = true;
            RebuildGroups();
            RecalcCounts();
#if ANDROID
            HealthTracker.HealthWidget.RequestUpdate();
#endif
            if (log != null) DoseLogged?.Invoke(this, item.MedicineName);
        }
        catch (Exception ex) { SetError("Couldn't log the dose. Please try again."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { item.IsBusy = false; }
    }

    private bool GroupsOutOfDate()
    {
        foreach (var group in Groups)
            foreach (var item in group)
                if (item.GroupKind != group.Kind) return true;
        return false;
    }

    private void RebuildGroups()
    {
        var ordered = Items.OrderBy(i => i.GroupKind).ThenBy(i => i.ScheduleTime, StringComparer.Ordinal).ToList();
        Groups.Clear();
        foreach (var kind in new[] { TodayDoseGroupKind.DueNow, TodayDoseGroupKind.Later, TodayDoseGroupKind.Done })
        {
            var members = ordered.Where(i => i.GroupKind == kind).ToList();
            if (members.Count > 0)
                Groups.Add(new TodayDoseGroup(kind, TodayDoseGroup.TitleFor(kind), members));
        }
    }

    private void RecalcCounts()
    {
        TakenDoses = Items.Count(i => i.IsDone);
        TotalDoses = Items.Count;
        UpdateNextDose();
    }

    private void UpdateNextDose()
    {
        if (Items.Count == 0) { NextDoseText = string.Empty; return; }

        var now = DateTime.Now.TimeOfDay;
        var upcoming = Items
            .Where(i => !i.IsLogged && Helpers.DateTimeHelper.TryParseTime(i.ScheduleTime, out var t) && t >= now)
            .OrderBy(i => i.ScheduleTime, StringComparer.Ordinal)
            .FirstOrDefault();

        if (upcoming != null)
        {
            NextDoseText = $"Next: {upcoming.MedicineName} at {upcoming.ScheduleTimeDisplay}";
            return;
        }

        var overdue = Items.Count(i => i.IsOverdue);
        if (overdue > 0) { NextDoseText = overdue == 1 ? "1 dose overdue" : $"{overdue} doses overdue"; return; }

        NextDoseText = Items.All(i => i.IsDone) ? "All doses taken today" : string.Empty;
    }
}
