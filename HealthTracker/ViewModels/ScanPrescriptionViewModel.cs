using System.Collections.ObjectModel;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services;
using HealthTracker.Services.Ai;

namespace HealthTracker.ViewModels;

/// <summary>An editable medicine candidate detected on the scanned prescription.</summary>
public class ScannedMedicineItem : BaseViewModel
{
    private bool _isSelected = true;
    private string _name = string.Empty;
    private string? _dosage;
    private string? _frequency;
    private string? _durationText;

    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string? Dosage { get => _dosage; set { _dosage = value; OnPropertyChanged(); } }
    public string? Frequency { get => _frequency; set { _frequency = value; OnPropertyChanged(); } }
    public string? Form { get; set; }
    public string? Instructions { get; set; }
    public List<string> Times { get; set; } = [];
    public bool HasTimes => Times.Count > 0;

    /// <summary>Duration in days as typed by the user (free text so it can be cleared).</summary>
    public string? DurationText { get => _durationText; set { _durationText = value; OnPropertyChanged(); } }

    public string MetaText => string.Join(" · ", new[] { Form, Instructions }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool HasMeta => !string.IsNullOrEmpty(MetaText);
}

public class ScanPrescriptionViewModel : BaseViewModel
{
    private readonly PrescriptionParserService _parser;
    private readonly PendingMedicineRepository _pendingRepo;

    private string _ocrText = string.Empty;
    private bool _hasResults;
    private bool _hasScanned;
    private bool _savedSuccessfully;
    private bool _showRawText;
    private string _progressText = string.Empty;
    private string _resultMessage = string.Empty;
    private string _aiStatusText = string.Empty;
    private bool _showOpenSettings;
    private ImageSource? _previewImage;
    private byte[]? _imageData;

    public ObservableCollection<ScannedMedicineItem> ScannedMedicines { get; } = new();

    public string OcrText { get => _ocrText; set { _ocrText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOcrText)); } }
    public bool HasOcrText => !string.IsNullOrWhiteSpace(OcrText);

    public bool HasResults { get => _hasResults; set { _hasResults = value; OnPropertyChanged(); } }
    public bool HasScanned { get => _hasScanned; set { _hasScanned = value; OnPropertyChanged(); } }
    public bool SavedSuccessfully { get => _savedSuccessfully; set { _savedSuccessfully = value; OnPropertyChanged(); } }
    public string ResultMessage { get => _resultMessage; set { _resultMessage = value; OnPropertyChanged(); } }

    public bool ShowRawText { get => _showRawText; set { _showRawText = value; OnPropertyChanged(); OnPropertyChanged(nameof(RawTextToggleText)); } }
    public string RawTextToggleText => ShowRawText ? "Hide scanned text" : "Show scanned text";

    /// <summary>Label next to the activity indicator while OCR/AI runs.</summary>
    public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(); } }

    public ImageSource? PreviewImage { get => _previewImage; set { _previewImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImage)); } }
    public bool HasImage => PreviewImage != null;

    /// <summary>"AI" when a Gemini key is configured, else "On-device OCR" (shown as a chip).</summary>
    public bool IsAiConfigured => AiParserService.IsConfigured;
    public string ParserModeText => IsAiConfigured ? "AI" : "On-device OCR";

    /// <summary>Friendly explanation when the AI path was skipped or failed (regex fallback was used).</summary>
    public string AiStatusText { get => _aiStatusText; set { _aiStatusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAiStatus)); } }
    public bool HasAiStatus => !string.IsNullOrEmpty(AiStatusText);
    public bool ShowOpenSettings { get => _showOpenSettings; set { _showOpenSettings = value; OnPropertyChanged(); } }

    public ICommand PickImageCommand { get; }
    public ICommand CaptureImageCommand { get; }
    public ICommand SaveToPendingCommand { get; }
    public ICommand ToggleRawTextCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ReviewNowCommand { get; }
    public ICommand ScanAnotherCommand { get; }

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ReviewRequested;

    public ScanPrescriptionViewModel(PrescriptionParserService parser, PendingMedicineRepository pendingRepo)
    {
        _parser = parser;
        _pendingRepo = pendingRepo;
        PickImageCommand = new Command(async () => await ScanFromGallery());
        CaptureImageCommand = new Command(async () => await ScanFromCamera());
        SaveToPendingCommand = new Command(async () => await SaveToPending());
        ToggleRawTextCommand = new Command(() => ShowRawText = !ShowRawText);
        OpenSettingsCommand = new Command(() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        ReviewNowCommand = new Command(() => ReviewRequested?.Invoke(this, EventArgs.Empty));
        ScanAnotherCommand = new Command(Reset);
    }

    /// <summary>Re-reads the AI configuration (the user may have added a key in Settings meanwhile).</summary>
    public void RefreshConfiguration()
    {
        OnPropertyChanged(nameof(IsAiConfigured));
        OnPropertyChanged(nameof(ParserModeText));
    }

    private void Reset()
    {
        ClearError();
        ScannedMedicines.Clear();
        OcrText = string.Empty;
        HasResults = false;
        HasScanned = false;
        SavedSuccessfully = false;
        ShowRawText = false;
        AiStatusText = string.Empty;
        ShowOpenSettings = false;
        PreviewImage = null;
        _imageData = null;
    }

    private async Task ScanFromGallery()
    {
        if (IsBusy) return;
        try
        {
            var result = await MediaPicker.Default.PickPhotoAsync();
            if (result != null) await ProcessImage(result);
        }
        catch (Exception ex)
        {
            SetError("Couldn't open the image picker.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task ScanFromCamera()
    {
        if (IsBusy) return;
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported) { SetError("This device has no camera available."); return; }
            var result = await MediaPicker.Default.CapturePhotoAsync();
            if (result != null) await ProcessImage(result);
        }
        catch (PermissionException)
        {
            SetError("Camera permission is required to take a photo.");
        }
        catch (Exception ex)
        {
            SetError("Couldn't take a photo.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task ProcessImage(FileResult imageFile)
    {
        IsBusy = true; ClearError();
        HasScanned = false;
        HasResults = false;
        SavedSuccessfully = false;
        ShowRawText = false;
        AiStatusText = string.Empty;
        ShowOpenSettings = false;
        ScannedMedicines.Clear();
        try
        {
            using (var stream = await imageFile.OpenReadAsync())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                _imageData = ms.ToArray();
            }
            var data = _imageData;
            PreviewImage = ImageSource.FromStream(() => new MemoryStream(data));

            ProgressText = "Reading prescription…";
            var text = await _parser.ExtractTextFromImageAsync(data);
            OcrText = text ?? string.Empty;
            HasScanned = true;

            if (string.IsNullOrWhiteSpace(text) && !IsAiConfigured)
            {
                SetError("Couldn't read any text from the image. Try a sharper, well-lit photo.");
                return;
            }

            ProgressText = IsAiConfigured ? "Finding medicines with AI…" : "Finding medicines…";
            var (parsed, aiResult) = await _parser.ParseMedicinesWithAiAsync(text ?? string.Empty, data);
            ApplyAiStatus(aiResult);

            foreach (var m in parsed)
            {
                ScannedMedicines.Add(new ScannedMedicineItem
                {
                    Name = m.Name,
                    Dosage = m.Dosage,
                    Frequency = m.Frequency,
                    Form = m.Form,
                    Instructions = m.Instructions,
                    Times = m.Times ?? [],
                    DurationText = m.DurationDays is > 0 ? m.DurationDays.Value.ToString() : null,
                });
            }

            HasResults = ScannedMedicines.Count > 0;
            if (!HasResults)
                SetError("No medicines were detected. Check the scanned text below, or add the medicine manually.");
        }
        catch (Exception ex) { SetError("Scanning failed. Please try again."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; ProgressText = string.Empty; }
    }

    private void ApplyAiStatus(GeminiResult<AiPrescriptionExtraction>? aiResult)
    {
        if (!IsAiConfigured)
        {
            AiStatusText = "Parsed on-device. Add a Gemini API key in Settings for more accurate results.";
            ShowOpenSettings = true;
            return;
        }
        if (aiResult is null || aiResult.Succeeded) return;

        ShowOpenSettings = aiResult.Kind is GeminiErrorKind.NoApiKey or GeminiErrorKind.Unauthorized;
        AiStatusText = aiResult.Kind switch
        {
            GeminiErrorKind.NoApiKey => "No Gemini API key is set, so the prescription was parsed on-device.",
            GeminiErrorKind.Network => "Couldn't reach the AI service (offline?). Parsed on-device instead.",
            GeminiErrorKind.Unauthorized => "Your Gemini API key was rejected. Check it in Settings. Parsed on-device instead.",
            GeminiErrorKind.RateLimited => "The AI service is busy right now. Parsed on-device instead.",
            GeminiErrorKind.Cancelled => "AI parsing was cancelled. Parsed on-device instead.",
            _ => "The AI couldn't read this prescription. Parsed on-device instead.",
        };
    }

    private async Task SaveToPending()
    {
        if (IsBusy) return;
        var selected = ScannedMedicines.Where(m => m.IsSelected && !string.IsNullOrWhiteSpace(m.Name)).ToList();
        if (selected.Count == 0)
        {
            SetError("Select at least one medicine to save.");
            return;
        }

        IsBusy = true; ClearError();
        try
        {
            var now = DateTimeHelper.ToDateTimeString(DateTime.Now);
            foreach (var item in selected)
            {
                var frequency = item.Frequency;
                if (int.TryParse(item.DurationText, out var days) && days > 0)
                    frequency = string.IsNullOrEmpty(frequency) ? $"for {days} days" : $"{frequency} for {days} days";

                await _pendingRepo.InsertAsync(new PendingMedicine
                {
                    Name = item.Name.Trim(),
                    Dosage = string.IsNullOrWhiteSpace(item.Dosage) ? null : item.Dosage.Trim(),
                    Frequency = frequency,
                    Form = item.Form ?? item.Instructions,
                    ScannedAt = now,
                });
            }

            SavedSuccessfully = true;
            HasResults = false;
            ResultMessage = selected.Count == 1
                ? $"{selected[0].Name} is waiting in Pending review."
                : $"{selected.Count} medicines are waiting in Pending review.";
        }
        catch (Exception ex) { SetError("Couldn't save the medicines."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }
}
