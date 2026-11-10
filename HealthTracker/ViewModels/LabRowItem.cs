using System.Globalization;
using HealthTracker.Constants;
using HealthTracker.Helpers;
using HealthTracker.Models;
using HealthTracker.Services.Labs;

namespace HealthTracker.ViewModels;

/// <summary>One lab result as a list row (Labs overview, test history, Today attention list).</summary>
public class LabRowItem
{
    public LabRowItem(LabResult result, LabResult? previous = null, bool showDateAsTitle = false)
    {
        Result = result;
        Flag = LabEvaluator.Evaluate(result);
        var panel = LabCatalog.Panel(result.PanelCode);
        PanelIcon = panel.Icon;
        PanelToken = panel.ColorToken;

        var date = DateTimeHelper.TryParseStored(result.CollectedOn, out var d) ? d : (DateTime?)null;
        DateText = date?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? result.CollectedOn;
        Title = showDateAsTitle ? DateText : result.TestName;

        ValueText = LabEvaluator.ValueText(result);
        Unit = result.IsQualitative == 1 ? string.Empty : result.Unit;
        ReferenceText = LabEvaluator.ReferenceText(result);

        // A qualitative result is its own label; numbers get In range / Low / High.
        FlagText = result.IsQualitative == 1
            ? (Flag == LabFlag.Normal ? "As expected" : Flag == LabFlag.Abnormal ? "Check" : string.Empty)
            : LabEvaluator.Label(Flag);
        FlagToken = LabEvaluator.Token(Flag);
        ValueToken = LabEvaluator.NeedsAttention(Flag) ? FlagToken : "TextPrimary";

        var details = new List<string>();
        if (showDateAsTitle)
        {
            if (!string.IsNullOrWhiteSpace(result.LabName)) details.Add(result.LabName!);
        }
        else details.Add(DateText);
        if (result.IsCalculated == 1) details.Add("calculated");
        if (ReferenceText.Length > 0) details.Add(ReferenceText);
        Subtitle = string.Join(" · ", details);

        (TrendIcon, TrendText) = BuildTrend(result, previous);
    }

    public LabResult Result { get; }
    public LabFlag Flag { get; }
    public int Id => Result.Id;
    public string TestCode => Result.TestCode;
    public string Title { get; }
    public string Subtitle { get; }
    public string ValueText { get; }
    public string Unit { get; }
    public bool HasUnit => Unit.Length > 0;
    public string ReferenceText { get; }
    public string DateText { get; }
    public string FlagText { get; }
    public bool HasFlag => FlagText.Length > 0;
    public string FlagToken { get; }
    public string ValueToken { get; }
    public string PanelIcon { get; }
    public string PanelToken { get; }
    public string Notes => Result.Notes ?? string.Empty;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Result.Notes);
    public string TrendIcon { get; }
    public string TrendText { get; }
    public bool HasTrend => TrendText.Length > 0;

    public string AccessibilityText =>
        $"{Result.TestName}, {LabEvaluator.ValueWithUnit(Result)}{(HasFlag ? ", " + FlagText : "")}, {DateText}";

    private static (string Icon, string Text) BuildTrend(LabResult latest, LabResult? previous)
    {
        if (previous is null) return ("ic_trending_flat.png", string.Empty);

        if (latest.IsQualitative == 1)
        {
            var same = LabCatalog.Normalize(latest.ValueText) == LabCatalog.Normalize(previous.ValueText);
            return ("ic_trending_flat.png", same ? "Same as last time" : $"Was {previous.ValueText}");
        }

        if (latest.Value is not { } now || previous.Value is not { } before) return ("ic_trending_flat.png", string.Empty);
        var def = LabCatalog.Find(latest.TestCode);
        // Compare in the newest result's unit; skip the trend when the older unit cannot be converted.
        var beforeConverted = LabEvaluator.Convert(def, before, previous.Unit, latest.Unit);
        if (beforeConverted is not { } b) return ("ic_trending_flat.png", string.Empty);

        var diff = now - b;
        var tolerance = Math.Max(Math.Abs(b) * 0.005, 1e-9);
        if (Math.Abs(diff) <= tolerance) return ("ic_trending_flat.png", "No change");
        var text = LabEvaluator.Format(Math.Abs(diff), LabEvaluator.DecimalsFor(def, latest.Unit));
        return diff > 0 ? ("ic_trending_up.png", $"Up {text}") : ("ic_trending_down.png", $"Down {text}");
    }
}

/// <summary>A group in the Labs overview: a panel (By panel) or one sitting (By date).</summary>
public class LabGroup : List<LabRowItem>
{
    public LabGroup(string title, string subtitle, string icon, string colorToken, IEnumerable<LabRowItem> items,
        string? batchId = null) : base(items)
    {
        Title = title;
        Subtitle = subtitle;
        Icon = icon;
        ColorToken = colorToken;
        BatchId = batchId;
        AttentionCount = this.Count(i => LabEvaluator.NeedsAttention(i.Flag));
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool HasSubtitle => Subtitle.Length > 0;
    public string Icon { get; }
    public string ColorToken { get; }
    /// <summary>Set in By-date mode: lets the header offer "Delete this set".</summary>
    public string? BatchId { get; }
    public bool IsBatch => BatchId is not null;
    public int AttentionCount { get; }
    public bool HasAttention => AttentionCount > 0;
    public string AttentionText => AttentionCount == 1 ? "1 to check" : $"{AttentionCount} to check";
}
