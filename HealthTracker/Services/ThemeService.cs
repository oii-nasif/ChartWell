namespace HealthTracker.Services;

/// <summary>User-selectable appearance. "System" follows the OS setting.</summary>
public enum AppThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// Swaps the semantic colour dictionary (Resources/Themes/LightTheme.xaml or DarkTheme.xaml) in
/// Application.Resources.MergedDictionaries. Everything in Styles.xaml and the pages binds colours
/// with DynamicResource, so a swap restyles the whole app instantly.
/// </summary>
public class ThemeService
{
    private const string PreferenceKey = "app_theme_mode";
    private ResourceDictionary? _current;

    public AppThemeMode Mode
    {
        get => (AppThemeMode)Preferences.Default.Get(PreferenceKey, (int)AppThemeMode.System);
        private set => Preferences.Default.Set(PreferenceKey, (int)value);
    }

    /// <summary>The theme currently on screen after resolving "System".</summary>
    public AppTheme EffectiveTheme { get; private set; } = AppTheme.Light;

    public event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>Call once from App's constructor, after InitializeComponent.</summary>
    public void Initialize(Application app)
    {
        app.RequestedThemeChanged += (_, _) =>
        {
            if (Mode == AppThemeMode.System) Apply(app);
        };
        Apply(app);
    }

    public void SetMode(AppThemeMode mode)
    {
        Mode = mode;
        if (Application.Current is { } app) Apply(app);
    }

    private void Apply(Application app)
    {
        var target = Mode switch
        {
            AppThemeMode.Light => AppTheme.Light,
            AppThemeMode.Dark => AppTheme.Dark,
            _ => app.PlatformAppTheme == AppTheme.Dark ? AppTheme.Dark : AppTheme.Light
        };

        // UserAppTheme drives the platform chrome (status bar icons, native dialogs, pickers).
        app.UserAppTheme = Mode == AppThemeMode.System ? AppTheme.Unspecified : target;

        ResourceDictionary next = target == AppTheme.Dark
            ? new Resources.Themes.DarkTheme()
            : new Resources.Themes.LightTheme();

        var merged = app.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Add(next);
        _current = next;
        EffectiveTheme = target;
        ThemeChanged?.Invoke(this, target);
    }
}
