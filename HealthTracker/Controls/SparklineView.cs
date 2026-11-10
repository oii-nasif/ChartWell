namespace HealthTracker.Controls;

/// <summary>
/// Tiny trend line for a list row: the last few readings, oldest on the left, with a soft fill and a
/// dot on the newest value. No axes or labels; the number next to it carries the meaning. The colour
/// is a theme token resolved at draw time, so call <see cref="GraphicsView.Invalidate"/> after a theme swap.
/// </summary>
public class SparklineView : GraphicsView, IDrawable
{
    public static readonly BindableProperty ValuesProperty = BindableProperty.Create(
        nameof(Values), typeof(double[]), typeof(SparklineView), null,
        propertyChanged: (bindable, _, _) => ((SparklineView)bindable).Invalidate());

    public static readonly BindableProperty ColorTokenProperty = BindableProperty.Create(
        nameof(ColorToken), typeof(string), typeof(SparklineView), "Primary",
        propertyChanged: (bindable, _, _) => ((SparklineView)bindable).Invalidate());

    /// <summary>Readings in chronological order. Fewer than two draws nothing.</summary>
    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public string ColorToken
    {
        get => (string)GetValue(ColorTokenProperty);
        set => SetValue(ColorTokenProperty, value);
    }

    public SparklineView()
    {
        Drawable = this;
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var values = Values;
        if (values is null || values.Length < 2) return;

        const float pad = 5; // room for the stroke and the end dot
        var width = dirtyRect.Width - pad * 2;
        var height = dirtyRect.Height - pad * 2;
        if (width <= 0 || height <= 0) return;

        var min = values.Min();
        var max = values.Max();
        var span = max - min;

        float X(int i) => dirtyRect.Left + pad + width * i / (values.Length - 1);
        // A flat series sits on the middle line instead of hugging an edge.
        float Y(double v) => span < 1e-9
            ? dirtyRect.Top + pad + height / 2
            : dirtyRect.Top + pad + (float)(height * (1 - (v - min) / span));

        var color = Resolve(ColorToken);

        var line = new PathF();
        line.MoveTo(X(0), Y(values[0]));
        for (var i = 1; i < values.Length; i++) line.LineTo(X(i), Y(values[i]));

        var fill = new PathF(line);
        fill.LineTo(X(values.Length - 1), dirtyRect.Bottom);
        fill.LineTo(X(0), dirtyRect.Bottom);
        fill.Close();
        canvas.FillColor = color.WithAlpha(0.12f);
        canvas.FillPath(fill);

        canvas.StrokeColor = color;
        canvas.StrokeSize = 2.5f;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(line);

        var lastX = X(values.Length - 1);
        var lastY = Y(values[^1]);
        canvas.FillColor = Resolve("Surface");
        canvas.FillCircle(lastX, lastY, 4.5f);
        canvas.FillColor = color;
        canvas.FillCircle(lastX, lastY, 3);
    }

    private static Color Resolve(string token)
    {
        var resources = Application.Current?.Resources;
        return resources is not null && resources.TryGetValue(token, out var value) && value is Color c ? c : Colors.Gray;
    }
}
