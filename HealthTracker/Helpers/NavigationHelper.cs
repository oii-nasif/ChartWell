namespace HealthTracker.Helpers;

/// <summary>
/// Resolves pages from DI and pushes them, with a re-entrancy guard so a double tap cannot push
/// the same page twice, and a catch so an async-void click handler can never crash the app.
/// Pages pass data by calling a method on the resolved page before pushing (see docs/ARCHITECTURE.md).
/// </summary>
public static class NavigationHelper
{
    private static int _navigating;

    public static Task PushAsync<TPage>(INavigation navigation) where TPage : Page
        => PushAsync<TPage>(navigation, _ => Task.CompletedTask);

    public static Task PushAsync<TPage>(INavigation navigation, Action<TPage> configure) where TPage : Page
        => PushAsync<TPage>(navigation, p => { configure(p); return Task.CompletedTask; });

    public static async Task PushAsync<TPage>(INavigation navigation, Func<TPage, Task> configure) where TPage : Page
    {
        if (Interlocked.Exchange(ref _navigating, 1) == 1) return;
        try
        {
            var page = App.Services.GetRequiredService<TPage>();
            await configure(page);
            await navigation.PushAsync(page);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation to {typeof(TPage).Name} failed: {ex}");
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("Something went wrong", ex.Message, "OK");
        }
        finally
        {
            Interlocked.Exchange(ref _navigating, 0);
        }
    }

    public static async Task PushModalAsync<TPage>(INavigation navigation, Action<TPage>? configure = null) where TPage : Page
    {
        if (Interlocked.Exchange(ref _navigating, 1) == 1) return;
        try
        {
            var page = App.Services.GetRequiredService<TPage>();
            configure?.Invoke(page);
            await navigation.PushModalAsync(page);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Modal navigation to {typeof(TPage).Name} failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _navigating, 0);
        }
    }

    /// <summary>Switches to a Shell tab by its route ("today", "labs", "metrics", "reports", "doctors", "timeline", "me").</summary>
    public static async Task GoToTabAsync(string route)
    {
        try
        {
            if (Shell.Current is not null) await Shell.Current.GoToAsync($"//{route}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Switching to tab '{route}' failed: {ex}");
        }
    }

    /// <summary>Opens a stored file with the system viewer; shows a friendly alert when no app can open it.</summary>
    public static async Task OpenFileAsync(string storedPath, string? title = null)
    {
        try
        {
            var path = FileStorageHelper.ResolvePath(storedPath);
            if (!File.Exists(path))
            {
                if (Shell.Current is not null)
                    await Shell.Current.DisplayAlert("File missing", "This file is no longer on the device.", "OK");
                return;
            }
            await Launcher.Default.OpenAsync(new OpenFileRequest(title ?? Path.GetFileName(path), new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenFile failed: {ex}");
            if (Shell.Current is not null)
                await Shell.Current.DisplayAlert("Can't open file", "No app on this device can open this file type.", "OK");
        }
    }

    public static async Task ShareFileAsync(string storedPath, string? title = null)
    {
        try
        {
            var path = FileStorageHelper.ResolvePath(storedPath);
            if (!File.Exists(path)) return;
            await Share.Default.RequestAsync(new ShareFileRequest(title ?? Path.GetFileName(path), new ShareFile(path)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShareFile failed: {ex}");
        }
    }
}
