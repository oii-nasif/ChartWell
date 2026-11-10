using System.Collections.ObjectModel;

namespace HealthTracker.ViewModels;

/// <summary>A titled section of the Today list ("Due now", "Later today", "Done") for a grouped CollectionView.</summary>
public class TodayDoseGroup : ObservableCollection<TodayScheduleItem>
{
    public TodayDoseGroup(TodayDoseGroupKind kind, string title, IEnumerable<TodayScheduleItem> items) : base(items)
    {
        Kind = kind;
        Title = title;
    }

    public TodayDoseGroupKind Kind { get; }
    public string Title { get; }
    public string CountText => Count.ToString();

    public static string TitleFor(TodayDoseGroupKind kind) => kind switch
    {
        TodayDoseGroupKind.DueNow => "Due now",
        TodayDoseGroupKind.Later => "Later today",
        _ => "Done",
    };
}
