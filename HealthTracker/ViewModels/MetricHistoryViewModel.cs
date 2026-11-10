using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using Paint = LiveChartsCore.Painting.Paint;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace HealthTracker.ViewModels;

/// <summary>Chart + stats + record list for one metric type. Range: 7 / 30 / 90 days or all.</summary>
public class MetricHistoryViewModel : BaseViewModel
{
    private readonly MetricsService _service;
    private string _metricType = MetricTypes.Weight;
    private int _rangeDays = 30;
    private List<HealthMetric> _all = new();

    public string MetricType
    {
        get => _metricType;
        set
        {
            _metricType = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(AccentToken));
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(IsBloodPressure));
            OnPropertyChanged(nameof(LegendPosition));
        }
    }

    public string Unit => MetricTypes.GetUnit(_metricType);
    public string DisplayName => MetricsDashboardViewModel.GetDisplayName(_metricType);
    public string AccentToken => ChartTheme.MetricToken(_metricType);
    public string Icon => ChartTheme.MetricIcon(_metricType);
    public bool IsBloodPressure => _metricType == MetricTypes.BloodPressure;

    /// <summary>0 means "all".</summary>
    public int RangeDays
    {
        get => _rangeDays;
        set
        {
            if (_rangeDays == value) return;
            _rangeDays = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Is7Days));
            OnPropertyChanged(nameof(Is30Days));
            OnPropertyChanged(nameof(Is90Days));
            OnPropertyChanged(nameof(IsAllTime));
            Rebuild();
        }
    }

    public bool Is7Days => _rangeDays == 7;
    public bool Is30Days => _rangeDays == 30;
    public bool Is90Days => _rangeDays == 90;
    public bool IsAllTime => _rangeDays == 0;

    public string RangeLabel => _rangeDays switch
    {
        7 => "Last 7 days",
        30 => "Last 30 days",
        90 => "Last 90 days",
        _ => "All time"
    };

    public ObservableCollection<MetricReadingItem> History { get; } = new();

    public ISeries[] Series { get; private set; } = Array.Empty<ISeries>();
    public ICartesianAxis[] XAxes { get; private set; } = Array.Empty<ICartesianAxis>();
    public ICartesianAxis[] YAxes { get; private set; } = Array.Empty<ICartesianAxis>();

    /// <summary>Legend only matters for blood pressure (two series); hidden otherwise.</summary>
    public LegendPosition LegendPosition => IsBloodPressure ? LegendPosition.Bottom : LegendPosition.Hidden;
    public Paint? LegendTextPaint { get; private set; }
    public Paint? TooltipTextPaint { get; private set; }
    public Paint? TooltipBackgroundPaint { get; private set; }

    /// <summary>True when at least two points are in range; a single reading is shown as stats only.</summary>
    public bool HasChart => Series.Length > 0;
    public bool HasData => _all.Count > 0;
    public bool HasReadingsInRange => CountInRange > 0;
    public string ReadingsHeader => CountInRange == 1 ? "1 reading" : $"{CountInRange} readings";
    public string LatestText { get; private set; } = "—";
    public string LatestDateText { get; private set; } = string.Empty;
    public string MinText { get; private set; } = "—";
    public string MaxText { get; private set; } = "—";
    public string AverageText { get; private set; } = "—";
    public string TrendText { get; private set; } = string.Empty;
    public string TrendIcon { get; private set; } = "ic_trending_flat.png";
    public string TrendToken { get; private set; } = "TextSecondary";
    public int CountInRange { get; private set; }

    public ICommand DeleteCommand { get; }
    public ICommand SetRangeCommand { get; }
    /// <summary>Bound to the empty state's action; the page listens to <see cref="AddReadingRequested"/> and navigates.</summary>
    public ICommand AddReadingCommand { get; }
    public event EventHandler? AddReadingRequested;

    public MetricHistoryViewModel(MetricsService service)
    {
        _service = service;
        DeleteCommand = new Command<object>(async p => await Delete(p));
        SetRangeCommand = new Command<string>(s => { if (int.TryParse(s, out var d)) RangeDays = d; });
        AddReadingCommand = new Command(() => AddReadingRequested?.Invoke(this, EventArgs.Empty));
    }

    private async Task Delete(object? parameter)
    {
        var metric = parameter switch
        {
            HealthMetric m => m,
            MetricReadingItem item => item.Metric,
            _ => null
        };
        if (metric is null) return;
        try
        {
            await _service.DeleteMetric(metric);
            _all.RemoveAll(m => m.Id == metric.Id);
            Rebuild();
        }
        catch (Exception ex) { SetError("Couldn't delete that reading."); System.Diagnostics.Debug.WriteLine(ex); }
    }

    public async Task Load(string metricType)
    {
        if (IsBusy) return;
        MetricType = metricType;
        IsBusy = true; ClearError();
        try
        {
            // Fetch everything once; range filtering happens in memory so switching ranges is instant.
            _all = await _service.GetHistoryByType(metricType, 1000);
            Rebuild();
        }
        catch (Exception ex) { SetError("Couldn't load this metric's history."); System.Diagnostics.Debug.WriteLine(ex); }
        finally { IsBusy = false; }
    }

    /// <summary>Re-filters and re-resolves theme colours; call when the range or the app theme changes.</summary>
    public void Rebuild()
    {
        var cutoff = _rangeDays == 0 ? DateTime.MinValue : DateTime.Today.AddDays(-_rangeDays + 1);
        var inRange = _all
            .Select(m => (Metric: m, Date: DateTimeHelper.TryParseStored(m.RecordedAt, out var d) ? d : DateTime.MinValue))
            .Where(x => x.Date >= cutoff)
            .OrderByDescending(x => x.Date)
            .ToList();

        History.Clear();
        foreach (var x in inRange) History.Add(new MetricReadingItem(x.Metric, x.Date, AccentToken));
        CountInRange = inRange.Count;

        // Theme-dependent paints for legend/tooltip, re-resolved on every rebuild so theme switches apply.
        LegendTextPaint = new SolidColorPaint(ChartTheme.TextSecondary);
        TooltipTextPaint = new SolidColorPaint(ChartTheme.Resolve("TextPrimary", "#0D1B2A"));
        TooltipBackgroundPaint = new SolidColorPaint(ChartTheme.Resolve("SurfaceElevated", "#FFFFFF"));

        if (inRange.Count == 0)
        {
            Series = Array.Empty<ISeries>();
            LatestText = MinText = MaxText = AverageText = "—";
            LatestDateText = TrendText = string.Empty;
            TrendIcon = "ic_trending_flat.png";
            TrendToken = "TextSecondary";
            RaiseAll();
            return;
        }

        var unit = Unit;
        var latest = inRange[0];
        var values = inRange.Select(x => x.Metric.Value).ToList();

        LatestText = MetricsDashboardViewModel.FormatValue(latest.Metric);
        LatestDateText = latest.Date.ToString("ddd, dd MMM yyyy · HH:mm", CultureInfo.InvariantCulture);

        if (IsBloodPressure)
        {
            var dia = inRange.Where(x => x.Metric.SecondValue.HasValue).Select(x => x.Metric.SecondValue!.Value).ToList();
            MinText = dia.Count == 0 ? $"{values.Min():0}" : $"{values.Min():0}/{dia.Min():0}";
            MaxText = dia.Count == 0 ? $"{values.Max():0}" : $"{values.Max():0}/{dia.Max():0}";
            AverageText = dia.Count == 0 ? $"{values.Average():0}" : $"{values.Average():0}/{dia.Average():0}";
        }
        else
        {
            MinText = values.Min().ToString("0.#", CultureInfo.InvariantCulture);
            MaxText = values.Max().ToString("0.#", CultureInfo.InvariantCulture);
            AverageText = values.Average().ToString("0.#", CultureInfo.InvariantCulture);
        }

        if (inRange.Count >= 2)
        {
            var diff = latest.Metric.Value - inRange[^1].Metric.Value;
            if (Math.Abs(diff) < 0.05)
            {
                TrendText = "Stable over this period";
                TrendIcon = "ic_trending_flat.png";
                TrendToken = "TextSecondary";
            }
            else
            {
                var what = IsBloodPressure ? "systolic" : unit;
                TrendText = $"{(diff > 0 ? "+" : "")}{diff.ToString("0.#", CultureInfo.InvariantCulture)} {what} since {inRange[^1].Date.ToString("dd MMM", CultureInfo.InvariantCulture)}";
                TrendIcon = diff > 0 ? "ic_trending_up.png" : "ic_trending_down.png";
                TrendToken = AccentToken;
            }
            BuildChart(inRange.OrderBy(x => x.Date).ToList());
        }
        else
        {
            TrendText = "Add another reading to see a trend";
            TrendIcon = "ic_trending_flat.png";
            TrendToken = "TextSecondary";
            Series = Array.Empty<ISeries>();
        }

        RaiseAll();
    }

    private void BuildChart(List<(HealthMetric Metric, DateTime Date)> ordered)
    {
        var accent = ChartTheme.Metric(_metricType);
        var textColor = ChartTheme.TextSecondary;
        var gridColor = ChartTheme.Outline;

        var primaryPoints = ordered
            .Select(x => new DateTimePoint(x.Date, x.Metric.Value))
            .ToList();

        var series = new List<ISeries>
        {
            MakeLine(primaryPoints, accent, IsBloodPressure ? "Systolic" : DisplayName)
        };

        if (IsBloodPressure)
        {
            var diastolic = ordered
                .Where(x => x.Metric.SecondValue.HasValue)
                .Select(x => new DateTimePoint(x.Date, x.Metric.SecondValue!.Value))
                .ToList();
            series.Add(MakeLine(diastolic, ChartTheme.Resolve("Info"), "Diastolic"));
        }

        Series = series.ToArray();

        var span = ordered[^1].Date - ordered[0].Date;

        // Readings within the same day need an hourly axis; a day-based step leaves the axis with no
        // labels at all when every point falls inside one day.
        var (unit, step, labelFormat) = span.TotalDays switch
        {
            < 1 => (TimeSpan.FromHours(1), TimeSpan.FromHours(Math.Max(1, Math.Ceiling(span.TotalHours / 4))), "HH:mm"),
            > 180 => (TimeSpan.FromDays(1), TimeSpan.FromDays(Math.Max(1, span.TotalDays / 5)), "MMM yy"),
            _ => (TimeSpan.FromDays(1), TimeSpan.FromDays(Math.Max(1, span.TotalDays / 5)), "dd MMM")
        };

        XAxes = new ICartesianAxis[]
        {
            new DateTimeAxis(unit, d => d.ToString(labelFormat, CultureInfo.InvariantCulture))
            {
                LabelsPaint = new SolidColorPaint(textColor),
                TextSize = 11,
                SeparatorsPaint = null,
                MinStep = step.Ticks,
            }
        };
        YAxes = new ICartesianAxis[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(textColor),
                TextSize = 11,
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                Labeler = v => v.ToString("0.#", CultureInfo.InvariantCulture),
            }
        };
    }

    private static LineSeries<DateTimePoint> MakeLine(List<DateTimePoint> points, SKColor color, string name) => new()
    {
        Name = name,
        Values = points,
        Stroke = new SolidColorPaint(color) { StrokeThickness = 3 },
        Fill = new SolidColorPaint(color.WithAlpha(28)),
        GeometryStroke = new SolidColorPaint(color) { StrokeThickness = 2 },
        GeometryFill = new SolidColorPaint(ChartTheme.Surface),
        GeometrySize = 10,
        LineSmoothness = 0.6,
        // Draw the value next to each point for sparse series only; dense series get tooltips.
        DataLabelsPaint = points.Count <= 8 ? new SolidColorPaint(color) : null,
        DataLabelsSize = 11,
        DataLabelsPosition = DataLabelsPosition.Top,
        DataLabelsFormatter = p => p.Coordinate.PrimaryValue.ToString("0.#", CultureInfo.InvariantCulture),
        XToolTipLabelFormatter = p => new DateTime((long)p.Coordinate.SecondaryValue).ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
        YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("0.#", CultureInfo.InvariantCulture),
    };

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Series));
        OnPropertyChanged(nameof(XAxes));
        OnPropertyChanged(nameof(YAxes));
        OnPropertyChanged(nameof(HasChart));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(HasReadingsInRange));
        OnPropertyChanged(nameof(LatestText));
        OnPropertyChanged(nameof(LatestDateText));
        OnPropertyChanged(nameof(MinText));
        OnPropertyChanged(nameof(MaxText));
        OnPropertyChanged(nameof(AverageText));
        OnPropertyChanged(nameof(TrendText));
        OnPropertyChanged(nameof(TrendIcon));
        OnPropertyChanged(nameof(TrendToken));
        OnPropertyChanged(nameof(CountInRange));
        OnPropertyChanged(nameof(ReadingsHeader));
        OnPropertyChanged(nameof(RangeLabel));
        OnPropertyChanged(nameof(LegendPosition));
        OnPropertyChanged(nameof(LegendTextPaint));
        OnPropertyChanged(nameof(TooltipTextPaint));
        OnPropertyChanged(nameof(TooltipBackgroundPaint));
    }
}
