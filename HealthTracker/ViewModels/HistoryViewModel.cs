using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Repositories;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

/// <summary>One course of a medicine inside a <see cref="MedicineHistoryGroup"/> card.</summary>
public class CourseSummaryRow
{
    public int MedicineId { get; set; }
    public string DateRange { get; set; } = string.Empty;
    public string SummaryLine { get; set; } = string.Empty;
    public double Progress { get; set; }
    public string ProgressText { get; set; } = string.Empty;
    /// <summary>Theme token for the progress bar and percentage.</summary>
    public string ProgressToken => Progress >= 1.0 ? "Success" : Progress > 0.5 ? "Primary" : "Warning";
}

/// <summary>A medicine with its courses in the selected period. Rendered as one card.</summary>
public class MedicineHistoryGroup
{
    public int MedicineId { get; }
    public string MedicineName { get; }
    public int TotalDoses { get; }
    public IReadOnlyList<CourseSummaryRow> Courses { get; }
    public string HeaderText => MedicineName;
    public string DosesText => TotalDoses == 1 ? "1 dose taken" : $"{TotalDoses} doses taken";

    public MedicineHistoryGroup(int id, string name, int totalDoses, IEnumerable<CourseSummaryRow> items)
    {
        MedicineId = id;
        MedicineName = name;
        TotalDoses = totalDoses;
        Courses = items.ToList();
    }
}

public class DoctorHistoryItem
{
    public int VisitId { get; set; }
    public string DoctorName { get; set; } = string.Empty;
    public string? Hospital { get; set; }
    public string VisitDate { get; set; } = string.Empty;
    public string? Diagnosis { get; set; }
    public string VisitDateDisplay => HistoryViewModel.DisplayDate(VisitDate);
    public string SubText => string.IsNullOrEmpty(Hospital) ? string.Empty : Hospital;
    public bool HasSubText => !string.IsNullOrEmpty(Hospital);
    public bool HasDiagnosis => !string.IsNullOrWhiteSpace(Diagnosis);
}

public class ReportHistoryItem
{
    public int ReportId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ReportType { get; set; } = string.Empty;
    public string ReportDate { get; set; } = string.Empty;
    public string? DoctorName { get; set; }
    public string ReportDateDisplay => HistoryViewModel.DisplayDate(ReportDate);
    public string SubText => string.IsNullOrEmpty(DoctorName) ? ReportType : $"{ReportType} · {DoctorName}";
    public string Icon => FileType.Equals("pdf", StringComparison.OrdinalIgnoreCase) ? "ic_picture_as_pdf.png" : "ic_image.png";
}

/// <summary>
/// Category history over a date range. One <see cref="Items"/> list feeds a single CollectionView; the
/// page picks the row template by item type.
/// </summary>
public class HistoryViewModel : BaseViewModel
{
    public const string CategoryMedicine = "Medicine";
    public const string CategoryDoctor = "Doctor";
    public const string CategoryReports = "Reports";

    private readonly MedicineService _medicineService;
    private readonly MedicineCourseRepository _courseRepo;
    private readonly MedicineLogRepository _logRepo;
    private readonly MedicineRepository _medicineRepo;
    private readonly DoctorService _doctorService;
    private readonly ReportService _reportService;

    private string _category = CategoryMedicine;
    public string Category
    {
        get => _category;
        set
        {
            if (_category == value) return;
            _category = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsMedicine));
            OnPropertyChanged(nameof(IsDoctor));
            OnPropertyChanged(nameof(IsReports));
            OnPropertyChanged(nameof(EmptyIcon));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }
    public bool IsMedicine => Category == CategoryMedicine;
    public bool IsDoctor => Category == CategoryDoctor;
    public bool IsReports => Category == CategoryReports;

    public string Title => "History";

    public string EmptyIcon => Category switch
    {
        CategoryDoctor => "ic_medical_services.png",
        CategoryReports => "ic_description.png",
        _ => "ic_medication.png",
    };
    public string EmptyTitle => Category switch
    {
        CategoryDoctor => "No doctor visits in this period",
        CategoryReports => "No reports in this period",
        _ => "No medicine history in this period",
    };
    public string EmptyMessage => "Try widening the date range with one of the presets above.";

    private DateTime _fromDate = DateTime.Today.AddDays(-30);
    private DateTime _toDate = DateTime.Today;

    public DateTime FromDate
    {
        get => _fromDate;
        set { if (_fromDate == value) return; _fromDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActivePreset)); }
    }

    public DateTime ToDate
    {
        get => _toDate;
        set { if (_toDate == value) return; _toDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActivePreset)); }
    }

    public DateTime MaxDate => DateTime.Today;

    /// <summary>"7d" / "30d" / "90d" / "year" or "" when the pickers don't match a preset. Drives chip highlight.</summary>
    public string ActivePreset
    {
        get
        {
            if (ToDate.Date != DateTime.Today) return string.Empty;
            if (FromDate.Date == DateTime.Today.AddDays(-7)) return "7d";
            if (FromDate.Date == DateTime.Today.AddDays(-30)) return "30d";
            if (FromDate.Date == DateTime.Today.AddDays(-90)) return "90d";
            if (FromDate.Date == new DateTime(DateTime.Today.Year, 1, 1)) return "year";
            return string.Empty;
        }
    }

    public ObservableCollection<object> Items { get; } = new();

    private bool _hasSearched;
    private CancellationTokenSource? _searchCts;
    public bool IsEmpty => _hasSearched && !IsBusy && Items.Count == 0;

    public ICommand SearchCommand { get; }
    public ICommand SetCategoryCommand { get; }
    public ICommand SetPresetCommand { get; }

    public HistoryViewModel(MedicineService medicineService, MedicineCourseRepository courseRepo,
        MedicineLogRepository logRepo, MedicineRepository medicineRepo,
        DoctorService doctorService, ReportService reportService)
    {
        _medicineService = medicineService;
        _courseRepo = courseRepo;
        _logRepo = logRepo;
        _medicineRepo = medicineRepo;
        _doctorService = doctorService;
        _reportService = reportService;
        SearchCommand = new Command(async () => await Search());
        SetCategoryCommand = new Command<string>(async c => { Category = c; await Search(); });
        SetPresetCommand = new Command<string>(async p => { ApplyPreset(p); await Search(); });
    }

    private void ApplyPreset(string? preset)
    {
        var today = DateTime.Today;
        (FromDate, ToDate) = preset switch
        {
            "7d" => (today.AddDays(-7), today),
            "30d" => (today.AddDays(-30), today),
            "90d" => (today.AddDays(-90), today),
            "year" => (new DateTime(today.Year, 1, 1), today),
            _ => (FromDate, ToDate),
        };
    }

    public static string DisplayDate(string stored)
        => DateTimeHelper.TryParseStored(stored, out var d) ? d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : stored;

    /// <summary>
    /// Runs a search for the current category and range. A new search cancels the previous one (a
    /// category or preset tap during an in-flight search must win), and a cancelled run never
    /// publishes its rows to <see cref="Items"/>.
    /// </summary>
    public async Task Search()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var ct = cts.Token;

        IsBusy = true;
        ClearError();
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var from = FromDate.Date;
            var to = ToDate.Date;
            if (to < from) (from, to) = (to, from);

            Items.Clear();
            var rows = new List<object>();
            if (IsMedicine) await LoadMedicineHistory(rows, from, to, ct);
            else if (IsDoctor) await LoadDoctorHistory(rows, from, to, ct);
            else if (IsReports) await LoadReportsHistory(rows, from, to, ct);

            if (ct.IsCancellationRequested) return;
            foreach (var row in rows) Items.Add(row);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search; it owns the UI state now.
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            SetError("Failed to load history.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                _hasSearched = true;
                IsBusy = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
            cts.Dispose();
        }
    }

    private async Task LoadMedicineHistory(List<object> rows, DateTime from, DateTime to, CancellationToken ct)
    {
        var allMedicines = await _medicineRepo.GetAllAsync();
        foreach (var med in allMedicines)
        {
            ct.ThrowIfCancellationRequested();
            var courses = await _courseRepo.GetByMedicineIdAsync(med.Id);
            var courseRows = new List<CourseSummaryRow>();
            int totalDoses = 0;

            foreach (var course in courses)
            {
                if (!DateTimeHelper.TryParseStored(course.StartDate, out var cs)) continue;
                var hasEnd = DateTimeHelper.TryParseStored(course.EndDate, out var ced);
                var ce = hasEnd ? ced : DateTime.MaxValue;

                // Check if course overlaps with date range
                if (cs.Date > to || ce.Date < from) continue;

                // Count logs in the overlapping window
                var overlapFrom = cs.Date > from ? cs.Date : from;
                var overlapTo = ce.Date < to ? ce.Date : to;

                var logs = await _logRepo.GetLogsForMedicineAsync(med.Id,
                    overlapFrom.ToString("yyyy-MM-ddT00:00:00", CultureInfo.InvariantCulture),
                    overlapTo.ToString("yyyy-MM-ddT23:59:59", CultureInfo.InvariantCulture));

                var taken = logs.Count(l => l.WasTaken == 1);
                var isActiveCourse = course.Status == "Active";

                // Skip completed courses with no doses in this period
                if (taken == 0 && !isActiveCourse) continue;

                totalDoses += taken;

                var startDisplay = cs.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
                var endDisplay = hasEnd ? ce.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : "Ongoing";
                var perDay = course.SchedulesPerDay > 0 ? $"{course.SchedulesPerDay}x daily" : "";
                var days = course.PrescribedDays > 0 ? $"{course.PrescribedDays} days" : "Ongoing";
                var expected = course.TotalScheduled > 0 ? course.TotalScheduled : (taken > 0 ? taken : 1);
                var progress = taken == 0 ? 0.0 : Math.Min(1.0, (double)taken / expected);
                var progressText = isActiveCourse && taken == 0 ? "Active" : $"{(int)(progress * 100)}%";

                var parts = new[] { days, perDay, taken == 0 ? "In progress" : $"{taken}/{expected} doses" }
                    .Where(p => !string.IsNullOrEmpty(p));

                courseRows.Add(new CourseSummaryRow
                {
                    MedicineId = med.Id,
                    DateRange = $"{startDisplay} – {endDisplay}",
                    SummaryLine = string.Join(" · ", parts),
                    Progress = progress,
                    ProgressText = progressText
                });
            }

            if (totalDoses > 0 || courseRows.Count > 0)
                rows.Add(new MedicineHistoryGroup(med.Id, med.Name, totalDoses, courseRows));
        }
    }

    private async Task LoadDoctorHistory(List<object> rows, DateTime from, DateTime to, CancellationToken ct)
    {
        var visits = await _doctorService.GetAllVisits();
        ct.ThrowIfCancellationRequested();
        foreach (var v in visits)
        {
            if (!DateTimeHelper.TryParseStored(v.VisitDate, out var vd)) continue;
            if (vd.Date < from || vd.Date > to) continue;
            rows.Add(new DoctorHistoryItem
            {
                VisitId = v.Id,
                DoctorName = v.DoctorName,
                Hospital = v.Hospital,
                VisitDate = v.VisitDate,
                Diagnosis = v.Diagnosis
            });
        }
    }

    private async Task LoadReportsHistory(List<object> rows, DateTime from, DateTime to, CancellationToken ct)
    {
        var reports = await _reportService.GetAllReports();
        ct.ThrowIfCancellationRequested();
        foreach (var r in reports)
        {
            if (!DateTimeHelper.TryParseStored(r.ReportDate, out var rd)) continue;
            if (rd.Date < from || rd.Date > to) continue;
            rows.Add(new ReportHistoryItem
            {
                ReportId = r.Id,
                FilePath = r.FilePath,
                FileType = r.FileType,
                Title = r.Title,
                ReportType = r.ReportType,
                ReportDate = r.ReportDate,
                DoctorName = r.DoctorName
            });
        }
    }
}
