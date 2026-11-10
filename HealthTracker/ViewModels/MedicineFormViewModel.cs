using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

/// <summary>One schedule time row in the medicine form.</summary>
public class ScheduleEntry : BaseViewModel
{
    private TimeSpan _time = new(8, 0, 0);
    public TimeSpan Time { get => _time; set { _time = value; OnPropertyChanged(); } }
}

/// <summary>
/// Add + Edit medicine in one ViewModel. Call <see cref="Load"/> for edit mode or
/// <see cref="PrefillFromPending"/> to start from a scanned prescription; otherwise it is a blank Add form.
/// </summary>
public partial class MedicineFormViewModel : BaseViewModel
{
    private readonly MedicineService _service;
    private Medicine? _medicine;

    private string _name = string.Empty;
    private string _prescribedDaysText = string.Empty;
    private string _stockText = string.Empty;
    private string _stockUnit = "Tablet";
    private string? _instructions;
    private string? _notes;
    private bool _isOngoing;
    private DateTime _startDate = DateTime.Today;
    private TimeSpan _startTime = RoundToMinute(DateTime.Now.TimeOfDay);

    public bool IsEditMode => _medicine != null;
    public string PageTitle => IsEditMode ? "Edit medicine" : "Add medicine";
    public string SaveButtonText => IsEditMode ? "Save changes" : "Save medicine";

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string PrescribedDaysText { get => _prescribedDaysText; set { _prescribedDaysText = value; OnPropertyChanged(); OnPropertyChanged(nameof(EndDateHint)); } }

    public bool IsOngoing
    {
        get => _isOngoing;
        set { _isOngoing = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPrescribedDaysVisible)); OnPropertyChanged(nameof(EndDateHint)); }
    }
    public bool IsPrescribedDaysVisible => !IsOngoing;

    public string StockText { get => _stockText; set { _stockText = value; OnPropertyChanged(); OnPropertyChanged(nameof(StockHint)); } }
    public string StockUnit { get => _stockUnit; set { _stockUnit = value ?? "Tablet"; OnPropertyChanged(); OnPropertyChanged(nameof(StockHint)); } }
    public string[] StockUnitOptions { get; } = ["Tablet", "Capsule", "Drop", "Syrup", "Injection", "Spray", "Inhaler", "Other"];

    public DateTime StartDate { get => _startDate; set { _startDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(EndDateHint)); OnPropertyChanged(nameof(StartsInPast)); } }
    public TimeSpan StartTime { get => _startTime; set { _startTime = value; OnPropertyChanged(); } }
    public bool StartsInPast => StartDate.Date < DateTime.Today;

    public string? Instructions { get => _instructions; set { _instructions = value; OnPropertyChanged(); } }
    public string? Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }

    public ObservableCollection<ScheduleEntry> Schedules { get; } = new();
    public bool CanRemoveSchedule => Schedules.Count > 1;

    /// <summary>Computed end date for a fixed course, shown under the prescribed-days field.</summary>
    public string EndDateHint
    {
        get
        {
            if (IsOngoing) return "No end date. The medicine stays active until you stop it.";
            if (!int.TryParse(PrescribedDaysText, out var days) || days <= 0) return "Number of days the doctor prescribed.";
            var end = StartDate.Date.AddDays(days - 1);
            return $"Course ends {end.ToString("ddd, dd MMM yyyy", CultureInfo.InvariantCulture)}.";
        }
    }

    public string StockHint
    {
        get
        {
            if (!ValidationHelper.TryParseDouble(StockText, out var stock) || Schedules.Count == 0) return "How many doses you have on hand right now.";
            var days = stock / Schedules.Count;
            if (days < 1) return "Less than a day of doses. You will see an out-of-stock warning.";
            return $"Covers about {Math.Floor(days):0} day{(Math.Floor(days) == 1 ? "" : "s")} at {Schedules.Count}x daily.";
        }
    }

    public ICommand AddScheduleCommand { get; }
    public ICommand RemoveScheduleCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    public ICommand SaveCommand { get; }

    /// <summary>Raised after a successful save; the page pops itself and shows a toast.</summary>
    public event EventHandler? SavedSuccessfully;

    public MedicineFormViewModel(MedicineService service)
    {
        _service = service;
        Schedules.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(CanRemoveSchedule)); OnPropertyChanged(nameof(StockHint)); };
        Schedules.Add(new ScheduleEntry());
        AddScheduleCommand = new Command(AddSchedule);
        RemoveScheduleCommand = new Command<ScheduleEntry>(s => { if (s != null && Schedules.Count > 1) Schedules.Remove(s); });
        ApplyPresetCommand = new Command<string>(ApplyPreset);
        SaveCommand = new Command(async () => await Save());
    }

    // ── Entry points ─────────────────────────────────────────────────────────

    /// <summary>Edit mode: pre-fills every field from the stored medicine.</summary>
    public async Task Load(int medicineId)
    {
        IsBusy = true; ClearError();
        try
        {
            _medicine = await _service.GetMedicineById(medicineId);
            if (_medicine is null) { SetError("Medicine not found."); return; }

            Name = _medicine.Name;
            StockText = _medicine.StockCount.ToString("0.##", CultureInfo.InvariantCulture);
            StockUnit = StockUnitOptions.Contains(_medicine.StockUnit) ? _medicine.StockUnit : "Other";
            var parsedStart = DateTimeHelper.TryParseStored(_medicine.StartDate, out var d) ? d : DateTime.Today;
            StartDate = parsedStart.Date;
            StartTime = parsedStart.TimeOfDay;
            Instructions = _medicine.Instructions;
            Notes = _medicine.Notes;
            IsOngoing = _medicine.IsOngoing == 1;
            PrescribedDaysText = _medicine.PrescribedDays > 0 ? _medicine.PrescribedDays.ToString() : string.Empty;

            var schedules = await _service.GetSchedulesForMedicine(medicineId);
            SetScheduleTimes(schedules
                .Where(s => s.IsActive == 1)
                .Select(s => DateTimeHelper.TryParseTime(s.ScheduleTime, out var t) ? t : TimeSpan.FromHours(8)));

            OnPropertyChanged(nameof(IsEditMode));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(SaveButtonText));
        }
        catch (Exception ex) { SetError("Failed to load medicine."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>Add mode seeded from a scanned prescription entry.</summary>
    public void PrefillFromPending(PendingMedicine pending)
    {
        Name = pending.Name;

        var parts = new[] { pending.Dosage, pending.Frequency }.Where(s => !string.IsNullOrWhiteSpace(s));
        Instructions = string.Join(" · ", parts);
        if (string.IsNullOrEmpty(Instructions)) Instructions = null;

        if (!string.IsNullOrEmpty(pending.Form))
        {
            var lower = pending.Form.ToLowerInvariant();
            StockUnit = lower.Contains("capsule") ? "Capsule"
                : lower.Contains("syrup") || lower.Contains("susp") ? "Syrup"
                : lower.Contains("inject") ? "Injection"
                : lower.Contains("drop") ? "Drop"
                : lower.Contains("inhal") ? "Inhaler"
                : lower.Contains("spray") ? "Spray"
                : lower.Contains("tab") ? "Tablet"
                : "Other";
        }

        var perDay = FrequencyPerDayFromText(pending.Frequency);
        if (perDay is > 0 and <= 4) ApplyPreset(perDay.Value.ToString());

        // The scanner folds the course length into the frequency text ("1-0-1 for 30 days").
        var days = DurationDaysFromText(pending.Frequency);
        if (days is > 0)
        {
            IsOngoing = false;
            PrescribedDaysText = days.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>"for 30 days" / "x 2 weeks" / "for 1 month" → days; null when no duration is present.</summary>
    internal static int? DurationDaysFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"(?:for|x|×)?\s*(\d{1,3})\s*(days?|d\b|weeks?|wks?|months?)", RegexOptions.IgnoreCase);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n) || n <= 0) return null;
        var unit = m.Groups[2].Value.ToLowerInvariant();
        return unit.StartsWith('w') ? n * 7 : unit.StartsWith('m') ? n * 30 : n;
    }

    // ── Schedules ────────────────────────────────────────────────────────────

    private void AddSchedule()
    {
        // Suggest a sensible next time: 6h after the last one, wrapping within the day.
        var last = Schedules.LastOrDefault()?.Time ?? new TimeSpan(8, 0, 0);
        var next = last.Add(TimeSpan.FromHours(6));
        if (next >= TimeSpan.FromDays(1)) next = new TimeSpan(21, 0, 0);
        Schedules.Add(new ScheduleEntry { Time = next });
    }

    private void ApplyPreset(string? preset)
    {
        IEnumerable<TimeSpan> times = preset switch
        {
            "1" => [new TimeSpan(8, 0, 0)],
            "2" => [new TimeSpan(8, 0, 0), new TimeSpan(20, 0, 0)],
            "3" => [new TimeSpan(8, 0, 0), new TimeSpan(14, 0, 0), new TimeSpan(20, 0, 0)],
            "4" => [new TimeSpan(8, 0, 0), new TimeSpan(12, 0, 0), new TimeSpan(16, 0, 0), new TimeSpan(20, 0, 0)],
            _ => [],
        };
        SetScheduleTimes(times);
    }

    private void SetScheduleTimes(IEnumerable<TimeSpan> times)
    {
        Schedules.Clear();
        foreach (var t in times) Schedules.Add(new ScheduleEntry { Time = t });
        if (Schedules.Count == 0) Schedules.Add(new ScheduleEntry());
    }

    /// <summary>"Twice daily", "BD", "1-0-1", "3 times a day" → doses per day; null when unknown.</summary>
    internal static int? FrequencyPerDayFromText(string? frequency)
    {
        if (string.IsNullOrWhiteSpace(frequency)) return null;
        var t = frequency.Trim().ToLowerInvariant();

        var grid = DoseGrid().Match(t);
        if (grid.Success)
        {
            var count = grid.Value.Split('-', '+').Count(s => s.Trim() != "0");
            return count > 0 ? count : null;
        }

        if (Regex.IsMatch(t, @"\b(?:od|once|qd|daily|hs|nocte|night)\b")) return 1;
        if (Regex.IsMatch(t, @"\b(?:bd|bid|twice|two)\b")) return 2;
        if (Regex.IsMatch(t, @"\b(?:tds|tid|thrice|three)\b")) return 3;
        if (Regex.IsMatch(t, @"\b(?:qid|qds|four)\b")) return 4;
        var n = Regex.Match(t, @"(\d+)\s*(?:time|x)");
        if (n.Success && int.TryParse(n.Groups[1].Value, out var c) && c is > 0 and <= 6) return c;
        return null;
    }

    [GeneratedRegex(@"(?<![\d/])[012½](?:\s*[\-\+]\s*[012½]){2,3}(?![\d/])")]
    private static partial Regex DoseGrid();

    private static TimeSpan RoundToMinute(TimeSpan t) => new(t.Hours, t.Minutes, 0);

    // ── Save ─────────────────────────────────────────────────────────────────

    private async Task Save()
    {
        if (IsBusy) return;
        IsBusy = true;
        ClearError();
        try
        {
            if (string.IsNullOrWhiteSpace(Name)) { SetError("Enter the medicine name."); return; }
            int pd = 0;
            if (!IsOngoing && (!int.TryParse(PrescribedDaysText, out pd) || pd <= 0)) { SetError("Enter the prescribed number of days, or switch on Ongoing."); return; }
            if (!ValidationHelper.TryParseDouble(StockText, out var stock) || stock < 0) { SetError("Enter how many doses you have in stock (0 is fine)."); return; }
            if (Schedules.Count == 0) { SetError("Add at least one schedule time."); return; }

            var schedList = Schedules.Select(s => DateTimeHelper.ToTimeString(s.Time)).Distinct().ToList();
            var startDateTime = DateTimeHelper.ToDateTimeMinuteString(StartDate.Date.Add(StartTime));
            var name = Name.Trim();
            var instructions = string.IsNullOrWhiteSpace(Instructions) ? null : Instructions.Trim();
            var notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();

            if (_medicine is null)
            {
                var medicine = await _service.AddMedicine(name, stock, StockUnit,
                    startDateTime, null, instructions, notes, pd, IsOngoing, schedList);

                if (StartDate.Date < DateTime.Today)
                {
                    var pastDays = (DateTime.Today - StartDate.Date).Days;
                    var backfill = await Shell.Current.DisplayAlert(
                        "Log past doses?",
                        $"You started this medicine {pastDays} day{(pastDays == 1 ? "" : "s")} ago. Mark all past doses as taken?",
                        "Yes, mark as taken", "No");

                    if (backfill)
                    {
                        var count = await _service.BackfillPastDoses(medicine.Id, StartDate.Date, DateTime.Today.AddDays(-1));
                        await Shell.Current.DisplayAlert("Done", $"{count} past dose{(count == 1 ? "" : "s")} logged as taken.", "OK");
                    }
                }
            }
            else
            {
                _medicine.Name = name;
                _medicine.StockCount = stock;
                _medicine.StockUnit = StockUnit;
                _medicine.StartDate = startDateTime;
                _medicine.Instructions = instructions;
                _medicine.Notes = notes;
                _medicine.PrescribedDays = pd;
                _medicine.IsOngoing = IsOngoing ? 1 : 0;
                await _service.UpdateMedicine(_medicine, schedList);
            }

#if ANDROID
            HealthTracker.HealthWidget.RequestUpdate();
#endif
            SavedSuccessfully?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { SetError(ex.Message); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }
}
