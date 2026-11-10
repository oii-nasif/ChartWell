using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

/// <summary>One timeline row, already mapped to theme tokens and icon names.</summary>
public class TimelineItem
{
    public int SourceId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string? SubText { get; init; }
    public bool HasSubText => !string.IsNullOrWhiteSpace(SubText);
    /// <summary>"ddd, dd MMM" plus "HH:mm" when the event has a time of day.</summary>
    public string When { get; init; } = string.Empty;
    /// <summary>Theme token name, resolved in XAML with ThemeColorConverter.</summary>
    public string ColorToken { get; init; } = "Primary";
    /// <summary>"ic_*.png" icon for the row.</summary>
    public string Icon { get; init; } = "ic_info.png";
}

/// <summary>Entries of one calendar month, newest first.</summary>
public class TimelineGroup : ObservableCollection<TimelineItem>
{
    public string Month { get; }
    public TimelineGroup(string month) : base() { Month = month; }
}

public class TimelineViewModel : BaseViewModel
{
    private readonly TimelineService _service;
    private const int PageSize = 20;
    private int _offset;
    private bool _hasMore = true;
    private bool _isLoadingMore;
    private bool _isRefreshing;
    private bool _hasLoadedOnce;

    public ObservableCollection<TimelineGroup> Groups { get; } = new();

    public ICommand LoadMoreCommand { get; }
    public ICommand RefreshCommand { get; }

    public bool HasMore
    {
        get => _hasMore;
        private set { _hasMore = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowLoadMore)); }
    }

    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set { _isLoadingMore = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowLoadMore)); }
    }

    public bool ShowLoadMore => HasMore && !IsLoadingMore && Groups.Count > 0;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { _isRefreshing = value; OnPropertyChanged(); }
    }

    public bool IsEmpty => _hasLoadedOnce && !IsBusy && Groups.Count == 0;

    public TimelineViewModel(TimelineService service)
    {
        _service = service;
        LoadMoreCommand = new Command(async () => await LoadMore());
        RefreshCommand = new Command(async () => await Refresh());
    }

    public async Task Load()
    {
        if (IsBusy) return;
        IsBusy = true;
        ClearError();
        try
        {
            var list = await _service.GetTimeline(PageSize, 0);
            Groups.Clear();
            AppendEntries(list);
            _offset = list.Count;
            HasMore = list.Count == PageSize;
        }
        catch (Exception ex)
        {
            SetError("Failed to load the timeline.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            _hasLoadedOnce = true;
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowLoadMore));
        }
    }

    private async Task Refresh()
    {
        try { await Load(); }
        finally { IsRefreshing = false; }
    }

    private async Task LoadMore()
    {
        if (IsBusy || IsLoadingMore || !HasMore) return;
        IsLoadingMore = true;
        ClearError();
        try
        {
            var list = await _service.GetTimeline(PageSize, _offset);
            AppendEntries(list);
            _offset += list.Count;
            HasMore = list.Count == PageSize;
        }
        catch (Exception ex)
        {
            SetError("Failed to load more.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsLoadingMore = false;
            OnPropertyChanged(nameof(ShowLoadMore));
        }
    }

    /// <summary>Adds entries to the last group when the month matches, otherwise starts a new group.</summary>
    private void AppendEntries(IEnumerable<TimelineEntry> entries)
    {
        foreach (var e in entries)
        {
            var month = string.IsNullOrWhiteSpace(e.MonthYearGroup) ? "Undated" : e.MonthYearGroup;
            var group = Groups.LastOrDefault();
            if (group is null || group.Month != month)
            {
                group = new TimelineGroup(month);
                Groups.Add(group);
            }
            group.Add(Map(e));
        }
    }

    private static TimelineItem Map(TimelineEntry e)
    {
        var (token, icon) = e.EventType switch
        {
            "dose_taken" => ("Success", "ic_medication.png"),
            "dose_missed" => ("Danger", "ic_medication.png"),
            "doctor_visit" => ("Info", "ic_medical_services.png"),
            "report_added" => ("Accent", "ic_description.png"),
            "metric_recorded" => ("Primary", "ic_monitor_heart.png"),
            "lab_results" => ("LabKidney", "ic_science.png"),
            "course_completed" => ("Success", "ic_task_alt.png"),
            "medicine_added" => ("Warning", "ic_add.png"),
            _ => ("Primary", "ic_info.png"),
        };

        return new TimelineItem
        {
            SourceId = e.SourceId,
            EventType = e.EventType,
            Title = e.DisplayLabel,
            Caption = e.Description,
            SubText = e.SubText,
            When = FormatWhen(e),
            ColorToken = token,
            Icon = icon,
        };
    }

    private static string FormatWhen(TimelineEntry e)
    {
        var inv = CultureInfo.InvariantCulture;
        // SortKey carries the full date-time when one exists; EventDate is date-only.
        if (DateTimeHelper.TryParseStored(e.SortKey, out var full))
        {
            var date = full.ToString("ddd, dd MMM", inv);
            var hasTime = full.TimeOfDay != TimeSpan.Zero && !string.IsNullOrEmpty(e.TimeText);
            return hasTime ? $"{date} · {full.ToString("HH:mm", inv)}" : date;
        }
        if (DateTimeHelper.TryParseStored(e.EventDate, out var d))
            return d.ToString("ddd, dd MMM", inv);
        return e.EventDate;
    }
}
