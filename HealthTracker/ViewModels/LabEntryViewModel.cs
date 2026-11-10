using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services;
using HealthTracker.Services.Labs;

namespace HealthTracker.ViewModels;

/// <summary>One test in the entry form. Empty rows are simply not saved.</summary>
public class LabEntryRow : INotifyPropertyChanged
{
    public const string NotTested = "Not tested";

    private readonly string? _sex;
    private string _valueInput = string.Empty;
    private string _selectedUnit;
    private string _selectedOption = NotTested;
    private string _refLowInput = string.Empty;
    private string _refHighInput = string.Empty;
    private string _customUnit = string.Empty;
    private bool _isRangeEditorOpen;
    private bool _rangeEdited;
    private bool _suppressRangeTracking;

    /// <summary>Catalog test.</summary>
    public LabEntryRow(LabTestDefinition def, string? sex, string? preferredUnit = null)
    {
        Definition = def;
        _sex = sex;
        Name = def.Name;
        IsQualitative = def.IsQualitative;
        Units = def.UnitNames.Where(u => u.Length > 0).ToList();
        var canonical = def.CanonicalUnit(preferredUnit);
        if (canonical is null && !def.IsQualitative && !string.IsNullOrWhiteSpace(preferredUnit))
        {
            // A unit the catalog cannot convert (as printed by some labs): keep it selectable so the value
            // is never relabelled; such a result simply has no catalog range.
            _selectedUnit = preferredUnit.Trim();
            Units.Add(_selectedUnit);
        }
        else _selectedUnit = canonical ?? def.Unit;
        Options = IsQualitative ? new[] { NotTested }.Concat(def.Options).ToList() : [];
        ExpectedText = def.Expected;
        ApplyCatalogRange();
        ToggleRangeCommand = new Command(() => IsRangeEditorOpen = !IsRangeEditorOpen);
    }

    /// <summary>Custom test (not in the catalog).</summary>
    public LabEntryRow(string name, bool qualitative, string unit = "", double? refLow = null, double? refHigh = null,
        string? expected = null)
    {
        Name = name.Trim();
        IsQualitative = qualitative;
        IsCustom = true;
        Units = [];
        _selectedUnit = unit;
        _customUnit = unit;
        Options = qualitative ? [NotTested, .. LabCatalog.NegPos] : [];
        ExpectedText = qualitative ? expected ?? "Negative" : null;
        SetRange(refLow, refHigh, edited: refLow is not null || refHigh is not null);
        ToggleRangeCommand = new Command(() => IsRangeEditorOpen = !IsRangeEditorOpen);
    }

    public LabTestDefinition? Definition { get; }
    public string Name { get; }
    public bool IsQualitative { get; }
    public bool IsNumeric => !IsQualitative;
    public bool IsCustom { get; }
    public string? ExpectedText { get; }
    public List<string> Units { get; }
    public List<string> Options { get; }
    public ICommand ToggleRangeCommand { get; }

    /// <summary>Carries the existing row when editing a saved result.</summary>
    public LabResult? Existing { get; set; }

    public bool HasUnitChoice => IsNumeric && !IsCustom && Units.Count > 1;
    public bool HasFixedUnit => IsNumeric && !IsCustom && Units.Count <= 1 && _selectedUnit.Length > 0;
    public bool ShowCustomUnit => IsNumeric && IsCustom;

    public string ValueInput
    {
        get => _valueInput;
        set { if (_valueInput == value) return; _valueInput = value ?? string.Empty; Raise(); RaiseState(); }
    }

    public string SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (string.IsNullOrEmpty(value) || _selectedUnit == value) return;
            _selectedUnit = value;
            Raise();
            // A typed or printed range belongs to the unit it was given in; otherwise follow the catalog.
            if (!_rangeEdited) ApplyCatalogRange();
            RaiseState();
        }
    }

    public string CustomUnit
    {
        get => _customUnit;
        set { if (_customUnit == value) return; _customUnit = value ?? string.Empty; _selectedUnit = _customUnit.Trim(); Raise(); RaiseState(); }
    }

    public string SelectedOption
    {
        get => _selectedOption;
        set { if (value is null || _selectedOption == value) return; _selectedOption = value; Raise(); RaiseState(); }
    }

    public string RefLowInput
    {
        get => _refLowInput;
        set { if (_refLowInput == value) return; _refLowInput = value ?? string.Empty; if (!_suppressRangeTracking) _rangeEdited = true; Raise(); RaiseState(); }
    }

    public string RefHighInput
    {
        get => _refHighInput;
        set { if (_refHighInput == value) return; _refHighInput = value ?? string.Empty; if (!_suppressRangeTracking) _rangeEdited = true; Raise(); RaiseState(); }
    }

    public bool IsRangeEditorOpen
    {
        get => _isRangeEditorOpen;
        set { if (_isRangeEditorOpen == value) return; _isRangeEditorOpen = value; Raise(); Raise(nameof(RangeToggleText)); }
    }

    public string RangeToggleText => IsRangeEditorOpen ? "Hide range" : "Use the range printed on my report";

    public bool HasValue => IsQualitative
        ? _selectedOption != NotTested
        : ValidationHelper.TryParseDouble(_valueInput, out _);

    /// <summary>Something was typed that is not a number.</summary>
    public bool HasInvalidInput => IsNumeric && !string.IsNullOrWhiteSpace(_valueInput) && !ValidationHelper.TryParseDouble(_valueInput, out _);

    private double? RefLow => ValidationHelper.TryParseDouble(_refLowInput, out var v) ? v : null;
    private double? RefHigh => ValidationHelper.TryParseDouble(_refHighInput, out var v) ? v : null;

    public string RangeHint
    {
        get
        {
            if (IsQualitative) return string.IsNullOrEmpty(ExpectedText) ? "No expected result for this test" : $"Expected: {ExpectedText}";
            var range = LabEvaluator.RangeText(RefLow, RefHigh, LabEvaluator.DecimalsFor(Definition, _selectedUnit));
            if (range.Length == 0) return "No reference range";
            return _selectedUnit.Length == 0 ? $"Reference {range}" : $"Reference {range} {_selectedUnit}";
        }
    }

    public LabFlag Flag
    {
        get
        {
            if (!HasValue) return LabFlag.None;
            if (IsQualitative)
                return string.IsNullOrEmpty(ExpectedText) ? LabFlag.None
                    : LabCatalog.Normalize(_selectedOption) == LabCatalog.Normalize(ExpectedText) ? LabFlag.Normal : LabFlag.Abnormal;
            ValidationHelper.TryParseDouble(_valueInput, out var v);
            return LabEvaluator.Evaluate(v, RefLow, RefHigh);
        }
    }

    public string FlagText => IsQualitative
        ? (Flag == LabFlag.Normal ? "As expected" : Flag == LabFlag.Abnormal ? "Check" : string.Empty)
        : LabEvaluator.Label(Flag);
    public string FlagToken => LabEvaluator.Token(Flag);
    public bool HasFlag => FlagText.Length > 0;

    /// <summary>Sets the range inputs. <paramref name="edited"/> marks it as the user's/report's own range.</summary>
    public void SetRange(double? low, double? high, bool edited)
    {
        _suppressRangeTracking = true;
        var decimals = LabEvaluator.DecimalsFor(Definition, _selectedUnit);
        RefLowInput = low is { } l ? LabEvaluator.Format(l, decimals) : string.Empty;
        RefHighInput = high is { } h ? LabEvaluator.Format(h, decimals) : string.Empty;
        _suppressRangeTracking = false;
        _rangeEdited = edited;
        RaiseState();
    }

    private void ApplyCatalogRange()
    {
        if (Definition is null || IsQualitative) return;
        var (low, high) = LabEvaluator.RangeIn(Definition, _sex, _selectedUnit);
        SetRange(low, high, edited: false);
    }

    /// <summary>The entry to save, or null when the row was left empty.</summary>
    public LabEntry? ToEntry()
    {
        if (!HasValue) return null;
        if (IsQualitative)
            return new LabEntry
            {
                TestCode = Definition?.Code ?? string.Empty, TestName = Name, IsQualitative = true,
                ValueText = _selectedOption, ExpectedText = ExpectedText,
            };

        ValidationHelper.TryParseDouble(_valueInput, out var value);
        return new LabEntry
        {
            TestCode = Definition?.Code ?? string.Empty, TestName = Name, Value = value, Unit = _selectedUnit,
            RefLow = RefLow, RefHigh = RefHigh,
            // Custom tests have no catalog range to fall back to, so "no range" is always the user's choice.
            HasCustomRange = _rangeEdited || IsCustom,
        };
    }

    private void RaiseState()
    {
        Raise(nameof(HasValue));
        Raise(nameof(HasInvalidInput));
        Raise(nameof(RangeHint));
        Raise(nameof(Flag));
        Raise(nameof(FlagText));
        Raise(nameof(FlagToken));
        Raise(nameof(HasFlag));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? StateChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A hit in the add-test search: a catalog test or a custom test the user entered before.</summary>
public sealed record LabSearchItem(string Name, string PanelName, LabTestDefinition? Definition, LabResult? CustomTemplate);

/// <summary>A panel shortcut chip in the entry form.</summary>
public sealed record LabPanelChip(string Code, string Name, string Icon, string ColorToken);

/// <summary>
/// Add lab results (a whole panel, single tests, or values read from a report) and edit one saved result.
/// </summary>
public class LabEntryViewModel : BaseViewModel
{
    private const int MaxSearchResults = 6;

    private readonly LabService _labs;
    private readonly UserProfileRepository _profiles;
    private string? _sex;
    private Dictionary<string, string> _preferredUnits = new(StringComparer.OrdinalIgnoreCase);
    private List<LabResult> _customTests = [];
    private bool _initialized;
    private DateTime _collectedDate = DateTime.Today;
    private string _labName = string.Empty;
    private string _searchText = string.Empty;
    private int? _reportId;
    private LabResult? _editing;
    private string? _presetTestCode;
    private (List<LabDraft> Drafts, DateTime? Date, string? LabName)? _prefill;
    private int? _editId;
    private string _importNote = string.Empty;

    public ObservableCollection<LabEntryRow> Rows { get; } = new();
    public ObservableCollection<LabSearchItem> SearchResults { get; } = new();
    public List<LabPanelChip> Panels { get; } = LabCatalog.Panels
        .Where(p => p.Code != LabCatalog.CustomPanel)
        .Select(p => new LabPanelChip(p.Code, p.Name, p.Icon, p.ColorToken))
        .ToList();

    public LabEntryViewModel(LabService labs, UserProfileRepository profiles)
    {
        _labs = labs;
        _profiles = profiles;
        AddPanelCommand = new Command<LabPanelChip>(AddPanel);
        AddSearchItemCommand = new Command<LabSearchItem>(AddSearchItem);
        RemoveRowCommand = new Command<LabEntryRow>(RemoveRow);
        SaveCommand = new Command(async () => await Save());
        DeleteCommand = new Command(() => DeleteRequested?.Invoke(this, EventArgs.Empty));
        AddCustomCommand = new Command(() => CustomTestRequested?.Invoke(this, SearchText.Trim()));
    }

    public ICommand AddPanelCommand { get; }
    public ICommand AddSearchItemCommand { get; }
    public ICommand RemoveRowCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand AddCustomCommand { get; }

    /// <summary>Raised after a successful save with a short message for the toast.</summary>
    public event EventHandler<string>? Saved;
    public event EventHandler? Deleted;
    /// <summary>The page confirms and then calls <see cref="DeleteExisting"/>.</summary>
    public event EventHandler? DeleteRequested;
    /// <summary>The page asks for the custom test's name/type and calls <see cref="AddCustomRow"/>. Argument: the search text.</summary>
    public event EventHandler<string>? CustomTestRequested;

    public bool IsEditMode => _editId is not null;
    public bool IsAddMode => !IsEditMode;
    public string PageTitle => IsEditMode ? "Edit result" : "Add lab results";
    public DateTime MaxDate => DateTime.Today;

    public DateTime CollectedDate
    {
        get => _collectedDate;
        set { if (_collectedDate == value) return; _collectedDate = value; OnPropertyChanged(); }
    }

    public string LabName
    {
        get => _labName;
        set { if (_labName == value) return; _labName = value ?? string.Empty; OnPropertyChanged(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (_searchText == value) return; _searchText = value ?? string.Empty; OnPropertyChanged(); UpdateSearch(); }
    }

    public bool HasSearchResults => SearchResults.Count > 0;
    public bool ShowCustomOption => IsAddMode && SearchText.Trim().Length >= 2;
    public string CustomOptionText => $"Add \"{SearchText.Trim()}\" as my own test";

    public bool HasRows => Rows.Count > 0;
    public bool ShowEmptyHint => IsAddMode && Rows.Count == 0;
    public int FilledCount => Rows.Count(r => r.HasValue);

    public string SaveButtonText => IsEditMode
        ? "Save changes"
        : FilledCount == 0 ? "Save results" : FilledCount == 1 ? "Save 1 result" : $"Save {FilledCount} results";

    public bool CanSave => !IsBusy && FilledCount > 0;

    /// <summary>"6 values read from your report. Check them before saving."</summary>
    public string ImportNote
    {
        get => _importNote;
        private set { _importNote = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasImportNote)); }
    }
    public bool HasImportNote => _importNote.Length > 0;

    // ── Entry points (called by the page before it is pushed) ───────────────────────────────

    public void PresetTest(string testCode) => _presetTestCode = testCode;

    public void Prefill(List<LabDraft> drafts, DateTime? date, string? labName, int? reportId)
    {
        _prefill = (drafts, date, labName);
        _reportId = reportId;
    }

    public void EditResult(int resultId) => _editId = resultId;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        IsBusy = true; ClearError();
        try
        {
            _sex = (await _profiles.GetAsync()).Sex;
            _preferredUnits = await _labs.GetPreferredUnits();
            _customTests = await _labs.GetCustomTests();

            if (_editId is { } id) await LoadExisting(id);
            else if (_prefill is { } p) ApplyPrefill(p.Drafts, p.Date, p.LabName);
            else if (_presetTestCode is { } code) AddTestByCode(code);
        }
        catch (Exception ex)
        {
            SetError("Couldn't open the form.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEditMode));
            OnPropertyChanged(nameof(IsAddMode));
            OnPropertyChanged(nameof(PageTitle));
            RaiseRowState();
        }
    }

    private async Task LoadExisting(int id)
    {
        _editing = await _labs.GetById(id);
        if (_editing is null) { SetError("This result no longer exists."); return; }

        if (DateTimeHelper.TryParseStored(_editing.CollectedOn, out var date)) CollectedDate = date;
        LabName = _editing.LabName ?? string.Empty;

        var def = LabCatalog.Find(_editing.TestCode);
        LabEntryRow row;
        if (def is not null)
        {
            row = new LabEntryRow(def, _sex, _editing.Unit);
            if (!def.IsQualitative) row.SetRange(_editing.RefLow, _editing.RefHigh, edited: true);
        }
        else
        {
            row = new LabEntryRow(_editing.TestName, _editing.IsQualitative == 1, _editing.Unit, _editing.RefLow,
                _editing.RefHigh, _editing.ExpectedText);
        }
        row.Existing = _editing;
        if (_editing.IsQualitative == 1)
            row.SelectedOption = row.Options.FirstOrDefault(o => o.Equals(_editing.ValueText, StringComparison.OrdinalIgnoreCase)) ?? LabEntryRow.NotTested;
        else if (_editing.Value is { } v)
            row.ValueInput = LabEvaluator.Format(v, LabEvaluator.DecimalsFor(def, _editing.Unit));
        AddRow(row);
    }

    private void ApplyPrefill(List<LabDraft> drafts, DateTime? date, string? labName)
    {
        if (date is { } d && d.Date <= DateTime.Today) CollectedDate = d.Date;
        if (!string.IsNullOrWhiteSpace(labName)) LabName = labName.Trim();

        foreach (var draft in drafts)
        {
            if (draft.Definition is { } def && Rows.Any(r => r.Definition?.Code == def.Code)) continue;

            LabEntryRow row;
            if (draft.Definition is { } known)
            {
                row = new LabEntryRow(known, _sex, draft.Unit);
                // Keep the printed range only when it is in a unit we recognise for this test.
                if (!known.IsQualitative && (draft.RefLow is not null || draft.RefHigh is not null)
                    && known.CanonicalUnit(draft.Unit) is not null)
                    row.SetRange(draft.RefLow, draft.RefHigh, edited: true);
            }
            else
            {
                row = new LabEntryRow(draft.Name, draft.IsQualitative, draft.Unit, draft.RefLow, draft.RefHigh);
            }

            if (draft.IsQualitative)
                row.SelectedOption = row.Options.FirstOrDefault(o => o.Equals(draft.ValueText, StringComparison.OrdinalIgnoreCase)) ?? LabEntryRow.NotTested;
            else if (draft.Value is { } v)
                row.ValueInput = LabEvaluator.Format(v, LabEvaluator.DecimalsFor(draft.Definition, draft.Unit));
            AddRow(row);
        }

        var count = Rows.Count(r => r.HasValue);
        ImportNote = count == 0
            ? "No lab values could be read from this report. Add them below."
            : count == 1
                ? "1 value was read from your report. Check it against the paper before saving."
                : $"{count} values were read from your report. Check them against the paper before saving.";
    }

    // ── Building the list ────────────────────────────────────────────────────────────────────

    private void AddPanel(LabPanelChip? panel)
    {
        if (panel is null) return;
        foreach (var def in LabCatalog.TestsIn(panel.Code))
            if (!Rows.Any(r => r.Definition?.Code == def.Code))
                AddRow(NewRow(def), raise: false);
        RaiseRowState();
    }

    private void AddTestByCode(string code)
    {
        if (LabCatalog.Find(code) is { } def)
        {
            if (!Rows.Any(r => r.Definition?.Code == def.Code)) AddRow(NewRow(def));
            return;
        }
        var template = _customTests.FirstOrDefault(c => c.TestCode.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (template is not null) AddCustomFromTemplate(template);
    }

    private LabEntryRow NewRow(LabTestDefinition def)
        => new(def, _sex, _preferredUnits.TryGetValue(def.Code, out var unit) ? unit : null);

    private void AddSearchItem(LabSearchItem? item)
    {
        if (item is null) return;
        if (item.Definition is { } def)
        {
            if (!Rows.Any(r => r.Definition?.Code == def.Code)) AddRow(NewRow(def));
        }
        else if (item.CustomTemplate is { } template) AddCustomFromTemplate(template);
        SearchText = string.Empty;
    }

    private void AddCustomFromTemplate(LabResult template)
    {
        if (Rows.Any(r => r.IsCustom && LabCatalog.CustomCode(r.Name) == template.TestCode)) return;
        AddRow(new LabEntryRow(template.TestName, template.IsQualitative == 1, template.Unit, template.RefLow,
            template.RefHigh, template.ExpectedText));
    }

    /// <summary>Adds a user-defined test. Returns false when the name is empty or already in the list.</summary>
    public bool AddCustomRow(string name, bool qualitative)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        // A name the catalog knows becomes the catalog test, so its history and ranges line up.
        if (LabCatalog.Match(name) is { } def)
        {
            if (!Rows.Any(r => r.Definition?.Code == def.Code)) AddRow(NewRow(def));
            SearchText = string.Empty;
            return true;
        }
        var code = LabCatalog.CustomCode(name);
        if (Rows.Any(r => r.IsCustom && LabCatalog.CustomCode(r.Name) == code)) return false;
        AddRow(new LabEntryRow(name, qualitative));
        SearchText = string.Empty;
        return true;
    }

    private void AddRow(LabEntryRow row, bool raise = true)
    {
        row.StateChanged += OnRowStateChanged;
        Rows.Add(row);
        if (raise) RaiseRowState();
    }

    private void RemoveRow(LabEntryRow? row)
    {
        if (row is null || IsEditMode) return;
        row.StateChanged -= OnRowStateChanged;
        Rows.Remove(row);
        RaiseRowState();
    }

    private void OnRowStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(FilledCount));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(CanSave));
    }

    private void RaiseRowState()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnRowStateChanged(this, EventArgs.Empty);
    }

    private void UpdateSearch()
    {
        SearchResults.Clear();
        var query = SearchText.Trim();
        if (query.Length >= 2 && IsAddMode)
        {
            var normalized = LabCatalog.Normalize(query);
            foreach (var custom in _customTests.Where(c => LabCatalog.Normalize(c.TestName).Contains(normalized, StringComparison.Ordinal)).Take(2))
                SearchResults.Add(new LabSearchItem(custom.TestName, "My tests", null, custom));
            foreach (var def in LabCatalog.Search(query)
                         .Where(d => !Rows.Any(r => r.Definition?.Code == d.Code))
                         .Take(MaxSearchResults - SearchResults.Count))
                SearchResults.Add(new LabSearchItem(def.Name, LabCatalog.Panel(def.PanelCode).Name, def, null));
        }
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowCustomOption));
        OnPropertyChanged(nameof(CustomOptionText));
    }

    // ── Saving ───────────────────────────────────────────────────────────────────────────────

    private async Task Save()
    {
        if (IsBusy) return;
        ClearError();

        var invalid = Rows.FirstOrDefault(r => r.HasInvalidInput);
        if (invalid is not null) { SetError($"{invalid.Name}: enter a number, for example 5.6."); return; }

        IsBusy = true; OnPropertyChanged(nameof(CanSave));
        try
        {
            if (IsEditMode) await SaveEdit();
            else await SaveNew();
        }
        catch (ArgumentException ex) { SetError(ex.Message); }
        catch (Exception ex)
        {
            SetError("Couldn't save. Please try again.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanSave)); }
    }

    private async Task SaveNew()
    {
        var entries = Rows.Select(r => r.ToEntry()).Where(e => e is not null).Select(e => e!).ToList();
        if (entries.Count == 0) { SetError("Enter at least one result."); return; }

        var saved = await _labs.AddResults(entries, DateTimeHelper.ToDateString(CollectedDate), LabName, _reportId);
        var calculated = saved.Count(r => r.IsCalculated == 1);
        var message = saved.Count - calculated == 1 ? "1 result saved" : $"{saved.Count - calculated} results saved";
        if (calculated > 0) message += $" · {calculated} calculated";
        Saved?.Invoke(this, message);
    }

    private async Task SaveEdit()
    {
        if (_editing is null || Rows.Count == 0) return;
        var entry = Rows[0].ToEntry();
        if (entry is null) { SetError("Enter a result, or delete this entry instead."); return; }

        _editing.CollectedOn = DateTimeHelper.ToDateString(CollectedDate);
        _editing.LabName = string.IsNullOrWhiteSpace(LabName) ? null : LabName.Trim();
        if (entry.IsQualitative)
        {
            _editing.ValueText = entry.ValueText;
        }
        else
        {
            _editing.Value = entry.Value;
            _editing.Unit = entry.Unit;
            _editing.RefLow = entry.RefLow;
            _editing.RefHigh = entry.RefHigh;
        }
        await _labs.UpdateResult(_editing);
        Saved?.Invoke(this, "Result updated");
    }

    public async Task DeleteExisting()
    {
        if (_editing is null) return;
        try
        {
            await _labs.DeleteResult(_editing);
            Deleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SetError("Couldn't delete this result.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
