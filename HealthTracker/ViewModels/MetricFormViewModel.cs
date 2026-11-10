using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;

namespace HealthTracker.ViewModels;

/// <summary>A selectable metric type chip on the form.</summary>
public class MetricTypeOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public MetricTypeOption(string type)
    {
        Type = type;
        DisplayName = MetricsDashboardViewModel.GetDisplayName(type);
        Icon = ChartTheme.MetricIcon(type);
        Token = ChartTheme.MetricToken(type);
    }

    public string Type { get; }
    public string DisplayName { get; }
    public string Icon { get; }
    public string Token { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Add + Edit in one ViewModel. Call <see cref="StartAdd"/> (optionally with a preselected type) or
/// <see cref="LoadForEdit"/> before the page appears. In edit mode the type is locked.
/// </summary>
public class MetricFormViewModel : BaseViewModel
{
    private readonly MetricsService _service;
    private HealthMetric? _editing;
    private string? _selectedType;
    private string _valueText = string.Empty;
    private string _secondValueText = string.Empty;
    private DateTime _recordedDate = DateTime.Today;
    private TimeSpan _recordedTime = DateTime.Now.TimeOfDay;
    private string? _notes;

    public ObservableCollection<MetricTypeOption> TypeOptions { get; } =
        new(MetricTypes.All.Select(t => new MetricTypeOption(t)));

    public ICommand SelectTypeCommand { get; }
    public ICommand SaveCommand { get; }
    public event EventHandler? SavedSuccessfully;

    public MetricFormViewModel(MetricsService service)
    {
        _service = service;
        SelectTypeCommand = new Command<object>(p =>
        {
            var type = p switch
            {
                MetricTypeOption o => o.Type,
                string s => s,
                _ => null
            };
            if (type is not null && !IsEditMode) SelectedType = type;
        });
        SaveCommand = new Command(async () => await Save());
    }

    // ---- Mode ----
    public bool IsEditMode => _editing is not null;
    public bool IsAddMode => !IsEditMode;
    public string PageTitle => IsEditMode ? "Edit reading" : "Add reading";
    public string SaveButtonText => IsEditMode ? "Save changes" : "Save reading";

    // ---- Type ----
    public string? SelectedType
    {
        get => _selectedType;
        private set
        {
            _selectedType = value;
            foreach (var o in TypeOptions) o.IsSelected = o.Type == value;
            if (HasError) ClearError();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasType));
            OnPropertyChanged(nameof(IsBloodPressure));
            OnPropertyChanged(nameof(IsSingleValue));
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(UnitOrPlaceholder));
            OnPropertyChanged(nameof(TypeDisplayName));
            OnPropertyChanged(nameof(TypeIcon));
            OnPropertyChanged(nameof(TypeToken));
            OnPropertyChanged(nameof(RangeHint));
            OnPropertyChanged(nameof(ValuePlaceholder));
        }
    }

    public bool HasType => !string.IsNullOrEmpty(_selectedType);
    public bool IsBloodPressure => _selectedType == MetricTypes.BloodPressure;
    public bool IsSingleValue => !IsBloodPressure;
    public string Unit => HasType ? MetricTypes.GetUnit(_selectedType!) : string.Empty;
    public string UnitOrPlaceholder => HasType ? Unit : "—";
    public string TypeDisplayName => HasType ? MetricsDashboardViewModel.GetDisplayName(_selectedType!) : "Choose a vital";
    public string TypeIcon => HasType ? ChartTheme.MetricIcon(_selectedType!) : "ic_monitor_heart.png";
    public string TypeToken => HasType ? ChartTheme.MetricToken(_selectedType!) : "Primary";

    public string ValuePlaceholder => _selectedType switch
    {
        MetricTypes.Weight => "e.g. 72.5",
        MetricTypes.HeartRate => "e.g. 72",
        MetricTypes.BloodSugar => "e.g. 110",
        MetricTypes.SpO2 => "e.g. 98",
        MetricTypes.Temperature => "e.g. 36.8",
        _ => "Value"
    };

    public string RangeHint
    {
        get
        {
            if (!HasType) return "Pick a vital above to see its accepted range.";
            if (IsBloodPressure)
            {
                var (sMin, sMax, dMin, dMax) = MetricTypes.GetBloodPressureRange();
                return $"Accepted: systolic {sMin:0}–{sMax:0}, diastolic {dMin:0}–{dMax:0} mmHg";
            }
            var (min, max) = MetricTypes.GetRange(_selectedType!);
            return $"Accepted range: {min.ToString("0.#", CultureInfo.InvariantCulture)}–{max.ToString("0.#", CultureInfo.InvariantCulture)} {Unit}";
        }
    }

    // ---- Fields ----
    public string ValueText
    {
        get => _valueText;
        set { _valueText = value; if (HasError) ClearError(); OnPropertyChanged(); }
    }

    public string SecondValueText
    {
        get => _secondValueText;
        set { _secondValueText = value; if (HasError) ClearError(); OnPropertyChanged(); }
    }

    public DateTime RecordedDate
    {
        get => _recordedDate;
        set { _recordedDate = value.Date; OnPropertyChanged(); }
    }

    public TimeSpan RecordedTime
    {
        get => _recordedTime;
        set { _recordedTime = value; OnPropertyChanged(); }
    }

    public DateTime MaxDate => DateTime.Today;

    public string? Notes
    {
        get => _notes;
        set { _notes = value; OnPropertyChanged(); }
    }

    // ---- Entry points ----

    /// <summary>Add mode. Pass null to let the user choose the type.</summary>
    public void StartAdd(string? preselectedType = null)
    {
        _editing = null;
        var now = DateTime.Now;
        RecordedDate = now.Date;
        RecordedTime = new TimeSpan(now.Hour, now.Minute, 0);
        ValueText = string.Empty;
        SecondValueText = string.Empty;
        Notes = null;
        ClearError();
        SelectedType = MetricTypes.All.Contains(preselectedType ?? string.Empty) ? preselectedType : null;
        RaiseModeChanged();
    }

    /// <summary>Edit mode. Works on a copy so a failed validation never corrupts the instance in the history list.</summary>
    public void LoadForEdit(HealthMetric metric)
    {
        _editing = new HealthMetric
        {
            Id = metric.Id,
            MetricType = metric.MetricType,
            Value = metric.Value,
            SecondValue = metric.SecondValue,
            Unit = metric.Unit,
            RecordedAt = metric.RecordedAt,
            Notes = metric.Notes
        };
        SelectedType = metric.MetricType;
        ValueText = metric.Value.ToString("0.##", CultureInfo.InvariantCulture);
        SecondValueText = metric.SecondValue?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        var dt = DateTimeHelper.TryParseStored(metric.RecordedAt, out var parsed) ? parsed : DateTime.Now;
        RecordedDate = dt.Date;
        RecordedTime = new TimeSpan(dt.Hour, dt.Minute, 0);
        Notes = metric.Notes;
        ClearError();
        RaiseModeChanged();
    }

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(IsAddMode));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(SaveButtonText));
    }

    private async Task Save()
    {
        if (IsBusy) return;
        IsBusy = true;
        ClearError();
        try
        {
            if (!HasType) { SetError("Choose which metric you're recording."); return; }

            if (!ValidationHelper.TryParseDouble(ValueText, out var value))
            {
                SetError(IsBloodPressure ? "Enter a valid systolic value." : "Enter a valid numeric value.");
                return;
            }

            double? second = null;
            if (IsBloodPressure)
            {
                if (!ValidationHelper.TryParseDouble(SecondValueText, out var sv)) { SetError("Enter a valid diastolic value."); return; }
                second = sv;
            }

            var recordedAt = RecordedDate.Date.Add(RecordedTime);
            if (recordedAt > DateTime.Now.AddMinutes(1)) { SetError("The recorded time can't be in the future."); return; }
            var stored = DateTimeHelper.ToDateTimeMinuteString(recordedAt);
            var notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();

            if (_editing is null)
            {
                await _service.AddMetric(_selectedType!, value, second, Unit, stored, notes);
            }
            else
            {
                _editing.Value = value;
                _editing.SecondValue = second;
                _editing.RecordedAt = stored;
                _editing.Notes = notes;
                await _service.UpdateMetric(_editing);
            }

            SavedSuccessfully?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException ex) { SetError(ex.Message); }
        catch (Exception ex) { SetError("Couldn't save this reading. Please try again."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }
}
