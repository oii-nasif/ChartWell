using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

/// <summary>Display wrapper for a stored report row. <c>FilePath</c> is already absolute (see ReportService).</summary>
public class ReportListItem
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public ReportListItem(MedicalReport report, int pageCount)
    {
        Report = report;
        PageCount = Math.Max(1, pageCount);

        if (DateTimeHelper.TryParseStored(report.ReportDate, out var date))
        {
            DateText = date.ToString("dd MMM yyyy", Inv);
            MonthKey = date.ToString("yyyy-MM", Inv);
            MonthTitle = date.ToString("MMMM yyyy", Inv);
        }
        else
        {
            DateText = report.ReportDate;
            MonthKey = "0000-00";
            MonthTitle = "Undated";
        }

        DoctorHospitalText = string.Join(" · ",
            new[] { report.DoctorName, report.Hospital }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public MedicalReport Report { get; }
    public int Id => Report.Id;
    public string Title => Report.Title;
    public string ReportType => Report.ReportType;
    public string FilePath => Report.FilePath;
    public string FileType => Report.FileType;
    public int PageCount { get; }
    public string DateText { get; }
    public string MonthKey { get; }
    public string MonthTitle { get; }
    public string DoctorHospitalText { get; }
    public bool HasDoctorHospital => DoctorHospitalText.Length > 0;
    public bool HasMultiplePages => PageCount > 1;
    public string PageCountText => $"{PageCount} pages";
    public bool IsPdf => string.Equals(Report.FileType, "pdf", StringComparison.OrdinalIgnoreCase);
    public string IconSource => IsPdf ? "ic_picture_as_pdf.png" : "ic_image.png";
    public string TypeAndDateText => $"{ReportType} · {DateText}";
}

/// <summary>One month of reports, newest first. Bound by the grouped CollectionView.</summary>
public class ReportGroup : List<ReportListItem>
{
    public ReportGroup(string title, IEnumerable<ReportListItem> items) : base(items) => Title = title;
    public string Title { get; }
}

/// <summary>A chip in the type-filter row. Exposes theme token names, never colours.</summary>
public class ReportTypeFilter : BaseViewModel
{
    private bool _isSelected;

    public ReportTypeFilter(string label, string? value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    /// <summary>Null means "All".</summary>
    public string? Value { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackgroundToken));
            OnPropertyChanged(nameof(TextToken));
        }
    }

    public string BackgroundToken => _isSelected ? "PrimaryContainer" : "SurfaceVariant";
    public string TextToken => _isSelected ? "OnPrimaryContainer" : "TextSecondary";
}

public class ReportsViewModel : BaseViewModel
{
    private readonly ReportService _service;
    private readonly List<ReportListItem> _all = new();
    private string _searchText = string.Empty;
    private bool _isRefreshing;
    private ReportTypeFilter _selectedType;

    public ObservableCollection<ReportGroup> GroupedReports { get; } = new();
    public ObservableCollection<ReportTypeFilter> TypeFilters { get; } = new();

    public ICommand LoadCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand SelectTypeCommand { get; }

    /// <summary>Raised when the user asks to add a report (FAB or empty state); the page navigates.</summary>
    public event EventHandler? AddRequested;

    public ReportsViewModel(ReportService service)
    {
        _service = service;

        TypeFilters.Add(new ReportTypeFilter("All", null));
        foreach (var type in AppConstants.ReportTypes)
            TypeFilters.Add(new ReportTypeFilter(type, type));
        _selectedType = TypeFilters[0];
        _selectedType.IsSelected = true;

        LoadCommand = new Command(async () => await Load());
        RefreshCommand = new Command(async () => await Refresh());
        AddCommand = new Command(() => AddRequested?.Invoke(this, EventArgs.Empty));
        SelectTypeCommand = new Command<ReportTypeFilter>(SelectType);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value ?? string.Empty;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { _isRefreshing = value; OnPropertyChanged(); }
    }

    public bool HasAnyReports => _all.Count > 0;
    public bool IsFiltered => !string.IsNullOrWhiteSpace(_searchText) || _selectedType.Value is not null;

    private bool _hasLoadedOnce;
    /// <summary>Only true after the first load, so the empty state doesn't flash before the data arrives.</summary>
    public bool IsEmpty => _hasLoadedOnce && !IsBusy && GroupedReports.Count == 0;

    public string EmptyTitle => HasAnyReports ? "No matching reports" : "No reports yet";
    public string EmptyMessage => HasAnyReports
        ? "Try a different search or type filter."
        : "Add lab results, scans and prescriptions so they are always on hand.";
    public string EmptyIcon => HasAnyReports ? "ic_search.png" : "ic_description.png";
    public string EmptyActionText => HasAnyReports ? string.Empty : "Add report";

    public async Task Load()
    {
        if (IsBusy) return;
        IsBusy = true; ClearError();
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var reports = await _service.GetAllReports();
            var items = new List<ReportListItem>(reports.Count);
            foreach (var r in reports)
            {
                var pages = await _service.GetReportPages(r.Id);
                items.Add(new ReportListItem(r, pages.Count));
            }
            _all.Clear();
            _all.AddRange(items);
            ApplyFilter();
        }
        catch (Exception ex) { SetError("Could not load reports."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { _hasLoadedOnce = true; IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    private async Task Refresh()
    {
        try { await Load(); }
        finally { IsRefreshing = false; }
    }

    public void SelectType(ReportTypeFilter? filter)
    {
        if (filter is null || ReferenceEquals(filter, _selectedType)) return;
        _selectedType.IsSelected = false;
        _selectedType = filter;
        _selectedType.IsSelected = true;
        ApplyFilter();
    }

    /// <summary>Deletes the report and its files. Confirmation is the page's job. Returns true on success.</summary>
    public async Task<bool> Delete(ReportListItem item)
    {
        ClearError();
        try
        {
            await _service.DeleteReport(item.Id);
            _all.Remove(item);
            ApplyFilter();
            return true;
        }
        catch (Exception ex)
        {
            SetError("Could not delete the report.");
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private void ApplyFilter()
    {
        var query = _searchText.Trim();
        IEnumerable<ReportListItem> filtered = _all;

        if (_selectedType.Value is { } type)
            filtered = filtered.Where(r => string.Equals(r.ReportType, type, StringComparison.OrdinalIgnoreCase));

        if (query.Length > 0)
        {
            filtered = filtered.Where(r =>
                Contains(r.Title, query) ||
                Contains(r.Report.DoctorName, query) ||
                Contains(r.Report.Hospital, query) ||
                Contains(r.ReportType, query));
        }

        var groups = filtered
            .OrderByDescending(r => r.Report.ReportDate, StringComparer.Ordinal)
            .ThenByDescending(r => r.Report.CreatedAt, StringComparer.Ordinal)
            .GroupBy(r => r.MonthKey)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ReportGroup(g.First().MonthTitle, g))
            .ToList();

        GroupedReports.Clear();
        foreach (var g in groups) GroupedReports.Add(g);

        OnPropertyChanged(nameof(HasAnyReports));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(EmptyIcon));
        OnPropertyChanged(nameof(EmptyActionText));
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
