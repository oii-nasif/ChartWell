using System.Collections.ObjectModel;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

public class DoctorVisitDetailViewModel : BaseViewModel
{
    private readonly DoctorService _doctorService;
    private readonly ReportService _reportService;
    private DoctorVisit? _visit;

    public DoctorVisitDetailViewModel(DoctorService doctorService, ReportService reportService)
    {
        _doctorService = doctorService;
        _reportService = reportService;
        AttachReportCommand = new Command(() => AttachReportRequested?.Invoke(this, EventArgs.Empty));
    }

    public ObservableCollection<ReportListItem> Reports { get; } = new();

    public ICommand AttachReportCommand { get; }
    /// <summary>Raised from the "Attach report" section action; the page pushes AddReportPage.</summary>
    public event EventHandler? AttachReportRequested;

    /// <summary>The loaded visit; <c>PrescriptionImagePath</c> is absolute (see DoctorService).</summary>
    public DoctorVisit? Visit
    {
        get => _visit;
        private set
        {
            _visit = value;
            foreach (var name in new[]
                     {
                         nameof(Visit), nameof(HasVisit), nameof(DoctorName), nameof(Hospital), nameof(HasHospital),
                         nameof(VisitDateText), nameof(HasFollowUp), nameof(IsFollowUpUpcoming), nameof(FollowUpText),
                         nameof(Diagnosis), nameof(HasDiagnosis), nameof(Notes), nameof(HasNotes),
                         nameof(HasPrescriptionImage), nameof(PrescriptionImagePath), nameof(PrescriptionImage),
                         nameof(PrescriptionButtonText),
                     })
                OnPropertyChanged(name);
        }
    }

    public bool HasVisit => _visit is not null;
    public int VisitId => _visit?.Id ?? 0;
    public string DoctorName => _visit?.DoctorName ?? string.Empty;
    public string Hospital => _visit?.Hospital ?? string.Empty;
    public bool HasHospital => !string.IsNullOrWhiteSpace(_visit?.Hospital);
    public string VisitDateText => DoctorVisitText.LongDate(_visit?.VisitDate);

    public bool HasFollowUp => DateTimeHelper.TryParseStored(_visit?.FollowUpDate, out _);
    public bool IsFollowUpUpcoming =>
        DateTimeHelper.TryParseStored(_visit?.FollowUpDate, out var fu) && fu.Date >= DateTime.Today;
    public string FollowUpText =>
        DateTimeHelper.TryParseStored(_visit?.FollowUpDate, out var fu)
            ? $"Follow-up {DoctorVisitText.LongDate(_visit!.FollowUpDate)} · {DoctorVisitText.Countdown(fu)}"
            : string.Empty;

    public string Diagnosis => _visit?.Diagnosis ?? string.Empty;
    public bool HasDiagnosis => !string.IsNullOrWhiteSpace(_visit?.Diagnosis);
    public string Notes => _visit?.Notes ?? string.Empty;
    public bool HasNotes => !string.IsNullOrWhiteSpace(_visit?.Notes);

    public string? PrescriptionImagePath => _visit?.PrescriptionImagePath;
    public bool HasPrescriptionImage => !string.IsNullOrEmpty(PrescriptionImagePath) && File.Exists(PrescriptionImagePath);
    public ImageSource? PrescriptionImage => HasPrescriptionImage ? ImageSource.FromFile(PrescriptionImagePath!) : null;
    public string PrescriptionButtonText => HasPrescriptionImage ? "Replace image" : "Add image";

    public bool HasReports => Reports.Count > 0;

    public async Task Load(int visitId)
    {
        IsBusy = true; ClearError();
        try
        {
            Visit = await _doctorService.GetVisitById(visitId);
            if (Visit is null) { SetError("This visit no longer exists."); return; }

            var reports = await _reportService.GetReportsByVisit(visitId);
            Reports.Clear();
            foreach (var r in reports.OrderByDescending(r => r.ReportDate, StringComparer.Ordinal))
            {
                var pages = await _reportService.GetReportPages(r.Id);
                Reports.Add(new ReportListItem(r, pages.Count));
            }
            OnPropertyChanged(nameof(HasReports));
        }
        catch (Exception ex) { SetError("Could not load the visit."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>Stores a newly picked prescription image on the visit (DoctorService copies it into app storage).</summary>
    public async Task<bool> ReplacePrescriptionImage(string sourcePath)
    {
        if (_visit is null) return false;
        IsBusy = true; ClearError();
        try
        {
            _visit.PrescriptionImagePath = sourcePath;
            await _doctorService.UpdateVisit(_visit);
            Visit = await _doctorService.GetVisitById(_visit.Id) ?? _visit;
            return true;
        }
        catch (Exception ex)
        {
            SetError("Could not save the prescription image.");
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Deletes the visit; reminders are cancelled and linked reports are kept. Confirmation is the page's job.</summary>
    public async Task<bool> Delete()
    {
        if (_visit is null) return false;
        IsBusy = true; ClearError();
        try
        {
            await _doctorService.DeleteVisit(_visit.Id);
            return true;
        }
        catch (Exception ex)
        {
            SetError("Could not delete the visit.");
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
        finally { IsBusy = false; }
    }
}
