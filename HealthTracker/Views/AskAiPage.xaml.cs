using CommunityToolkit.Maui.Alerts;
using HealthTracker.Helpers;
using HealthTracker.ViewModels;

namespace HealthTracker.Views;

public partial class AskAiPage : ContentPage
{
    private readonly AskAiViewModel _vm;
    private CancellationTokenSource? _typingCts;

    public AskAiPage(AskAiViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        vm.OpenSettingsRequested += OnOpenSettingsRequested;
        vm.MessageAppended += OnMessageAppended;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AskAiViewModel.IsBusy)) ToggleTypingAnimation(_vm.IsBusy);
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ScrollToEnd(animate: false);
        if (_vm.IsBusy) ToggleTypingAnimation(true);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ToggleTypingAnimation(false);
        // Only when the page was actually popped (not merely covered by Settings): drop the in-flight request.
        if (!Navigation.NavigationStack.Contains(this)) _vm.CancelPending();
    }

    private void OnMessageAppended(object? sender, ChatMessage message)
    {
        // Give the CollectionView a frame to realise the new row before scrolling.
        Dispatcher.Dispatch(() => ScrollToEnd(animate: true));
    }

    private void ScrollToEnd(bool animate)
    {
        try
        {
            if (_vm.Messages.Count > 0)
                MessagesView.ScrollTo(_vm.Messages[^1], position: ScrollToPosition.End, animate: animate);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScrollTo failed: {ex}");
        }
    }

    private async void OnOpenSettingsRequested(object? sender, EventArgs e)
    {
        await NavigationHelper.PushAsync<SettingsPage>(Navigation);
    }

    /// <summary>Labels aren't selectable on Android, so a double tap copies the bubble text.</summary>
    private async void OnBubbleDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (e.Parameter is not string text || string.IsNullOrWhiteSpace(text)) return;
            await Clipboard.Default.SetTextAsync(text);
            await Toast.Make("Copied to clipboard").Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Copy failed: {ex}");
        }
    }

    private void ToggleTypingAnimation(bool on)
    {
        _typingCts?.Cancel();
        _typingCts = null;
        if (!on) return;
        _typingCts = new CancellationTokenSource();
        _ = RunTypingAnimation(_typingCts.Token);
    }

    private async Task RunTypingAnimation(CancellationToken ct)
    {
        var dots = new[] { Dot1, Dot2, Dot3 };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var dot in dots)
                {
                    if (ct.IsCancellationRequested) break;
                    await dot.FadeTo(0.25, 180);
                    await dot.FadeTo(1.0, 180);
                }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Typing animation failed: {ex}"); }
        finally
        {
            foreach (var dot in dots) dot.Opacity = 1;
        }
    }
}
