using HealthTracker.ViewModels;

namespace HealthTracker.Views;

/// <summary>Chooses the medicine / doctor / report row template for the single History list.</summary>
public class HistoryItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MedicineTemplate { get; set; }
    public DataTemplate? DoctorTemplate { get; set; }
    public DataTemplate? ReportTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
    {
        MedicineHistoryGroup => MedicineTemplate!,
        DoctorHistoryItem => DoctorTemplate!,
        ReportHistoryItem => ReportTemplate!,
        _ => MedicineTemplate!,
    };
}
