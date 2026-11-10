using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Repositories;
using HealthTracker.Services;
using HealthTracker.Services.Labs;

namespace HealthTracker.ViewModels;

/// <summary>
/// One row per vital (latest value, status against general adult ranges, sparkline), a "last 7 days"
/// strip showing which days have readings, a BMI meter (latest weight + profile height) and a soft,
/// non-diagnostic advisory when the latest blood pressure or SpO2 sits outside a healthy-ish band.
/// </summary>
public class MetricsDashboardViewModel : BaseViewModel
{
    private const int LookbackReadings = 120;
    private readonly MetricsService _service;
    private readonly UserProfileRepository _profiles;
    private bool _isRefreshing;

    public ObservableCollection<MetricSummaryItem> Summaries { get; } = new();

    /// <summary>Seven entries, oldest day first, ending today.</summary>
    public ObservableCollection<WeekDayDot> WeekDays { get; } = new();

    public ICommand LoadCommand { get; }
    public ICommand RefreshCommand { get; }

    public MetricsDashboardViewModel(MetricsService service, UserProfileRepository profiles)
    {
        _service = service;
        _profiles = profiles;
        LoadCommand = new Command(async () => await Load());
        RefreshCommand = new Command(async () =>
        {
            try { await Load(); }
            finally { IsRefreshing = false; }
        });
    }

    /// <summary>Bound two-way to RefreshView.IsRefreshing; separate from IsBusy so Load's guard still works.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set { _isRefreshing = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowLoading)); }
    }

    /// <summary>Centre spinner only for the initial load, not while the pull-to-refresh indicator is showing.</summary>
    public bool ShowLoading => IsBusy && !IsRefreshing;

    // ---- Week summary strip ----
    public int WeekCount { get; private set; }
    public int WeekTypesCount { get; private set; }
    public string WeekSummaryText { get; private set; } = "No readings this week";
    public string WeekDetailText { get; private set; } = "Tap a tile to see its history, or add a reading.";

    // ---- Advisory ----
    public bool ShowAdvisory { get; private set; }
    public string AdvisoryText { get; private set; } = string.Empty;

    public bool HasAnyData { get; private set; }

    // ---- BMI meter ----
    /// <summary>0 until both a weight reading and a profile height exist.</summary>
    public double Bmi { get; private set; }
    public bool HasBmi => Bmi > 0;
    public bool HasNoBmi => !HasBmi;
    public string BmiText { get; private set; } = "—";
    public string BmiCategory { get; private set; } = string.Empty;
    /// <summary>Status token of the category: Info (under), Success (healthy), Warning (over), Danger (obese).</summary>
    public string BmiToken { get; private set; } = "TextSecondary";
    /// <summary>"72 kg · 170 cm"</summary>
    public string BmiInputsText { get; private set; } = string.Empty;
    /// <summary>"Healthy weight for your height: 53.5 – 72 kg"</summary>
    public string HealthyWeightText { get; private set; } = string.Empty;
    /// <summary>What is missing, when the meter cannot be shown yet.</summary>
    public string BmiPromptText { get; private set; } = string.Empty;
    public string BmiActionText { get; private set; } = string.Empty;
    /// <summary>True when the missing piece is the height (set in the profile) rather than a weight reading.</summary>
    public bool BmiNeedsHeight { get; private set; }

    public async Task Load()
    {
        if (IsBusy) return;
        IsBusy = true; ClearError();
        OnPropertyChanged(nameof(ShowLoading));
        try
        {
            var now = DateTime.Now;
            var weekCutoff = now.Date.AddDays(-6);
            var items = new List<MetricSummaryItem>();
            int weekCount = 0, weekTypes = 0;
            HealthMetric? latestBp = null, latestSpO2 = null;
            var loggedDays = new HashSet<DateTime>();

            foreach (var type in MetricTypes.All)
            {
                var history = await _service.GetHistoryByType(type, LookbackReadings);
                var parsed = history
                    .Select(m => (Metric: m, Date: DateTimeHelper.TryParseStored(m.RecordedAt, out var d) ? d : DateTime.MinValue))
                    .OrderByDescending(x => x.Date)
                    .ToList();

                var latest = parsed.Count >= 1 ? parsed[0] : default;
                var prev = parsed.Count >= 2 ? parsed[1] : default;
                var inWeek = parsed.Count(x => x.Date >= weekCutoff);
                foreach (var x in parsed.Where(x => x.Date >= weekCutoff)) loggedDays.Add(x.Date.Date);
                weekCount += inWeek;
                if (inWeek > 0) weekTypes++;

                if (type == MetricTypes.BloodPressure) latestBp = latest.Metric;
                if (type == MetricTypes.SpO2) latestSpO2 = latest.Metric;

                var item = BuildTile(type, latest, prev, inWeek, now);
                item.Spark = parsed.Take(12).Reverse().Select(x => x.Metric.Value).ToArray();
                (item.StatusText, item.StatusToken) = Classify(latest.Metric);
                items.Add(item);
            }

            Summaries.Clear();
            foreach (var item in items) Summaries.Add(item);

            WeekDays.Clear();
            for (var i = 0; i < 7; i++)
            {
                var day = weekCutoff.AddDays(i);
                WeekDays.Add(new WeekDayDot(day.ToString("ddd", CultureInfo.InvariantCulture)[..1],
                    loggedDays.Contains(day), day == now.Date));
            }

            HasAnyData = items.Any(i => i.HasData);
            WeekCount = weekCount;
            WeekTypesCount = weekTypes;
            WeekSummaryText = weekCount switch
            {
                0 => "No readings this week",
                1 => "1 reading this week",
                _ => $"{weekCount} readings this week"
            };
            WeekDetailText = weekCount == 0
                ? (HasAnyData ? "Keep your trends current by logging a reading." : "Tap a vital below to add your first reading.")
                : $"Across {weekTypes} {(weekTypes == 1 ? "vital" : "vitals")} · last 7 days";

            BuildAdvisory(latestBp, latestSpO2);
            await BuildBmi();
        }
        catch (Exception ex)
        {
            SetError("Couldn't load your vitals. Pull down to try again.");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ShowLoading));
            RaiseSummaryChanged();
        }
    }

    private static MetricSummaryItem BuildTile(string type,
        (HealthMetric Metric, DateTime Date) latest,
        (HealthMetric Metric, DateTime Date) prev,
        int weekCount, DateTime now)
    {
        var item = new MetricSummaryItem
        {
            Type = type,
            Icon = ChartTheme.MetricIcon(type),
            DisplayName = GetDisplayName(type),
            Unit = MetricTypes.GetUnit(type),
            AccentToken = ChartTheme.MetricToken(type),
            WeekCount = weekCount,
            HasData = latest.Metric is not null
        };

        if (latest.Metric is null)
        {
            item.LatestValueText = "—";
            return item;
        }

        item.LatestValueText = FormatValue(latest.Metric);
        item.LastRecordedText = latest.Date == DateTime.MinValue
            ? string.Empty
            : MetricTimeFormat.RelativeTo(latest.Date, now);

        if (prev.Metric is null)
        {
            item.TrendText = "First reading";
            item.TrendIcon = "ic_trending_flat.png";
            item.TrendToken = "TextSecondary";
            return item;
        }

        var diff = latest.Metric.Value - prev.Metric.Value;
        if (Math.Abs(diff) < 0.05)
        {
            item.TrendText = "Stable";
            item.TrendIcon = "ic_trending_flat.png";
            item.TrendToken = "TextSecondary";
        }
        else
        {
            var sign = diff > 0 ? "+" : "";
            item.TrendText = type == MetricTypes.BloodPressure
                ? $"{sign}{diff:0} systolic"
                : $"{sign}{diff.ToString("0.#", CultureInfo.InvariantCulture)} {item.Unit}";
            item.TrendIcon = diff > 0 ? "ic_trending_up.png" : "ic_trending_down.png";
            item.TrendToken = item.AccentToken;
        }
        return item;
    }

    /// <summary>
    /// Where the latest reading sits against general resting-adult ranges. Deliberately coarse, and silent
    /// for weight (BMI covers it) and blood sugar (the right range depends on fasting or after a meal).
    /// </summary>
    public static (string Text, string Token) Classify(HealthMetric? m)
    {
        if (m is null) return (string.Empty, "TextSecondary");
        var v = m.Value;
        switch (m.MetricType)
        {
            case MetricTypes.BloodPressure:
                var dia = m.SecondValue ?? 0;
                if (v >= 140 || dia >= 90) return ("High", "Danger");
                if (v < 90 || (m.SecondValue.HasValue && dia < 60)) return ("Low", "Info");
                if (v >= 120 || dia >= 80) return ("Elevated", "Warning");
                return ("Normal", "Success");
            case MetricTypes.HeartRate:
                if (v > 100) return ("High", "Warning");
                if (v < 60) return ("Low", "Info");
                return ("Normal", "Success");
            case MetricTypes.SpO2:
                if (v < 90) return ("Very low", "Danger");
                if (v < 95) return ("Low", "Warning");
                return ("Normal", "Success");
            case MetricTypes.Temperature:
                if (v >= 38) return ("Fever", "Danger");
                if (v > 37.2) return ("Raised", "Warning");
                if (v < 36.1) return ("Low", "Info");
                return ("Normal", "Success");
            default:
                return (string.Empty, "TextSecondary");
        }
    }

    private void BuildAdvisory(HealthMetric? bp, HealthMetric? spo2)
    {
        var notes = new List<string>();

        if (bp is not null)
        {
            var sys = bp.Value;
            var dia = bp.SecondValue ?? 0;
            if (sys >= 140 || dia >= 90)
                notes.Add($"your latest blood pressure ({sys:0}/{dia:0}) is on the higher side");
            else if (sys < 90 || (bp.SecondValue.HasValue && dia < 60))
                notes.Add($"your latest blood pressure ({sys:0}/{dia:0}) is on the lower side");
        }

        if (spo2 is not null && spo2.Value < 94)
            notes.Add($"your latest SpO2 ({spo2.Value:0}%) is a little below the usual range");

        ShowAdvisory = notes.Count > 0;
        AdvisoryText = notes.Count switch
        {
            0 => string.Empty,
            1 => Capitalize(notes[0]) + ". It may be worth mentioning at your next visit.",
            _ => Capitalize(notes[0]) + ", and " + notes[1] + ". It may be worth mentioning at your next visit."
        };
    }

    private async Task BuildBmi()
    {
        var inv = CultureInfo.InvariantCulture;
        var height = (await _profiles.GetAsync()).HeightCm;
        var weight = (await _service.GetLatestByType(MetricTypes.Weight))?.Value;
        var bmi = LabCalculator.Bmi(weight, height);

        BmiNeedsHeight = height is null;
        if (bmi is not { } value)
        {
            Bmi = 0;
            BmiText = "—";
            BmiCategory = string.Empty;
            BmiToken = "TextSecondary";
            BmiInputsText = HealthyWeightText = string.Empty;
            BmiPromptText = (height, weight) switch
            {
                (null, null) => "Add your height and a weight reading to see your body mass index.",
                (null, _) => "Add your height in your profile to see your body mass index.",
                _ => "Record your weight to see your body mass index."
            };
            BmiActionText = height is null ? "Add height" : "Add weight";
            return;
        }

        Bmi = value;
        BmiText = value.ToString("0.0", inv);
        BmiCategory = LabCalculator.BmiCategory(value);
        BmiToken = value switch { < 18.5 => "Info", < 25 => "Success", < 30 => "Warning", _ => "Danger" };
        BmiInputsText = $"{weight!.Value.ToString("0.#", inv)} kg · {height!.Value.ToString("0.#", inv)} cm";

        var metres = height.Value / 100;
        var low = 18.5 * metres * metres;
        var high = 24.9 * metres * metres;
        HealthyWeightText = $"Healthy weight for your height: {low.ToString("0.#", inv)} – {high.ToString("0.#", inv)} kg";
        BmiPromptText = BmiActionText = string.Empty;
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(HasAnyData));
        OnPropertyChanged(nameof(WeekCount));
        OnPropertyChanged(nameof(WeekTypesCount));
        OnPropertyChanged(nameof(WeekSummaryText));
        OnPropertyChanged(nameof(WeekDetailText));
        OnPropertyChanged(nameof(ShowAdvisory));
        OnPropertyChanged(nameof(AdvisoryText));
        OnPropertyChanged(nameof(Bmi));
        OnPropertyChanged(nameof(HasBmi));
        OnPropertyChanged(nameof(HasNoBmi));
        OnPropertyChanged(nameof(BmiText));
        OnPropertyChanged(nameof(BmiCategory));
        OnPropertyChanged(nameof(BmiToken));
        OnPropertyChanged(nameof(BmiInputsText));
        OnPropertyChanged(nameof(HealthyWeightText));
        OnPropertyChanged(nameof(BmiPromptText));
        OnPropertyChanged(nameof(BmiActionText));
        OnPropertyChanged(nameof(BmiNeedsHeight));
    }

    public static string FormatValue(HealthMetric m) =>
        m.MetricType == MetricTypes.BloodPressure
            ? $"{m.Value:0}/{m.SecondValue:0}"
            : m.Value.ToString("0.#", CultureInfo.InvariantCulture);

    public static string GetDisplayName(string type) => type switch
    {
        MetricTypes.BloodPressure => "Blood pressure",
        MetricTypes.HeartRate => "Heart rate",
        MetricTypes.BloodSugar => "Blood sugar",
        MetricTypes.SpO2 => "SpO2",
        _ => type
    };
}
