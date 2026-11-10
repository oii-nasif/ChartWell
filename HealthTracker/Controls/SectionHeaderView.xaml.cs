using System.Windows.Input;

namespace HealthTracker.Controls;

/// <summary>Uppercase section title with an optional trailing text action ("See all", "Edit").</summary>
public partial class SectionHeaderView : ContentView
{
    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(SectionHeaderView), string.Empty);

    public static readonly BindableProperty ActionTextProperty =
        BindableProperty.Create(nameof(ActionText), typeof(string), typeof(SectionHeaderView), string.Empty);

    public static readonly BindableProperty ActionCommandProperty =
        BindableProperty.Create(nameof(ActionCommand), typeof(ICommand), typeof(SectionHeaderView));

    public SectionHeaderView()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }
}
