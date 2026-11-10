using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services;
using HealthTracker.Services.Ai;

namespace HealthTracker.ViewModels;

/// <summary>A medicine read from the scanned prescription, shown read-only on the visit form.</summary>
public class DoctorVisitMedicineItem
{
    public DoctorVisitMedicineItem(AiPrescribedMedicine source)
    {
        Source = source;
        var parsed = PrescriptionParserService.FromAi(source);
        Name = parsed.Name;
        Detail = string.Join(" · ", new[]
        {
            parsed.Dosage,
            parsed.Frequency,
            source.DurationDays is { } d ? $"{d} days" : null,
            parsed.Instructions,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public AiPrescribedMedicine Source { get; }
    public string Name { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// Add + Edit doctor visit in one form. Call <see cref="Load"/> for edit mode; otherwise it adds.
/// Scanning a prescription (camera/gallery) fills empty fields: Gemini with the image when a key is
/// configured, otherwise on-device OCR plus regex heuristics. Dates the user already changed are kept.
/// </summary>
public class DoctorVisitFormViewModel : BaseViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly DoctorService _service;
    private readonly PrescriptionParserService _ocr;
    private readonly AiParserService _aiParser;
    private readonly PendingMedicineRepository _pendingRepo;

    private DoctorVisit? _visit;
    private bool _userEditedVisitDate;
    private bool _userEditedFollowUp;
    private bool _rescanRequested;

    private string _doctorName = string.Empty;
    private string? _hospital;
    private DateTime _visitDate = DateTime.Today;
    private string? _diagnosis;
    private string? _notes;
    private bool _hasFollowUp;
    // A follow-up is usually a week or more away; defaulting to today produced a "follow-up today" visit by accident.
    private DateTime _followUpDate = DateTime.Today.AddDays(7);
    private DateTime _followUpMinDate = DateTime.Today;
    private string? _prescriptionImagePath;

    private bool _isScanning;
    private string _scanStatus = string.Empty;
    private bool _isAiMode;
    private bool _showAiBanner;
    private string _aiNoticeText = string.Empty;
    private bool _showSettingsAction;
    private bool _medicinesSent;

    public DoctorVisitFormViewModel(DoctorService service, PrescriptionParserService ocr,
        AiParserService aiParser, PendingMedicineRepository pendingRepo)
    {
        _service = service;
        _ocr = ocr;
        _aiParser = aiParser;
        _pendingRepo = pendingRepo;

        SaveCommand = new Command(async () => await Save());
        PickImageCommand = new Command(async () => await PickImage());
        CaptureImageCommand = new Command(async () => await CaptureImage());
        RemoveImageCommand = new Command(() => PrescriptionImagePath = null);
        RescanCommand = new Command(() => { _rescanRequested = true; NotifyScanCardChanged(); });
        DismissBannerCommand = new Command(() => ShowAiBanner = false);
        OpenSettingsCommand = new Command(() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        AddToPendingCommand = new Command(async () => await AddMedicinesToPending());
    }

    // ----- Mode --------------------------------------------------------------------------------

    public bool IsEditMode => _visit is not null;
    public string PageTitle => IsEditMode ? "Edit visit" : "Add visit";
    public string SaveButtonText => IsEditMode ? "Save changes" : "Save visit";
    public bool ShowScanCard => !IsEditMode || _rescanRequested;
    public bool ShowRescanButton => IsEditMode && !_rescanRequested;

    // ----- Fields ------------------------------------------------------------------------------

    public string DoctorName { get => _doctorName; set { _doctorName = value ?? string.Empty; OnPropertyChanged(); } }
    public string? Hospital { get => _hospital; set { _hospital = value; OnPropertyChanged(); } }
    public string? Diagnosis { get => _diagnosis; set { _diagnosis = value; OnPropertyChanged(); } }
    public string? Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }

    /// <summary>Set by the DatePicker; marks the date as user-chosen so a scan never overwrites it.</summary>
    public DateTime VisitDate
    {
        get => _visitDate;
        set
        {
            if (_visitDate.Date == value.Date) return;
            _visitDate = value.Date;
            _userEditedVisitDate = true;
            OnPropertyChanged();
        }
    }

    public bool HasFollowUp
    {
        get => _hasFollowUp;
        set { _hasFollowUp = value; OnPropertyChanged(); }
    }

    public DateTime FollowUpDate
    {
        get => _followUpDate;
        set
        {
            if (_followUpDate.Date == value.Date) return;
            _followUpDate = value.Date;
            _userEditedFollowUp = true;
            OnPropertyChanged();
        }
    }

    /// <summary>Today, or the stored follow-up when editing a visit whose follow-up already passed.</summary>
    public DateTime FollowUpMinDate
    {
        get => _followUpMinDate;
        private set { _followUpMinDate = value; OnPropertyChanged(); }
    }

    public string? PrescriptionImagePath
    {
        get => _prescriptionImagePath;
        set
        {
            _prescriptionImagePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(PrescriptionThumbnail));
        }
    }

    public bool HasImage => !string.IsNullOrEmpty(_prescriptionImagePath) && File.Exists(_prescriptionImagePath);
    public ImageSource? PrescriptionThumbnail => HasImage ? ImageSource.FromFile(_prescriptionImagePath!) : null;

    // ----- Scan state --------------------------------------------------------------------------

    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowProgressRow)); }
    }

    public string ScanStatus
    {
        get => _scanStatus;
        set
        {
            _scanStatus = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasScanStatus));
            OnPropertyChanged(nameof(ShowProgressRow));
        }
    }

    public bool HasScanStatus => _scanStatus.Length > 0;
    public bool ShowProgressRow => _isScanning || HasScanStatus;

    public bool IsAiMode
    {
        get => _isAiMode;
        set { _isAiMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanModeLabel)); }
    }

    public string ScanModeLabel => _isAiMode ? ReportAiStatus.AiModeLabel : ReportAiStatus.OcrModeLabel;

    public bool ShowAiBanner { get => _showAiBanner; set { _showAiBanner = value; OnPropertyChanged(); } }

    public string AiNoticeText
    {
        get => _aiNoticeText;
        set { _aiNoticeText = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(HasAiNotice)); }
    }

    public bool HasAiNotice => _aiNoticeText.Length > 0;
    public bool ShowSettingsAction { get => _showSettingsAction; set { _showSettingsAction = value; OnPropertyChanged(); } }

    // ----- Medicines ---------------------------------------------------------------------------

    public ObservableCollection<DoctorVisitMedicineItem> Medicines { get; } = new();
    public bool HasMedicines => Medicines.Count > 0;

    public bool MedicinesSent
    {
        get => _medicinesSent;
        set
        {
            _medicinesSent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSendMedicines));
            OnPropertyChanged(nameof(AddToPendingText));
        }
    }

    public bool CanSendMedicines => !_medicinesSent && HasMedicines;
    public string AddToPendingText => _medicinesSent ? "Added to Pending review" : "Add to Pending review";

    // ----- Commands / events -------------------------------------------------------------------

    public ICommand SaveCommand { get; }
    public ICommand PickImageCommand { get; }
    public ICommand CaptureImageCommand { get; }
    public ICommand RemoveImageCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand AddToPendingCommand { get; }

    public event EventHandler? SavedSuccessfully;
    public event EventHandler? OpenSettingsRequested;
    /// <summary>Short confirmation text for the page to show as a toast.</summary>
    public event EventHandler<string>? ToastRequested;

    // ----- Edit mode ---------------------------------------------------------------------------

    public async Task Load(int visitId)
    {
        IsBusy = true; ClearError();
        try
        {
            _visit = await _service.GetVisitById(visitId);
            if (_visit is null) { SetError("This visit no longer exists."); return; }

            _doctorName = _visit.DoctorName;
            _hospital = _visit.Hospital;
            _visitDate = DateTimeHelper.TryParseStored(_visit.VisitDate, out var vd) ? vd.Date : DateTime.Today;
            _diagnosis = _visit.Diagnosis;
            _notes = _visit.Notes;
            _hasFollowUp = DateTimeHelper.TryParseStored(_visit.FollowUpDate, out var fu);
            _followUpDate = _hasFollowUp ? fu.Date : DateTime.Today.AddDays(7);
            FollowUpMinDate = _hasFollowUp && fu.Date < DateTime.Today ? fu.Date : DateTime.Today;
            _prescriptionImagePath = _visit.PrescriptionImagePath;
            _userEditedVisitDate = false;
            _userEditedFollowUp = false;

            foreach (var name in new[]
                     {
                         nameof(DoctorName), nameof(Hospital), nameof(VisitDate), nameof(Diagnosis), nameof(Notes),
                         nameof(HasFollowUp), nameof(FollowUpDate), nameof(PrescriptionImagePath), nameof(HasImage),
                         nameof(PrescriptionThumbnail), nameof(IsEditMode), nameof(PageTitle), nameof(SaveButtonText),
                     })
                OnPropertyChanged(name);
            NotifyScanCardChanged();
        }
        catch (Exception ex) { SetError("Could not load the visit."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    private void NotifyScanCardChanged()
    {
        OnPropertyChanged(nameof(ShowScanCard));
        OnPropertyChanged(nameof(ShowRescanButton));
    }

    // ----- Scanning ----------------------------------------------------------------------------

    private async Task PickImage()
    {
        ClearError();
        try
        {
            var result = await MediaPicker.Default.PickPhotoAsync();
            if (result is not null) await ProcessImage(result);
        }
        catch (Exception ex) { SetError("Could not pick that image."); System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async Task CaptureImage()
    {
        ClearError();
        try
        {
            var status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted) { SetError("Camera permission is required to take a photo."); return; }
            if (!MediaPicker.Default.IsCaptureSupported) { SetError("No camera is available on this device."); return; }

            var result = await MediaPicker.Default.CapturePhotoAsync();
            if (result is not null) await ProcessImage(result);
        }
        catch (Exception ex) { SetError("Could not take a photo."); System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async Task ProcessImage(FileResult imageFile)
    {
        IsScanning = true;
        ShowAiBanner = false;
        try
        {
            PrescriptionImagePath = imageFile.FullPath;
            var bytes = await File.ReadAllBytesAsync(imageFile.FullPath);

            if (AiParserService.IsConfigured)
            {
                IsAiMode = true;
                ScanStatus = "Reading prescription with AI…";
                var result = await _aiParser.ExtractDoctorVisitAsync(null, GeminiImage.FromBytes(bytes));
                if (result.Succeeded && result.Value is not null)
                {
                    ApplyAiFields(result.Value);
                    AiNoticeText = string.Empty;
                    ShowSettingsAction = false;
                    ShowAiBanner = true;
                    ScanStatus = Medicines.Count > 0
                        ? $"{Medicines.Count} medicine{(Medicines.Count == 1 ? string.Empty : "s")} found on the prescription."
                        : string.Empty;
                    return;
                }
                System.Diagnostics.Debug.WriteLine($"AI visit extraction failed ({result.Kind}): {result.Error}");
                AiNoticeText = ReportAiStatus.Describe(result.Kind);
                ShowSettingsAction = ReportAiStatus.NeedsSettings(result.Kind);
            }
            else
            {
                AiNoticeText = ReportAiStatus.Describe(GeminiErrorKind.NoApiKey);
                ShowSettingsAction = true;
            }

            IsAiMode = false;
            ScanStatus = "Reading prescription…";
            var text = await _ocr.ExtractTextFromImageAsync(bytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                ScanStatus = "No readable text in the image. Fill in the fields manually.";
                return;
            }

            ApplyBasicParsing(text);
            ScanStatus = "Fields filled from scanned text — please check.";
        }
        catch (Exception ex)
        {
            ScanStatus = "Could not read the image. Fill in the fields manually.";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsScanning = false; }
    }

    private void ApplyAiFields(AiDoctorVisitExtraction v)
    {
        if (string.IsNullOrWhiteSpace(DoctorName) && !string.IsNullOrEmpty(v.DoctorName)) DoctorName = v.DoctorName;
        if (string.IsNullOrWhiteSpace(Hospital) && !string.IsNullOrEmpty(v.Hospital)) Hospital = v.Hospital;
        if (string.IsNullOrWhiteSpace(Diagnosis) && !string.IsNullOrEmpty(v.Diagnosis)) Diagnosis = v.Diagnosis;
        if (string.IsNullOrWhiteSpace(Notes) && !string.IsNullOrEmpty(v.Notes)) Notes = v.Notes;

        if (!_userEditedVisitDate && TryParseAiDate(v.VisitDate, out var visitDate) && visitDate <= DateTime.Today)
        {
            _visitDate = visitDate;
            OnPropertyChanged(nameof(VisitDate));
        }

        if (!_userEditedFollowUp && TryParseAiDate(v.FollowUpDate, out var followUp) && followUp >= DateTime.Today)
        {
            _followUpDate = followUp;
            OnPropertyChanged(nameof(FollowUpDate));
            HasFollowUp = true;
        }

        Medicines.Clear();
        foreach (var m in v.Medicines) Medicines.Add(new DoctorVisitMedicineItem(m));
        MedicinesSent = false;
        OnPropertyChanged(nameof(HasMedicines));
        OnPropertyChanged(nameof(CanSendMedicines));
    }

    private static bool TryParseAiDate(string? s, out DateTime date)
    {
        date = default;
        if (string.IsNullOrEmpty(s)) return false;
        if (!DateTime.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var parsed)) return false;
        date = parsed.Date;
        return true;
    }

    /// <summary>Regex fallback when AI is unavailable: doctor, hospital and the first date in the text.</summary>
    private void ApplyBasicParsing(string text)
    {
        var drMatch = Regex.Match(text, @"(?:Dr\.?\s+)([A-Z][a-z]+(?:\s+[A-Z][a-z]+)*)", RegexOptions.IgnoreCase);
        if (drMatch.Success && string.IsNullOrWhiteSpace(DoctorName))
            DoctorName = "Dr. " + drMatch.Groups[1].Value.Trim();

        var hospMatch = Regex.Match(text, @"(?:hospital|clinic|medical\s+center|health\s+center)[:\s]*(.+)", RegexOptions.IgnoreCase);
        if (hospMatch.Success && string.IsNullOrWhiteSpace(Hospital))
            Hospital = hospMatch.Groups[1].Value.Trim().TrimEnd('.', ',');

        if (_userEditedVisitDate) return;
        var dateMatch = Regex.Match(text, @"\b(\d{1,2}[/\-\.]\d{1,2}[/\-\.]\d{2,4})\b");
        if (dateMatch.Success
            && DateTime.TryParse(dateMatch.Groups[1].Value, Inv, DateTimeStyles.None, out var parsedDate)
            && parsedDate.Date <= DateTime.Today)
        {
            _visitDate = parsedDate.Date;
            OnPropertyChanged(nameof(VisitDate));
        }
    }

    // ----- Pending medicines -------------------------------------------------------------------

    /// <summary>Same mapping as ScanPrescriptionViewModel: Name/Dosage/Frequency, Form holds the instructions.</summary>
    private async Task AddMedicinesToPending()
    {
        if (!CanSendMedicines) return;
        IsBusy = true; ClearError();
        try
        {
            var now = DateTimeHelper.ToDateTimeString(DateTime.Now);
            foreach (var item in Medicines)
            {
                var parsed = PrescriptionParserService.FromAi(item.Source);
                await _pendingRepo.InsertAsync(new PendingMedicine
                {
                    Name = parsed.Name,
                    Dosage = parsed.Dosage,
                    Frequency = parsed.Frequency,
                    Form = parsed.Instructions,
                    ScannedAt = now
                });
            }
            MedicinesSent = true;
            var n = Medicines.Count;
            ToastRequested?.Invoke(this, $"{n} medicine{(n == 1 ? string.Empty : "s")} added to Pending review");
        }
        catch (Exception ex) { SetError("Could not add the medicines to Pending review."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    // ----- Save --------------------------------------------------------------------------------

    private async Task Save()
    {
        if (IsBusy) return;
        IsBusy = true;
        ClearError();
        try
        {
            if (string.IsNullOrWhiteSpace(DoctorName)) { SetError("Enter the doctor's name."); return; }

            var visitDate = DateTimeHelper.ToDateString(_visitDate);
            var followUp = HasFollowUp ? DateTimeHelper.ToDateString(_followUpDate) : null;

            if (_visit is null)
            {
                if (followUp is not null && _followUpDate.Date < DateTime.Today)
                {
                    SetError("Follow-up date must be today or a future date.");
                    return;
                }
                await _service.AddVisit(DoctorName.Trim(), Trimmed(Hospital), visitDate,
                    Trimmed(Diagnosis), Trimmed(Notes), followUp, HasImage ? PrescriptionImagePath : null);
            }
            else
            {
                // Only enforce "not in the past" when the follow-up is being changed, so an old visit
                // whose follow-up already passed can still be edited.
                if (followUp is not null && followUp != _visit.FollowUpDate && _followUpDate.Date < DateTime.Today)
                {
                    SetError("Follow-up date must be today or a future date.");
                    return;
                }

                _visit.DoctorName = DoctorName.Trim();
                _visit.Hospital = Trimmed(Hospital);
                _visit.VisitDate = visitDate;
                _visit.Diagnosis = Trimmed(Diagnosis);
                _visit.Notes = Trimmed(Notes);
                _visit.FollowUpDate = followUp;
                _visit.PrescriptionImagePath = HasImage ? PrescriptionImagePath : null;
                await _service.UpdateVisit(_visit);
            }

            SavedSuccessfully?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException ex) { SetError(ex.Message); }
        catch (Exception ex) { SetError("Could not save the visit. " + ex.Message); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
