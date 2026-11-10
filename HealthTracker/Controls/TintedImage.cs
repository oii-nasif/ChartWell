namespace HealthTracker.Controls;

/// <summary>
/// Image whose monochrome source (our Material SVG icons are plain black) is recoloured with
/// <see cref="TintColor"/>. Being a real element in the visual tree, TintColor resolves
/// DynamicResource tokens and follows theme swaps, unlike a Behavior. The platform tint is applied
/// by the handler mapping registered in MauiProgram (Android: ImageView tint list).
/// </summary>
public class TintedImage : Image
{
    public static readonly BindableProperty TintColorProperty =
        BindableProperty.Create(nameof(TintColor), typeof(Color), typeof(TintedImage), null);

    public Color? TintColor
    {
        get => (Color?)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }
}

/// <summary>ImageButton counterpart of <see cref="TintedImage"/>; used for FABs and icon buttons.</summary>
public class TintedImageButton : ImageButton
{
    public static readonly BindableProperty TintColorProperty =
        BindableProperty.Create(nameof(TintColor), typeof(Color), typeof(TintedImageButton), null);

    public Color? TintColor
    {
        get => (Color?)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }
}

public static class TintedImageHandlers
{
    /// <summary>Hooks the Image/ImageButton handlers so TintColor reaches the platform view, and removes the native underline from inputs.</summary>
    public static void Register()
    {
#if ANDROID
        // Inputs sit inside an InputBorder (filled, rounded); the native underline would draw a second edge.
        static void NoUnderline(Android.Views.View view) =>
            view.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (h, _) => NoUnderline(h.PlatformView));
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("NoUnderline", (h, _) => NoUnderline(h.PlatformView));
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("NoUnderline", (h, _) => NoUnderline(h.PlatformView));
        Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping("NoUnderline", (h, _) => NoUnderline(h.PlatformView));
        Microsoft.Maui.Handlers.TimePickerHandler.Mapper.AppendToMapping("NoUnderline", (h, _) => NoUnderline(h.PlatformView));
        Microsoft.Maui.Handlers.SearchBarHandler.Mapper.AppendToMapping("NoUnderline", (h, _) =>
        {
            // The underline belongs to the inner search plate, not the SearchView itself.
            var plateId = h.PlatformView.Context?.Resources?.GetIdentifier("search_plate", "id", h.PlatformView.Context.PackageName) ?? 0;
            if (plateId != 0 && h.PlatformView.FindViewById(plateId) is { } plate) NoUnderline(plate);
        });

        Microsoft.Maui.Handlers.ImageHandler.Mapper.AppendToMapping(nameof(TintedImage.TintColor), (handler, view) =>
        {
            if (view is TintedImage tinted) ApplyTint(handler.PlatformView, tinted.TintColor);
        });
        // The tint list lives on the ImageView, so it survives source changes; re-apply anyway in case
        // the handler recreated the drawable with its own state.
        Microsoft.Maui.Handlers.ImageHandler.Mapper.AppendToMapping(nameof(Image.Source), (handler, view) =>
        {
            if (view is TintedImage tinted) ApplyTint(handler.PlatformView, tinted.TintColor);
        });
        Microsoft.Maui.Handlers.ImageButtonHandler.Mapper.AppendToMapping(nameof(TintedImageButton.TintColor), (handler, view) =>
        {
            if (view is TintedImageButton tinted) ApplyTint(handler.PlatformView, tinted.TintColor);
        });
        Microsoft.Maui.Handlers.ImageButtonHandler.Mapper.AppendToMapping(nameof(ImageButton.Source), (handler, view) =>
        {
            if (view is TintedImageButton tinted) ApplyTint(handler.PlatformView, tinted.TintColor);
        });
#endif
    }

#if ANDROID
    private static void ApplyTint(Android.Widget.ImageView? imageView, Color? color)
    {
        if (imageView is null) return;
        if (color is null)
        {
            AndroidX.Core.Widget.ImageViewCompat.SetImageTintList(imageView, null);
            return;
        }
        var list = Android.Content.Res.ColorStateList.ValueOf(Microsoft.Maui.Platform.ColorExtensions.ToPlatform(color));
        AndroidX.Core.Widget.ImageViewCompat.SetImageTintList(imageView, list);
    }
#endif
}
