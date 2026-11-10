using System.Windows.Input;

namespace HealthTracker.Controls;

/// <summary>
/// Illustrated empty state with an optional call to action. Use it as a CollectionView.EmptyView
/// or inline when a section has no data.
/// </summary>
public partial class EmptyStateView : ContentView
{
    public static readonly BindableProperty IconProperty =
        BindableProperty.Create(nameof(Icon), typeof(ImageSource), typeof(EmptyStateView), default(ImageSource));

    public static readonly BindableProperty TitleProperty =
        BindableProperty.Create(nameof(Title), typeof(string), typeof(EmptyStateView), "Nothing here yet");

    public static readonly BindableProperty MessageProperty =
        BindableProperty.Create(nameof(Message), typeof(string), typeof(EmptyStateView), string.Empty);

    public static readonly BindableProperty ActionTextProperty =
        BindableProperty.Create(nameof(ActionText), typeof(string), typeof(EmptyStateView), string.Empty);

    public static readonly BindableProperty ActionCommandProperty =
        BindableProperty.Create(nameof(ActionCommand), typeof(ICommand), typeof(EmptyStateView));

    public EmptyStateView()
    {
        InitializeComponent();
    }

    public ImageSource Icon
    {
        get => (ImageSource)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
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
