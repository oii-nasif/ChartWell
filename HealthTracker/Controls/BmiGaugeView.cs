namespace HealthTracker.Controls;

/// <summary>
/// Half-circle BMI meter: four bands (underweight, healthy, overweight, obese) on a 15–40 scale with
/// a marker at <see cref="Bmi"/>. Colours come from the active theme's status tokens at draw time, so
/// call <see cref="GraphicsView.Invalidate"/> after a theme swap. The number and category are laid
/// over it by the page; the control draws only the arc, the band boundaries and the marker.
/// </summary>
public class BmiGaugeView : GraphicsView, IDrawable
{
    public const double ScaleMin = 15;
    public const double ScaleMax = 40;

    /// <summary>Band edges in BMI units with the token that colours each band.</summary>
    private static readonly (double From, double To, string Token)[] Bands =
    [
        (ScaleMin, 18.5, "Info"),
        (18.5, 25, "Success"),
        (25, 30, "Warning"),
        (30, ScaleMax, "Danger"),
    ];

    public static readonly BindableProperty BmiProperty = BindableProperty.Create(
        nameof(Bmi), typeof(double), typeof(BmiGaugeView), 0d,
        propertyChanged: (bindable, _, _) => ((BmiGaugeView)bindable).Invalidate());

    /// <summary>The value to mark; 0 (or less) draws the bands without a marker.</summary>
    public double Bmi
    {
        get => (double)GetValue(BmiProperty);
        set => SetValue(BmiProperty, value);
    }

    public BmiGaugeView()
    {
        Drawable = this;
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        const float stroke = 16;
        const float labelRoom = 22;   // space outside the arc for the boundary numbers
        const float gapDegrees = 2.5f;

        var radius = Math.Min(dirtyRect.Width / 2 - labelRoom - stroke / 2, dirtyRect.Height - labelRoom - stroke);
        if (radius <= 8) return;
        var cx = dirtyRect.Center.X;
        var cy = dirtyRect.Bottom - stroke / 2 - 2;

        // Angles: 180° is the left end of the arc (ScaleMin), 0° the right end (ScaleMax).
        static float AngleOf(double bmi)
        {
            var f = Math.Clamp((bmi - ScaleMin) / (ScaleMax - ScaleMin), 0, 1);
            return (float)(180 - 180 * f);
        }

        canvas.StrokeSize = stroke;
        canvas.StrokeLineCap = LineCap.Butt;
        for (var i = 0; i < Bands.Length; i++)
        {
            var (from, to, token) = Bands[i];
            var start = AngleOf(from) - (i == 0 ? 0 : gapDegrees / 2);
            var end = AngleOf(to) + (i == Bands.Length - 1 ? 0 : gapDegrees / 2);
            canvas.StrokeColor = Resolve(token, Colors.Gray);
            canvas.DrawArc(cx - radius, cy - radius, radius * 2, radius * 2, start, end, clockwise: true, closed: false);
        }

        // Boundary values just outside the arc.
        canvas.FontSize = 10;
        canvas.FontColor = Resolve("TextTertiary", Colors.Gray);
        foreach (var edge in new[] { 18.5, 25, 30 })
        {
            var a = AngleOf(edge) * Math.PI / 180;
            var r = radius + stroke / 2 + 11;
            var x = cx + (float)(r * Math.Cos(a));
            var y = cy - (float)(r * Math.Sin(a));
            canvas.DrawString(edge.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                x - 16, y - 7, 32, 14, HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        if (Bmi <= 0) return;

        var angle = AngleOf(Bmi) * Math.PI / 180;
        var mx = cx + (float)(radius * Math.Cos(angle));
        var my = cy - (float)(radius * Math.Sin(angle));
        canvas.FillColor = Resolve("Surface", Colors.White);
        canvas.FillCircle(mx, my, 12);
        canvas.StrokeSize = 4;
        canvas.StrokeColor = Resolve("TextPrimary", Colors.Black);
        canvas.DrawCircle(mx, my, 10);
    }

    private static Color Resolve(string token, Color fallback)
    {
        var resources = Application.Current?.Resources;
        return resources is not null && resources.TryGetValue(token, out var value) && value is Color c ? c : fallback;
    }
}
