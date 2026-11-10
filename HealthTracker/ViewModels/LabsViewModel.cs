using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Services;
using HealthTracker.Services.Labs;

namespace HealthTracker.ViewModels;

/// <summary>Labs tab: newest value of every test grouped by panel, or every sitting grouped by date.</summary>
public class LabsViewModel : BaseViewModel
{
    private readonly LabService _labs;
    private List<LabLatest> _latest = [];
    private List<LabBatch> _batches = [];
    private bool _byDate;
    private bool _attentionOnly;
    private bool _isRefreshing;
    private bool _hasLoadedOnce;

    public ObservableCollection<LabGroup> Groups { get; } = new();

    public LabsViewModel(LabService labs)
    {
        _labs = labs;
        LoadCommand = new Command(async () => await Load());
        ShowByPanelCommand = new Command(() => ByDate = false);
        ShowByDateCommand = new Command(() => ByDate = true);
        ToggleAttentionCommand = new Command(() => AttentionOnly = !AttentionOnly);
        AddCommand = new Command(() => AddRequested?.Invoke(this, EventArgs.Empty));
    }

    public ICommand LoadCommand { get; }
    public ICommand ShowByPanelCommand { get; }
    public ICommand ShowByDateCommand { get; }
    public ICommand ToggleAttentionCommand { get; }
    public ICommand AddCommand { get; }

    /// <summary>Raised by the empty-state action; the page navigates.</summary>
    public event EventHandler? AddRequested;

    public bool ByDate
    {
        get => _byDate;
        set { if (_byDate == value) return; _byDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(ByPanel)); Rebuild(); }
    }
    public bool ByPanel => !_byDate;

    public bool AttentionOnly
    {
        get => _attentionOnly;
        set { if (_attentionOnly == value) return; _attentionOnly = value; OnPropertyChanged(); Rebuild(); }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { if (_isRefreshing == value) return; _isRefreshing = value; OnPropertyChanged(); }
    }

    public int TestCount => _latest.Count;
    public int AttentionCount => _latest.Count(l => LabEvaluator.NeedsAttention(l.Flag));
    public bool HasData => _latest.Count > 0;
    public bool HasAttention => AttentionCount > 0;
    public bool IsEmpty => _hasLoadedOnce && !IsBusy && _latest.Count == 0;
    /// <summary>The filter hides everything although there is data.</summary>
    public bool IsFilteredEmpty => HasData && Groups.Count == 0;

    public string HeadlineText => !HasData
        ? "No lab results yet"
        : AttentionCount == 0
            ? "Everything in range"
            : AttentionCount == 1 ? "1 result to check" : $"{AttentionCount} results to check";

    public string SummaryText
    {
        get
        {
            if (!HasData) return "Add results from a report and track how each value changes.";
            var tests = TestCount == 1 ? "1 test tracked" : $"{TestCount} tests tracked";
            var last = _batches.Count > 0 && DateTimeHelper.TryParseStored(_batches[0].CollectedOn, out var d)
                ? $" · last sample {d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}"
                : string.Empty;
            return tests + last;
        }
    }

    public string AttentionChipText => AttentionOnly ? "Showing results to check" : "Show only results to check";

    public async Task Load()
    {
        if (IsBusy) { IsRefreshing = false; return; }
        IsBusy = true; ClearError();
        try
        {
            var all = await _labs.GetAll();
            _latest = LabService.LatestPerTest(all);
            _batches = await _labs.GetBatches();
            Rebuild();
        }
        catch (Exception ex)
        {
            SetError("Couldn't load your lab results.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            _hasLoadedOnce = true;
            IsBusy = false;
            IsRefreshing = false;
            RaiseSummary();
        }
    }

    public async Task DeleteBatch(string batchId)
    {
        try
        {
            await _labs.DeleteBatch(batchId);
            await Load();
        }
        catch (Exception ex)
        {
            SetError("Couldn't delete those results.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void Rebuild()
    {
        Groups.Clear();
        if (_byDate)
        {
            foreach (var batch in _batches)
            {
                var rows = batch.Results
                    .Select(r => new LabRowItem(r))
                    .Where(r => !_attentionOnly || LabEvaluator.NeedsAttention(r.Flag))
                    .ToList();
                if (rows.Count == 0) continue;

                var title = DateTimeHelper.TryParseStored(batch.CollectedOn, out var d)
                    ? d.ToString("ddd, dd MMM yyyy", CultureInfo.InvariantCulture)
                    : batch.CollectedOn;
                var count = batch.Results.Count == 1 ? "1 result" : $"{batch.Results.Count} results";
                var subtitle = string.IsNullOrWhiteSpace(batch.LabName) ? count : $"{batch.LabName} · {count}";
                Groups.Add(new LabGroup(title, subtitle, "ic_event.png", "Primary", rows, batch.BatchId));
            }
        }
        else
        {
            foreach (var panel in LabCatalog.Panels)
            {
                var rows = _latest
                    .Where(l => l.Latest.PanelCode.Equals(panel.Code, StringComparison.OrdinalIgnoreCase))
                    .Where(l => !_attentionOnly || LabEvaluator.NeedsAttention(l.Flag))
                    .Select(l => new LabRowItem(l.Latest, l.Previous))
                    .ToList();
                if (rows.Count == 0) continue;
                Groups.Add(new LabGroup(panel.Name, string.Empty, panel.Icon, panel.ColorToken, rows));
            }
        }
        RaiseSummary();
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(TestCount));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(HeadlineText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(AttentionChipText));
    }
}
