using System.Collections.ObjectModel;
using System.Globalization;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services;
using HealthTracker.Services.Labs;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace HealthTracker.ViewModels;

/// <summary>One test over time: newest value against its range, a trend chart with the reference band, and every result.</summary>
public class LabTestDetailViewModel : BaseViewModel
{
    private readonly LabService _labs;
    private string _testCode = string.Empty;
    private List<LabResult> _history = [];

    public LabTestDetailViewModel(LabService labs)
    {
        _labs = labs;
    }

    public ObservableCollection<LabRowItem> History { get; } = new();

    public string TestCode => _testCode;
    public void SetTest(string testCode) => _testCode = testCode;

    private LabTestDefinition? Definition => LabCatalog.Find(_testCode);
    private LabResult? Latest => _history.Count > 0 ? _history[0] : null;

    // ── Header ───────────────────────────────────────────────────────────────────────────────
    public string TestName => Definition?.Name ?? Latest?.TestName ?? "Lab test";
    private LabPanel Panel => LabCatalog.Panel(Definition?.PanelCode ?? Latest?.PanelCode);
    public string PanelName => Panel.Name;
    public string PanelIcon => Panel.Icon;
    public string PanelToken => Panel.ColorToken;
    public string About => Definition?.About ?? string.Empty;
    public bool HasAbout => About.Length > 0;

    public bool HasData => _history.Count > 0;
    public bool IsEmpty => !IsBusy && _history.Count == 0;
    public bool IsNumeric => Latest is { IsQualitative: 0 };

    public string LatestValueText => Latest is null ? "—" : LabEvaluator.ValueText(Latest);
    public string LatestUnit => Latest is { IsQualitative: 0 } l ? l.Unit : string.Empty;
    public string LatestDateText => Latest is not null && DateTimeHelper.TryParseStored(Latest.CollectedOn, out var d)
        ? d.ToString("ddd, dd MMM yyyy", CultureInfo.InvariantCulture)
        : string.Empty;
    public string ReferenceText => Latest is null ? string.Empty : LabEvaluator.ReferenceText(Latest);
    public bool HasReference => ReferenceText.Length > 0;

    private LabFlag LatestFlag => Latest is null ? LabFlag.None : LabEvaluator.Evaluate(Latest);
    public string FlagToken => LabEvaluator.Token(LatestFlag);
    public string ValueToken => LabEvaluator.NeedsAttention(LatestFlag) ? FlagToken : "TextPrimary";
    public bool HasFlag => FlagText.Length > 0;
    public string FlagText => Latest is null ? string.Empty
        : Latest.IsQualitative == 1
            ? (LatestFlag == LabFlag.Normal ? "As expected" : LatestFlag == LabFlag.Abnormal ? "Worth discussing with your doctor" : string.Empty)
            : LatestFlag switch
            {
                LabFlag.Normal => "Within the reference range",
                LabFlag.Low => "Below the reference range",
                LabFlag.High => "Above the reference range",
                _ => string.Empty
            };
    public bool IsCalculated => Latest is { IsCalculated: 1 };
    public string CalculatedText => Latest?.Notes ?? "Calculated from other results";

    // ── Range bar: a track whose shaded zone is the reference range, with a marker at the value ──
    public bool HasRangeBar { get; private set; }
    public Rect NormalZoneBounds { get; private set; } = new(0, 0, 1, 1);
    public Rect MarkerBounds { get; private set; } = new(0.5, 0.5, 18, 18);
    public string RangeLowLabel { get; private set; } = string.Empty;
    public string RangeHighLabel { get; private set; } = string.Empty;

    // ── Stats ────────────────────────────────────────────────────────────────────────────────
    public bool HasStats { get; private set; }
    public string CountText => _history.Count.ToString(CultureInfo.InvariantCulture);
    public string LowestText { get; private set; } = "—";
    public string HighestText { get; private set; } = "—";
    public string ChangeText { get; private set; } = string.Empty;
    public bool HasChange => ChangeText.Length > 0;

    // ── Chart ────────────────────────────────────────────────────────────────────────────────
    public ISeries[] Series { get; private set; } = [];
    public ICartesianAxis[] XAxes { get; private set; } = [];
    public ICartesianAxis[] YAxes { get; private set; } = [];
    public RectangularSection[] Sections { get; private set; } = [];
    public bool HasChart => Series.Length > 0;
    public SolidColorPaint TooltipTextPaint { get; private set; } = new(SKColors.Black);
    public SolidColorPaint TooltipBackgroundPaint { get; private set; } = new(SKColors.White);
    public string ChartCaption { get; private set; } = string.Empty;

    public string HistoryHeader => _history.Count == 1 ? "1 result" : $"{_history.Count} results";

    public async Task Load()
    {
        if (IsBusy) return;
        IsBusy = true; ClearError();
        try
        {
            _history = await _labs.GetHistory(_testCode);
        }
        catch (Exception ex)
        {
            SetError("Couldn't load this test.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    /// <summary>Rebuilds rows and chart paints; also called on a theme change (paints hold resolved colours).</summary>
    public void Rebuild()
    {
        History.Clear();
        foreach (var r in _history) History.Add(new LabRowItem(r, showDateAsTitle: true));

        BuildRangeBar();
        BuildStatsAndChart();
        RefreshThemeBindings();
    }

    private void BuildRangeBar()
    {
        HasRangeBar = false;
        if (Latest is not { IsQualitative: 0, Value: { } value } latest) return;
        var (low, high) = (latest.RefLow, latest.RefHigh);
        if (low is null && high is null) return;

        // The window always shows the reference range plus room on the side(s) a value can fall outside.
        double min, max;
        if (low is { } l && high is { } h)
        {
            var span = Math.Max(h - l, 1e-9);
            min = l - span / 2;
            max = h + span / 2;
        }
        else if (high is { } onlyHigh)
        {
            min = 0;
            max = onlyHigh * 1.5;
        }
        else
        {
            min = low!.Value * 0.5;
            max = low.Value * 2;
        }
        if (max <= min) return;

        double Position(double v) => Math.Clamp((v - min) / (max - min), 0, 1);
        var zoneStart = low is { } zl ? Position(zl) : 0;
        var zoneEnd = high is { } zh ? Position(zh) : 1;
        var zoneWidth = Math.Clamp(zoneEnd - zoneStart, 0.02, 1);

        // AbsoluteLayout proportional X is measured over (container - child) width, so convert.
        NormalZoneBounds = new Rect(zoneWidth >= 1 ? 0 : zoneStart / (1 - zoneWidth), 0, zoneWidth, 1);
        MarkerBounds = new Rect(Position(value), 0.5, 18, 18);

        var decimals = LabEvaluator.DecimalsFor(Definition, latest.Unit);
        RangeLowLabel = low is { } ll ? LabEvaluator.Format(ll, decimals) : string.Empty;
        RangeHighLabel = high is { } hh ? LabEvaluator.Format(hh, decimals) : string.Empty;
        HasRangeBar = true;
    }

    private void BuildStatsAndChart()
    {
        Series = [];
        Sections = [];
        HasStats = false;
        ChangeText = string.Empty;
        LowestText = HighestText = "—";
        ChartCaption = string.Empty;

        TooltipTextPaint = new SolidColorPaint(ChartTheme.Resolve("TextPrimary", "#10201C"));
        TooltipBackgroundPaint = new SolidColorPaint(ChartTheme.Resolve("SurfaceElevated", "#FFFFFF"));

        if (Latest is not { IsQualitative: 0 } latest) return;
        var def = Definition;
        var unit = latest.Unit;

        // Every point in the newest result's unit; results in a unit that cannot be converted are left out.
        var points = new List<(DateTime Date, double Value)>();
        foreach (var r in _history)
        {
            if (r.IsQualitative == 1 || r.Value is not { } v) continue;
            if (!DateTimeHelper.TryParseStored(r.CollectedOn, out var date)) continue;
            if (LabEvaluator.Convert(def, v, r.Unit, unit) is { } converted) points.Add((date, converted));
        }
        if (points.Count < 2) return;

        points = points.OrderBy(p => p.Date).ToList();
        var decimals = LabEvaluator.DecimalsFor(def, unit);
        string Fmt(double v) => LabEvaluator.Format(v, decimals);

        HasStats = true;
        LowestText = Fmt(points.Min(p => p.Value));
        HighestText = Fmt(points.Max(p => p.Value));
        var diff = points[^1].Value - points[0].Value;
        var since = points[0].Date.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        ChangeText = Math.Abs(diff) < 1e-9
            ? $"Unchanged since {since}"
            : $"{(diff > 0 ? "Up" : "Down")} {Fmt(Math.Abs(diff))} {unit} since {since}".Replace("  ", " ");

        var accent = ChartTheme.Resolve(PanelToken);
        var textColor = ChartTheme.TextSecondary;

        Series =
        [
            new LineSeries<DateTimePoint>
            {
                Name = TestName,
                Values = points.Select(p => new DateTimePoint(p.Date, p.Value)).ToList(),
                Stroke = new SolidColorPaint(accent) { StrokeThickness = 3 },
                Fill = null,
                GeometryStroke = new SolidColorPaint(accent) { StrokeThickness = 2 },
                GeometryFill = new SolidColorPaint(ChartTheme.Surface),
                GeometrySize = 10,
                LineSmoothness = 0.3,
                DataLabelsPaint = points.Count <= 8 ? new SolidColorPaint(textColor) : null,
                DataLabelsSize = 11,
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsFormatter = p => Fmt(p.Coordinate.PrimaryValue),
                XToolTipLabelFormatter = p => new DateTime((long)p.Coordinate.SecondaryValue).ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                YToolTipLabelFormatter = p => $"{Fmt(p.Coordinate.PrimaryValue)} {unit}".Trim(),
            }
        ];

        // Reference band from the newest result's own range (the one the latest value is judged against).
        var (low, high) = (latest.RefLow, latest.RefHigh);
        var minValue = points.Min(p => p.Value);
        var maxValue = points.Max(p => p.Value);
        if (low is not null || high is not null)
        {
            var band = ChartTheme.Resolve("Success", "#1E8A4F");
            Sections =
            [
                new RectangularSection
                {
                    Yi = low ?? Math.Min(0, minValue),
                    Yj = high ?? Math.Max(maxValue, low ?? 0) * 1.25,
                    Fill = new SolidColorPaint(band.WithAlpha(34)),
                }
            ];
            ChartCaption = "The shaded band is the reference range.";
        }

        // Keep both the points and the nearest range bound(s) in view.
        var axisMin = Math.Min(minValue, low ?? minValue);
        var axisMax = Math.Max(maxValue, high ?? maxValue);
        var pad = Math.Max((axisMax - axisMin) * 0.18, Math.Abs(axisMax) * 0.04 + 1e-6);

        var span = points[^1].Date - points[0].Date;
        var labelFormat = span.TotalDays > 300 ? "MMM yy" : "dd MMM";
        XAxes =
        [
            new DateTimeAxis(TimeSpan.FromDays(1), d => d.ToString(labelFormat, CultureInfo.InvariantCulture))
            {
                LabelsPaint = new SolidColorPaint(textColor),
                TextSize = 11,
                SeparatorsPaint = null,
                MinStep = TimeSpan.FromDays(Math.Max(1, span.TotalDays / 4)).Ticks,
            }
        ];
        YAxes =
        [
            new Axis
            {
                LabelsPaint = new SolidColorPaint(textColor),
                TextSize = 11,
                SeparatorsPaint = new SolidColorPaint(ChartTheme.Outline) { StrokeThickness = 1 },
                Labeler = v => LabEvaluator.Format(v),
                MinLimit = Math.Max(0, axisMin - pad),
                MaxLimit = axisMax + pad,
            }
        ];
    }
}
