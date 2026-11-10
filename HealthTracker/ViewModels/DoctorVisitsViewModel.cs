using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

/// <summary>Shared date wording for doctor-visit screens ("today", "tomorrow", "in 3 days", "3 days ago").</summary>
public static class DoctorVisitText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string LongDate(string? stored) =>
        DateTimeHelper.TryParseStored(stored, out var d) ? d.ToString("dd MMM yyyy", Inv) : stored ?? string.Empty;

    public static string ShortDate(string? stored) =>
        DateTimeHelper.TryParseStored(stored, out var d) ? d.ToString("ddd, dd MMM", Inv) : stored ?? string.Empty;

    public static string Countdown(DateTime date)
    {
        var days = (date.Date - DateTime.Today).Days;
        return days switch
        {
            0 => "today",
            1 => "tomorrow",
            -1 => "yesterday",
            > 1 => $"in {days} days",
            _ => $"{-days} days ago",
        };
    }
}

/// <summary>Display wrapper for a visit row. <c>PrescriptionImagePath</c> is already absolute (see DoctorService).</summary>
public class DoctorVisitListItem
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public DoctorVisitListItem(DoctorVisit visit)
    {
        Visit = visit;
        DateText = DoctorVisitText.LongDate(visit.VisitDate);

        if (DateTimeHelper.TryParseStored(visit.VisitDate, out var date))
        {
            MonthKey = date.ToString("yyyy-MM", Inv);
            MonthTitle = date.ToString("MMMM yyyy", Inv);
        }
        else
        {
            MonthKey = "0000-00";
            MonthTitle = "Undated";
        }

        HasFollowUp = DateTimeHelper.TryParseStored(visit.FollowUpDate, out var fu);
        FollowUpDate = HasFollowUp ? fu.Date : null;
        IsFollowUpUpcoming = HasFollowUp && fu.Date >= DateTime.Today;
        FollowUpText = HasFollowUp ? $"Follow-up {DoctorVisitText.Countdown(fu)}" : string.Empty;

        HasPrescription = !string.IsNullOrEmpty(visit.PrescriptionImagePath) && File.Exists(visit.PrescriptionImagePath);
        DiagnosisPreview = string.IsNullOrWhiteSpace(visit.Diagnosis)
            ? string.Empty
            : visit.Diagnosis.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    public DoctorVisit Visit { get; }
    public int Id => Visit.Id;
    public string DoctorName => Visit.DoctorName;
    public string Hospital => Visit.Hospital ?? string.Empty;
    public bool HasHospital => !string.IsNullOrWhiteSpace(Visit.Hospital);
    public string DateText { get; }
    public string MonthKey { get; }
    public string MonthTitle { get; }
    public string DiagnosisPreview { get; }
    public bool HasDiagnosis => DiagnosisPreview.Length > 0;
    public bool HasPrescription { get; }
    public bool HasFollowUp { get; }
    public DateTime? FollowUpDate { get; }
    public bool IsFollowUpUpcoming { get; }
    public string FollowUpText { get; }
}

public class DoctorVisitGroup : List<DoctorVisitListItem>
{
    public DoctorVisitGroup(string title, IEnumerable<DoctorVisitListItem> items) : base(items) => Title = title;
    public string Title { get; }
}

/// <summary>A visit whose follow-up is today or later, shown in the "Upcoming follow-ups" section.</summary>
public class DoctorFollowUpItem
{
    public DoctorFollowUpItem(DoctorVisitListItem visit)
    {
        VisitId = visit.Id;
        Headline = $"Follow-up with {visit.DoctorName}";
        var date = visit.FollowUpDate ?? DateTime.Today;
        CountdownText = DoctorVisitText.Countdown(date);
        DateText = date.ToString("ddd, dd MMM", CultureInfo.InvariantCulture);
        Detail = visit.HasHospital ? $"{visit.Hospital} · {DateText}" : DateText;
    }

    public int VisitId { get; }
    public string Headline { get; }
    public string CountdownText { get; }
    public string DateText { get; }
    public string Detail { get; }
}

public class DoctorVisitsViewModel : BaseViewModel
{
    private readonly DoctorService _service;
    private bool _isRefreshing;

    public ObservableCollection<DoctorFollowUpItem> UpcomingFollowUps { get; } = new();
    public ObservableCollection<DoctorVisitGroup> GroupedVisits { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand AddCommand { get; }

    /// <summary>Raised when the user asks to add a visit (FAB or empty state); the page navigates.</summary>
    public event EventHandler? AddRequested;

    public DoctorVisitsViewModel(DoctorService service)
    {
        _service = service;
        RefreshCommand = new Command(async () => await Refresh());
        AddCommand = new Command(() => AddRequested?.Invoke(this, EventArgs.Empty));
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { _isRefreshing = value; OnPropertyChanged(); }
    }

    public bool HasUpcomingFollowUps => UpcomingFollowUps.Count > 0;
    public bool HasVisits => GroupedVisits.Count > 0;

    private bool _hasLoadedOnce;
    /// <summary>Only true after the first load, so the empty state doesn't flash before the data arrives.</summary>
    public bool IsEmpty => _hasLoadedOnce && !IsBusy && GroupedVisits.Count == 0;

    public async Task Load()
    {
        if (IsBusy) return;
        IsBusy = true; ClearError();
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var visits = (await _service.GetAllVisits()).Select(v => new DoctorVisitListItem(v)).ToList();

            UpcomingFollowUps.Clear();
            foreach (var item in visits.Where(v => v.IsFollowUpUpcoming).OrderBy(v => v.FollowUpDate))
                UpcomingFollowUps.Add(new DoctorFollowUpItem(item));

            var groups = visits
                .OrderByDescending(v => v.Visit.VisitDate, StringComparer.Ordinal)
                .ThenByDescending(v => v.Visit.CreatedAt, StringComparer.Ordinal)
                .GroupBy(v => v.MonthKey)
                .OrderByDescending(g => g.Key, StringComparer.Ordinal)
                .Select(g => new DoctorVisitGroup(g.First().MonthTitle, g))
                .ToList();

            GroupedVisits.Clear();
            foreach (var g in groups) GroupedVisits.Add(g);

            OnPropertyChanged(nameof(HasUpcomingFollowUps));
            OnPropertyChanged(nameof(HasVisits));
        }
        catch (Exception ex) { SetError("Could not load doctor visits."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { _hasLoadedOnce = true; IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    private async Task Refresh()
    {
        try { await Load(); }
        finally { IsRefreshing = false; }
    }

    /// <summary>Deletes the visit (reminders cancelled, linked reports kept). Confirmation is the page's job.</summary>
    public async Task<bool> Delete(DoctorVisitListItem item)
    {
        ClearError();
        try
        {
            await _service.DeleteVisit(item.Id);
            await Reload();
            return true;
        }
        catch (Exception ex)
        {
            SetError("Could not delete the visit.");
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    private async Task Reload()
    {
        // Load() guards on IsBusy; make sure a reload after a mutation always runs.
        while (IsBusy) await Task.Delay(50);
        await Load();
    }
}
