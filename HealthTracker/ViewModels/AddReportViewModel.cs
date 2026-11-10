using HealthTracker.Services.Labs;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.Services.Ai;

namespace HealthTracker.ViewModels;

/// <summary>One page (photo or PDF) queued for the report being added.</summary>
public class ReportPageItem : BaseViewModel
{
    private int _pageNumber;

    public ReportPageItem(string filePath, string fileType, string fileName, int pageNumber)
    {
        FilePath = filePath;
        FileType = fileType.ToLowerInvariant();
        FileName = fileName;
        _pageNumber = pageNumber;
    }

    public string FilePath { get; }
    public string FileType { get; }
    public string FileName { get; }
    public bool IsPdf => FileType == "pdf";
    public bool IsImage => !IsPdf;
    public ImageSource? Thumbnail => IsPdf ? null : ImageSource.FromFile(FilePath);

    public int PageNumber
    {
        get => _pageNumber;
        set { _pageNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(PageLabel)); }
    }

    public string PageLabel => $"Page {_pageNumber}";
}

/// <summary>Lab values read from a just-saved report, offered to the Labs entry form for review.</summary>
public sealed record LabImportOffer(int ReportId, List<LabDraft> Drafts, DateTime ReportDate, string? LabName);

/// <summary>Selectable report-type chip. Exposes theme token names, never colours.</summary>
public class ReportTypeOption : BaseViewModel
{
    private bool _isSelected;

    public ReportTypeOption(string name) => Name = name;

    public string Name { get; }

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

/// <summary>Picker entry for "Link to doctor visit". <c>Id</c> null = not linked.</summary>
public class ReportVisitOption
{
    public ReportVisitOption(int? id, string label)
    {
        Id = id;
        Label = label;
    }

    public int? Id { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public class AddReportViewModel : BaseViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly ReportService _service;
    private readonly ReportTextParserService _textParser;
    private readonly AiParserService _aiParser;
    private readonly DoctorService _doctorService;
    private readonly StringBuilder _accumulatedOcrText = new();
    /// <summary>Lab values read from the pages so far (AI key findings, or the offline text scan).</summary>
    private readonly List<LabDraft> _labDrafts = [];

    private bool _visitsLoaded;
    private bool _userEditedDate;
    private bool _userEditedType;

    private string _title = string.Empty;
    private string? _doctorName;
    private string? _hospital;
    private string? _notes;
    private DateTime _reportDate = DateTime.Today;
    private ReportTypeOption _selectedType;
    private ReportVisitOption? _selectedVisit;
    private int? _doctorVisitId;

    private bool _isScanning;
    private string _scanStatus = string.Empty;
    private bool _isAiMode;
    private bool _showAiBanner;
    private string _aiNoticeText = string.Empty;
    private bool _showSettingsAction;

    public AddReportViewModel(ReportService service, ReportTextParserService textParser,
        AiParserService aiParser, DoctorService doctorService)
    {
        _service = service;
        _textParser = textParser;
        _aiParser = aiParser;
        _doctorService = doctorService;

        foreach (var type in AppConstants.ReportTypes)
            TypeOptions.Add(new ReportTypeOption(type));
        _selectedType = TypeOptions[0];
        _selectedType.IsSelected = true;

        VisitOptions.Add(new ReportVisitOption(null, "Not linked"));
        _selectedVisit = VisitOptions[0];

        PickFileCommand = new Command(async () => await PickFile());
        CapturePhotoCommand = new Command(async () => await CapturePhoto());
        RemovePageCommand = new Command<ReportPageItem>(RemovePage);
        SelectTypeCommand = new Command<ReportTypeOption>(o => SelectType(o, userAction: true));
        DismissBannerCommand = new Command(() => ShowAiBanner = false);
        OpenSettingsCommand = new Command(() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        SaveCommand = new Command(async () => await Save());
    }

    // ----- Fields ----------------------------------------------------------------------------

    public string Title
    {
        get => _title;
        set { _title = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string? DoctorName
    {
        get => _doctorName;
        set { _doctorName = value; OnPropertyChanged(); }
    }

    public string? Hospital
    {
        get => _hospital;
        set { _hospital = value; OnPropertyChanged(); }
    }

    public string? Notes
    {
        get => _notes;
        set { _notes = value; OnPropertyChanged(); }
    }

    /// <summary>Set by the DatePicker. Marks the date as user-chosen so extraction never overwrites it.</summary>
    public DateTime ReportDate
    {
        get => _reportDate;
        set
        {
            if (_reportDate.Date == value.Date) return;
            _reportDate = value.Date;
            _userEditedDate = true;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ReportTypeOption> TypeOptions { get; } = new();
    public string ReportType => _selectedType.Name;

    public ObservableCollection<ReportVisitOption> VisitOptions { get; } = new();

    public ReportVisitOption? SelectedVisit
    {
        get => _selectedVisit;
        set
        {
            _selectedVisit = value;
            _doctorVisitId = value?.Id;
            OnPropertyChanged();
        }
    }

    /// <summary>Visit the report will be attached to. Kept for callers that preset it before VisitOptions load.</summary>
    public int? DoctorVisitId
    {
        get => _doctorVisitId;
        set
        {
            _doctorVisitId = value;
            var match = VisitOptions.FirstOrDefault(o => o.Id == value);
            if (match is not null) { _selectedVisit = match; OnPropertyChanged(nameof(SelectedVisit)); }
            OnPropertyChanged();
        }
    }

    // ----- Pages -----------------------------------------------------------------------------

    public ObservableCollection<ReportPageItem> Pages { get; } = new();
    public bool HasPages => Pages.Count > 0;
    public string PageCountText => Pages.Count == 1 ? "1 page" : $"{Pages.Count} pages";

    // ----- Extraction state ------------------------------------------------------------------

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

    /// <summary>True while/after Gemini handled the current page; drives the "AI" vs "On-device OCR" chip.</summary>
    public bool IsAiMode
    {
        get => _isAiMode;
        set { _isAiMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScanModeLabel)); }
    }

    public string ScanModeLabel => _isAiMode ? ReportAiStatus.AiModeLabel : ReportAiStatus.OcrModeLabel;

    public bool ShowAiBanner
    {
        get => _showAiBanner;
        set { _showAiBanner = value; OnPropertyChanged(); }
    }

    public string AiNoticeText
    {
        get => _aiNoticeText;
        set { _aiNoticeText = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(HasAiNotice)); }
    }

    public bool HasAiNotice => _aiNoticeText.Length > 0;

    public bool ShowSettingsAction
    {
        get => _showSettingsAction;
        set { _showSettingsAction = value; OnPropertyChanged(); }
    }

    // ----- Commands / events -----------------------------------------------------------------

    public ICommand PickFileCommand { get; }
    public ICommand CapturePhotoCommand { get; }
    public ICommand RemovePageCommand { get; }
    public ICommand SelectTypeCommand { get; }
    public ICommand DismissBannerCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand SaveCommand { get; }

    public event EventHandler? SavedSuccessfully;

    /// <summary>Set by a successful save: what the page needs to offer "add these values to Labs".</summary>
    public LabImportOffer? LabImport { get; private set; }
    /// <summary>Raised when the user taps "Open Settings" on an AI notice; the page navigates.</summary>
    public event EventHandler? OpenSettingsRequested;

    // ----- Setup -----------------------------------------------------------------------------

    /// <summary>Loads the doctor-visit options once. Safe to call on every OnAppearing.</summary>
    public async Task InitializeAsync()
    {
        if (_visitsLoaded) return;
        _visitsLoaded = true;
        try
        {
            var visits = await _doctorService.GetAllVisits();
            foreach (var v in visits)
            {
                var date = DateTimeHelper.TryParseStored(v.VisitDate, out var d) ? d.ToString("dd MMM yyyy", Inv) : v.VisitDate;
                VisitOptions.Add(new ReportVisitOption(v.Id, $"{v.DoctorName} · {date}"));
            }
            if (_doctorVisitId is { } id)
            {
                var match = VisitOptions.FirstOrDefault(o => o.Id == id);
                if (match is not null) { _selectedVisit = match; OnPropertyChanged(nameof(SelectedVisit)); }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Visit options failed: {ex}"); }
    }

    /// <summary>Pre-fills the form when opened from a doctor visit ("Attach report").</summary>
    public void Preset(int visitId, string? doctorName, string? hospital, string? reportType = null)
    {
        DoctorVisitId = visitId;
        if (!string.IsNullOrWhiteSpace(doctorName)) DoctorName = doctorName;
        if (!string.IsNullOrWhiteSpace(hospital)) Hospital = hospital;
        if (!string.IsNullOrWhiteSpace(reportType))
        {
            var option = TypeOptions.FirstOrDefault(o => o.Name == reportType);
            if (option is not null) SelectType(option, userAction: true);
        }
    }

    public void SelectType(ReportTypeOption? option, bool userAction)
    {
        if (option is null) return;
        if (userAction) _userEditedType = true;
        if (ReferenceEquals(option, _selectedType)) return;
        _selectedType.IsSelected = false;
        _selectedType = option;
        _selectedType.IsSelected = true;
        OnPropertyChanged(nameof(ReportType));
    }

    // ----- Adding pages ----------------------------------------------------------------------

    private async Task PickFile()
    {
        ClearError();
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "image/*", "application/pdf" } }
                })
            });
            if (result is null) return;

            var ext = Path.GetExtension(result.FileName).TrimStart('.').ToLowerInvariant();
            if (!ValidationHelper.IsValidFileType(ext, out var err)) { SetError(err); return; }

            var page = AddPage(result.FullPath, ext, result.FileName);
            await RunExtraction(page);
        }
        catch (Exception ex) { SetError("Could not pick that file."); System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async Task CapturePhoto()
    {
        ClearError();
        try
        {
            var status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted) { SetError("Camera permission is required to take a photo."); return; }
            if (!MediaPicker.Default.IsCaptureSupported) { SetError("No camera is available on this device."); return; }

            var photo = await CapturePhotoMutedAsync();
            if (photo is null) return;

            var ext = Path.GetExtension(photo.FileName).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "jpg";
            var page = AddPage(photo.FullPath, ext, photo.FileName);
            await RunExtraction(page);
        }
        catch (Exception ex) { SetError("Could not take a photo."); System.Diagnostics.Debug.WriteLine(ex); }
    }

    /// <summary>Captures a photo with the shutter sound muted on Android; the volume is always restored.</summary>
    private static async Task<FileResult?> CapturePhotoMutedAsync()
    {
#if ANDROID
        var audioManager = (Android.Media.AudioManager?)Android.App.Application.Context
            .GetSystemService(Android.Content.Context.AudioService);
        int originalVolume = -1;
        try
        {
            originalVolume = audioManager?.GetStreamVolume(Android.Media.Stream.System) ?? -1;
            audioManager?.SetStreamVolume(Android.Media.Stream.System, 0,
                Android.Media.VolumeNotificationFlags.RemoveSoundAndVibrate);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Volume mute failed: {ex.Message}"); }
        try
        {
            return await MediaPicker.Default.CapturePhotoAsync();
        }
        finally
        {
            if (originalVolume >= 0)
            {
                try { audioManager?.SetStreamVolume(Android.Media.Stream.System, originalVolume, 0); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Volume restore failed: {ex.Message}"); }
            }
        }
#else
        return await MediaPicker.Default.CapturePhotoAsync();
#endif
    }

    private ReportPageItem AddPage(string filePath, string fileType, string fileName)
    {
        var page = new ReportPageItem(filePath, fileType, fileName, Pages.Count + 1);
        Pages.Add(page);
        NotifyPagesChanged();
        return page;
    }

    public void RemovePage(ReportPageItem? page)
    {
        if (page is null || !Pages.Remove(page)) return;
        for (int i = 0; i < Pages.Count; i++) Pages[i].PageNumber = i + 1;
        NotifyPagesChanged();
        if (Pages.Count == 0) ScanStatus = string.Empty;
    }

    private void NotifyPagesChanged()
    {
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(PageCountText));
    }

    // ----- Extraction: AI (image) first, on-device OCR + regex fallback ----------------------

    private async Task RunExtraction(ReportPageItem page)
    {
        if (page.IsPdf)
        {
            ScanStatus = "PDF added. Fill in the details below.";
            return;
        }

        IsScanning = true;
        var pageLabel = $"page {page.PageNumber} of {Pages.Count}";
        try
        {
            var bytes = await File.ReadAllBytesAsync(page.FilePath);

            if (AiParserService.IsConfigured)
            {
                IsAiMode = true;
                ScanStatus = $"Reading {pageLabel} with AI…";
                var result = await _aiParser.ExtractReportAsync(null, GeminiImage.FromBytes(bytes));
                if (result.Succeeded && result.Value is not null)
                {
                    ApplyAiFields(result.Value);
                    MergeLabDrafts(result.Value.KeyFindings
                        .Select(f => LabFindingParser.Parse(f.Name, f.Value, f.Unit, f.ReferenceRange))
                        .Where(d => d is not null).Select(d => d!));
                    AiNoticeText = string.Empty;
                    ShowSettingsAction = false;
                    ShowAiBanner = true;
                    ScanStatus = string.Empty;
                    return;
                }
                System.Diagnostics.Debug.WriteLine($"AI report extraction failed ({result.Kind}): {result.Error}");
                AiNoticeText = ReportAiStatus.Describe(result.Kind);
                ShowSettingsAction = ReportAiStatus.NeedsSettings(result.Kind);
            }
            else
            {
                AiNoticeText = ReportAiStatus.Describe(GeminiErrorKind.NoApiKey);
                ShowSettingsAction = true;
            }

            IsAiMode = false;
            ScanStatus = $"Reading {pageLabel}…";
            var text = await _textParser.ExtractTextFromImageAsync(bytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                ScanStatus = "No readable text on this page.";
                return;
            }

            _accumulatedOcrText.AppendLine(text);
            ApplyParsedFields(_textParser.ParseReportFields(_accumulatedOcrText.ToString()));
            MergeLabDrafts(LabFindingParser.ScanText(text));
            ScanStatus = "Fields filled from scanned text — please check.";
        }
        catch (Exception ex)
        {
            ScanStatus = "Could not read this page. Fill in the details manually.";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsScanning = false; }
    }

    /// <summary>Adds newly read values; a test already read from an earlier page keeps its first value.</summary>
    private void MergeLabDrafts(IEnumerable<LabDraft> drafts)
    {
        foreach (var draft in drafts)
        {
            var key = draft.Definition?.Code ?? LabCatalog.CustomCode(draft.Name);
            if (_labDrafts.Any(d => (d.Definition?.Code ?? LabCatalog.CustomCode(d.Name)) == key)) continue;
            _labDrafts.Add(draft);
        }
    }

    private void ApplyAiFields(AiReportExtraction r)
    {
        if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrEmpty(r.Title)) Title = r.Title;
        if (string.IsNullOrWhiteSpace(DoctorName) && !string.IsNullOrEmpty(r.DoctorName)) DoctorName = r.DoctorName;
        if (string.IsNullOrWhiteSpace(Hospital) && !string.IsNullOrEmpty(r.Hospital)) Hospital = r.Hospital;
        ApplyType(r.ReportType);
        ApplyExtractedDate(r.ReportDate);

        var notes = BuildNotes(r);
        if (notes.Length == 0) return;
        if (string.IsNullOrWhiteSpace(Notes)) Notes = notes;
        else if (!Notes.Contains(notes, StringComparison.Ordinal)) Notes = Notes.TrimEnd() + "\n\n" + notes;
    }

    private void ApplyParsedFields(ParsedReportFields f)
    {
        if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrEmpty(f.Title)) Title = f.Title;
        if (string.IsNullOrWhiteSpace(DoctorName) && !string.IsNullOrEmpty(f.DoctorName)) DoctorName = f.DoctorName;
        if (string.IsNullOrWhiteSpace(Hospital) && !string.IsNullOrEmpty(f.Hospital)) Hospital = f.Hospital;
        ApplyType(f.ReportType);
        ApplyExtractedDate(f.ReportDate);
    }

    private void ApplyType(string? type)
    {
        if (_userEditedType || string.IsNullOrEmpty(type)) return;
        var option = TypeOptions.FirstOrDefault(o => o.Name.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (option is not null) SelectType(option, userAction: false);
    }

    /// <summary>Extracted dates are "yyyy-MM-dd" only; a date the user already picked is never overwritten.</summary>
    private void ApplyExtractedDate(string? value)
    {
        if (_userEditedDate || string.IsNullOrEmpty(value)) return;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var parsed)) return;
        if (parsed.Date > DateTime.Today) return;
        _reportDate = parsed.Date;
        OnPropertyChanged(nameof(ReportDate));
    }

    /// <summary>"Summary" then one "Name: Value Unit (ref) ↑" line per key finding.</summary>
    public static string BuildNotes(AiReportExtraction r)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.Summary)) sb.AppendLine(r.Summary.Trim());

        if (r.KeyFindings.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Key findings:");
            foreach (var f in r.KeyFindings)
            {
                var line = new StringBuilder(f.Name);
                var value = string.Join(" ", new[] { f.Value, f.Unit }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (value.Length > 0) line.Append(": ").Append(value);
                if (!string.IsNullOrWhiteSpace(f.ReferenceRange)) line.Append(" (").Append(f.ReferenceRange).Append(')');
                if (f.Flag == "high") line.Append(" ↑");
                else if (f.Flag == "low") line.Append(" ↓");
                sb.AppendLine(line.ToString());
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ----- Save ------------------------------------------------------------------------------

    private async Task Save()
    {
        if (IsBusy) return;
        IsBusy = true;
        ClearError();
        try
        {
            if (Pages.Count == 0) { SetError("Add at least one page (photo or PDF)."); return; }
            if (string.IsNullOrWhiteSpace(Title)) { SetError("Give the report a title."); return; }

            var pageList = Pages.Select(p => (p.FilePath, p.FileType)).ToList();
            var saved = await _service.SaveMultiPageReport(pageList, ReportType, Title.Trim(),
                Trimmed(DoctorName), Trimmed(Hospital), DateTimeHelper.ToDateString(_reportDate),
                Trimmed(Notes), _doctorVisitId);

            // Only lab reports feed the Labs tab; an X-ray's "findings" are not lab values.
            LabImport = ReportType == "Lab" && _labDrafts.Count > 0
                ? new LabImportOffer(saved.Id, _labDrafts.ToList(), _reportDate, Trimmed(Hospital))
                : null;

            SavedSuccessfully?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException ex) { SetError(ex.Message); }
        catch (Exception ex) { SetError("Could not save the report. " + ex.Message); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
