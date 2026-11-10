using System.Windows.Input;

namespace HealthTracker.Controls;

/// <summary>
/// 3x4 numeric keypad for the lock screen: digits 0-9, a backspace key and an optional biometric key.
/// Stateless; the owner binds the three commands and decides what a key press means.
/// </summary>
public partial class PinPadView : ContentView
{
    public static readonly BindableProperty DigitCommandProperty =
        BindableProperty.Create(nameof(DigitCommand), typeof(ICommand), typeof(PinPadView));

    public static readonly BindableProperty BackspaceCommandProperty =
        BindableProperty.Create(nameof(BackspaceCommand), typeof(ICommand), typeof(PinPadView));

    public static readonly BindableProperty BiometricCommandProperty =
        BindableProperty.Create(nameof(BiometricCommand), typeof(ICommand), typeof(PinPadView));

    public static readonly BindableProperty ShowBiometricProperty =
        BindableProperty.Create(nameof(ShowBiometric), typeof(bool), typeof(PinPadView), false);

    public static readonly BindableProperty IsKeypadEnabledProperty =
        BindableProperty.Create(nameof(IsKeypadEnabled), typeof(bool), typeof(PinPadView), true);

    public PinPadView()
    {
        InitializeComponent();
    }

    /// <summary>Invoked with the pressed digit ("0".."9") as a string parameter.</summary>
    public ICommand? DigitCommand
    {
        get => (ICommand?)GetValue(DigitCommandProperty);
        set => SetValue(DigitCommandProperty, value);
    }

    public ICommand? BackspaceCommand
    {
        get => (ICommand?)GetValue(BackspaceCommandProperty);
        set => SetValue(BackspaceCommandProperty, value);
    }

    public ICommand? BiometricCommand
    {
        get => (ICommand?)GetValue(BiometricCommandProperty);
        set => SetValue(BiometricCommandProperty, value);
    }

    /// <summary>Shows the fingerprint key in the bottom-left slot.</summary>
    public bool ShowBiometric
    {
        get => (bool)GetValue(ShowBiometricProperty);
        set => SetValue(ShowBiometricProperty, value);
    }

    /// <summary>False during the failed-attempt cooldown.</summary>
    public bool IsKeypadEnabled
    {
        get => (bool)GetValue(IsKeypadEnabledProperty);
        set => SetValue(IsKeypadEnabledProperty, value);
    }
}
