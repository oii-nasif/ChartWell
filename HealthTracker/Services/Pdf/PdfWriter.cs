using SkiaSharp;

namespace HealthTracker.Services.Pdf;

/// <summary>
/// A small flow-layout writer on top of SkiaSharp's PDF backend. Pages are A4 with fixed margins;
/// content is appended top-to-bottom and a new page is started automatically when an element does
/// not fit. Every page gets a footer "Chartwell · generated {date} · page N" when it is ended.
/// Not thread-safe; intended to be used from a single background task.
/// </summary>
public sealed class PdfWriter : IDisposable
{
    public const float PageWidth = 595f;   // A4 in points
    public const float PageHeight = 842f;
    public const float Margin = 40f;
    private const float FooterHeight = 18f;

    public static readonly SKColor Primary = SKColor.Parse("#0E7C6B");
    public static readonly SKColor PrimaryLight = SKColor.Parse("#E3F4EF");
    public static readonly SKColor Text = SKColor.Parse("#10201C");
    public static readonly SKColor SecondaryText = SKColor.Parse("#4A605A");
    public static readonly SKColor RuleColor = SKColor.Parse("#DDE6E2");
    public static readonly SKColor ZebraRow = SKColor.Parse("#F4F8F6");
    public static readonly SKColor Success = SKColor.Parse("#1E8A4F");
    public static readonly SKColor Danger = SKColor.Parse("#C4392D");

    private readonly SKDocument _document;
    private readonly SKTypeface _regular;
    private readonly SKTypeface _bold;
    private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKSamplingOptions _imageSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private readonly string _footerText;

    private SKCanvas? _canvas;
    private int _pageNumber;
    private float _y;
    private bool _finished;

    public PdfWriter(Stream output, string title, DateTime generatedAt)
    {
        _document = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata
        {
            Title = title,
            Author = "Chartwell",
            Creator = "Chartwell",
            Producer = "Chartwell (SkiaSharp)",
            Creation = generatedAt,
            Modified = generatedAt
        }) ?? throw new InvalidOperationException("Could not create the PDF document.");

        (_regular, _bold) = LoadTypefaces();
        _footerText = $"Chartwell · generated {generatedAt:MMMM d, yyyy h:mm tt} · page ";
    }

    public float ContentWidth => PageWidth - 2 * Margin;
    public float ContentHeight => PageHeight - 2 * Margin - FooterHeight;
    public float RemainingHeight => _canvas == null ? ContentHeight : PageHeight - Margin - FooterHeight - _y;
    public int PageCount => _pageNumber;

    // ─── Page control ────────────────────────────────────────────────────────

    /// <summary>Starts a new page if the current one has less than <paramref name="height"/> left.</summary>
    public void EnsureSpace(float height)
    {
        if (_canvas == null || RemainingHeight < height) NewPage();
    }

    public void NewPage()
    {
        EndPage();
        _canvas = _document.BeginPage(PageWidth, PageHeight);
        _pageNumber++;
        _y = Margin;
    }

    private void EndPage()
    {
        if (_canvas == null) return;
        DrawFooter();
        _document.EndPage();
        _canvas = null;
    }

    private void DrawFooter()
    {
        if (_canvas == null) return;
        using var font = MakeFont(8, bold: false);
        _textPaint.Color = SecondaryText;
        var baseline = PageHeight - Margin / 2;
        _canvas.DrawText(_footerText + _pageNumber, PageWidth / 2, baseline, SKTextAlign.Center, font, _textPaint);
    }

    /// <summary>Ends the last page and writes the PDF to the output stream.</summary>
    public void Finish()
    {
        if (_finished) return;
        if (_canvas == null) NewPage(); // never emit an empty document
        EndPage();
        _document.Close();
        _finished = true;
    }

    // ─── Text elements ───────────────────────────────────────────────────────

    /// <summary>Large document title in the primary colour, followed by a secondary-colour line and a rule.</summary>
    public void Title(string title, string? subtitle = null)
    {
        EnsureSpace(50);
        DrawWrapped(title, 20, bold: true, Primary, Margin, ContentWidth);
        if (!string.IsNullOrEmpty(subtitle))
        {
            _y += 2;
            DrawWrapped(subtitle, 9, bold: false, SecondaryText, Margin, ContentWidth);
        }
        _y += 6;
        Rule();
        _y += 10;
    }

    /// <summary>Section heading: white bold text on a primary-colour bar.</summary>
    public void Heading(string text)
    {
        const float fontSize = 13;
        const float padding = 8;
        using var font = MakeFont(fontSize, bold: true);
        var lines = Wrap(text, font, ContentWidth - 2 * padding);
        var barHeight = lines.Count * LineHeight(font) + 2 * padding;

        // Keep the heading with at least a couple of lines of what follows.
        EnsureSpace(barHeight + 40);
        if (_y > Margin) _y += 6;

        _fillPaint.Color = Primary;
        _canvas!.DrawRect(Margin, _y, ContentWidth, barHeight, _fillPaint);
        DrawLines(lines, font, SKColors.White, Margin + padding, _y + padding);
        _y += barHeight + 8;
    }

    /// <summary>Sub-heading: primary-colour bold text on a light bar.</summary>
    public void Subheading(string text)
    {
        const float fontSize = 11;
        const float padding = 6;
        using var font = MakeFont(fontSize, bold: true);
        var lines = Wrap(text, font, ContentWidth - 2 * padding);
        var barHeight = lines.Count * LineHeight(font) + 2 * padding;

        EnsureSpace(barHeight + 30);

        _fillPaint.Color = PrimaryLight;
        _canvas!.DrawRect(Margin, _y, ContentWidth, barHeight, _fillPaint);
        DrawLines(lines, font, Primary, Margin + padding, _y + padding);
        _y += barHeight + 4;
    }

    /// <summary>Word-wrapped paragraph. Breaks across pages line by line.</summary>
    public void Paragraph(string text, float fontSize = 10, bool bold = false, SKColor? color = null, float indent = 0)
    {
        DrawWrapped(text, fontSize, bold, color ?? Text, Margin + indent, ContentWidth - indent);
        _y += 2;
    }

    /// <summary>One line with left-aligned text and a right-aligned secondary text (e.g. name + date).</summary>
    public void TextRow(string left, string right, float fontSize = 11, bool leftBold = true, SKColor? rightColor = null)
    {
        using var leftFont = MakeFont(fontSize, leftBold);
        using var rightFont = MakeFont(fontSize - 1, bold: false);
        var rightWidth = Math.Min(rightFont.MeasureText(right, _textPaint) + 8, ContentWidth * 0.4f);
        var lines = Wrap(left, leftFont, ContentWidth - rightWidth);
        var height = lines.Count * LineHeight(leftFont);
        EnsureSpace(height);

        DrawLines(lines, leftFont, Text, Margin, _y);
        _textPaint.Color = rightColor ?? SecondaryText;
        _canvas!.DrawText(right, PageWidth - Margin, _y - rightFont.Metrics.Ascent, SKTextAlign.Right, rightFont, _textPaint);
        _y += height + 2;
    }

    /// <summary>A label (secondary colour) in a fixed-width column followed by a wrapped value.</summary>
    public void KeyValue(string label, string value, float labelWidth = 120, float fontSize = 9)
    {
        using var font = MakeFont(fontSize, bold: false);
        var valueWidth = ContentWidth - labelWidth - 6;
        var lines = Wrap(value, font, valueWidth);
        var height = Math.Max(1, lines.Count) * LineHeight(font) + 3;
        EnsureSpace(height);

        _textPaint.Color = SecondaryText;
        _canvas!.DrawText(label, Margin, _y - font.Metrics.Ascent, SKTextAlign.Left, font, _textPaint);
        DrawLines(lines, font, Text, Margin + labelWidth + 6, _y);
        _y += height;
    }

    public void Rule()
    {
        EnsureSpace(4);
        _fillPaint.Color = RuleColor;
        _canvas!.DrawRect(Margin, _y, ContentWidth, 1, _fillPaint);
        _y += 3;
    }

    public void Space(float height)
    {
        if (_canvas == null) return;
        _y = Math.Min(_y + height, PageHeight - Margin - FooterHeight);
    }

    // ─── Table ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws a table with a header row and zebra-striped body rows. Cell text wraps; rows that do
    /// not fit move to a new page and the header is repeated there.
    /// </summary>
    public void Table(PdfTable table)
    {
        if (table.Columns.Count == 0) return;
        const float cellPadding = 4;
        const float fontSize = 9;
        using var font = MakeFont(fontSize, bold: false);
        using var boldFont = MakeFont(fontSize, bold: true);

        var totalWeight = table.Columns.Sum(c => c.Weight);
        var widths = table.Columns.Select(c => ContentWidth * c.Weight / totalWeight).ToArray();

        EnsureSpace(60);
        DrawTableHeader(table, widths, boldFont, cellPadding);

        for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var cellLines = new List<string>[widths.Length];
            float rowHeight = 0;
            for (int col = 0; col < widths.Length; col++)
            {
                var cell = col < row.Length ? row[col] : PdfTableCell.Empty;
                var cellFont = cell.Bold ? boldFont : font;
                cellLines[col] = Wrap(cell.Text, cellFont, widths[col] - 2 * cellPadding);
                rowHeight = Math.Max(rowHeight, Math.Max(1, cellLines[col].Count) * LineHeight(cellFont));
            }
            rowHeight += 2 * cellPadding;

            if (RemainingHeight < rowHeight)
            {
                NewPage();
                DrawTableHeader(table, widths, boldFont, cellPadding);
            }

            if (rowIndex % 2 == 1)
            {
                _fillPaint.Color = ZebraRow;
                _canvas!.DrawRect(Margin, _y, ContentWidth, rowHeight, _fillPaint);
            }

            float x = Margin;
            for (int col = 0; col < widths.Length; col++)
            {
                var cell = col < row.Length ? row[col] : PdfTableCell.Empty;
                DrawLines(cellLines[col], cell.Bold ? boldFont : font, cell.Color ?? Text, x + cellPadding, _y + cellPadding);
                x += widths[col];
            }

            _fillPaint.Color = RuleColor;
            _canvas!.DrawRect(Margin, _y + rowHeight - 1, ContentWidth, 1, _fillPaint);
            _y += rowHeight;
        }

        _y += 6;
    }

    private void DrawTableHeader(PdfTable table, float[] widths, SKFont boldFont, float cellPadding)
    {
        var lines = new List<string>[widths.Length];
        float height = 0;
        for (int col = 0; col < widths.Length; col++)
        {
            lines[col] = Wrap(table.Columns[col].Header, boldFont, widths[col] - 2 * cellPadding);
            height = Math.Max(height, Math.Max(1, lines[col].Count) * LineHeight(boldFont));
        }
        height += 2 * cellPadding;

        _fillPaint.Color = Primary;
        _canvas!.DrawRect(Margin, _y, ContentWidth, height, _fillPaint);
        float x = Margin;
        for (int col = 0; col < widths.Length; col++)
        {
            DrawLines(lines[col], boldFont, SKColors.White, x + cellPadding, _y + cellPadding);
            x += widths[col];
        }
        _y += height;
    }

    // ─── Images ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws an image aspect-fitted to the content width. If it does not fit in the remaining
    /// space a new page is started; if it is taller than a whole page it is scaled down further.
    /// </summary>
    public void Image(SKImage image, string? caption = null)
    {
        if (image.Width <= 0 || image.Height <= 0) return;

        float captionHeight = 0;
        SKFont? captionFont = null;
        List<string>? captionLines = null;
        if (!string.IsNullOrEmpty(caption))
        {
            captionFont = MakeFont(11, bold: true);
            captionLines = Wrap(caption, captionFont, ContentWidth);
            captionHeight = captionLines.Count * LineHeight(captionFont) + 4;
        }

        var scale = Math.Min(ContentWidth / image.Width, (ContentHeight - captionHeight) / image.Height);
        scale = Math.Min(scale, 1f * 72f / 96f); // never enlarge beyond ~96 dpi placement
        var drawWidth = image.Width * scale;
        var drawHeight = image.Height * scale;

        EnsureSpace(drawHeight + captionHeight);

        if (captionLines != null && captionFont != null)
        {
            DrawLines(captionLines, captionFont, Text, Margin, _y);
            _y += captionHeight;
            captionFont.Dispose();
        }

        var left = Margin + (ContentWidth - drawWidth) / 2;
        var dest = new SKRect(left, _y, left + drawWidth, _y + drawHeight);
        _canvas!.DrawImage(image, dest, _imageSampling, null);

        _fillPaint.Color = RuleColor;
        _fillPaint.Style = SKPaintStyle.Stroke;
        _canvas.DrawRect(dest, _fillPaint);
        _fillPaint.Style = SKPaintStyle.Fill;

        _y += drawHeight + 10;
    }

    // ─── Low-level drawing ───────────────────────────────────────────────────

    private void DrawWrapped(string text, float fontSize, bool bold, SKColor color, float x, float width)
    {
        using var font = MakeFont(fontSize, bold);
        var lines = Wrap(text, font, width);
        var lineHeight = LineHeight(font);
        foreach (var line in lines)
        {
            EnsureSpace(lineHeight);
            _textPaint.Color = color;
            _canvas!.DrawText(line, x, _y - font.Metrics.Ascent, SKTextAlign.Left, font, _textPaint);
            _y += lineHeight;
        }
    }

    /// <summary>Draws pre-wrapped lines at a fixed position without advancing <see cref="_y"/>.</summary>
    private void DrawLines(List<string> lines, SKFont font, SKColor color, float x, float top)
    {
        _textPaint.Color = color;
        var lineHeight = LineHeight(font);
        var baseline = top - font.Metrics.Ascent;
        foreach (var line in lines)
        {
            _canvas!.DrawText(line, x, baseline, SKTextAlign.Left, font, _textPaint);
            baseline += lineHeight;
        }
    }

    private static float LineHeight(SKFont font) => font.Spacing > 0 ? font.Spacing : font.Size * 1.25f;

    private SKFont MakeFont(float size, bool bold) => new(bold ? _bold : _regular, size)
    {
        Edging = SKFontEdging.SubpixelAntialias,
        Subpixel = true
    };

    /// <summary>Greedy word wrap honouring explicit newlines; over-long words are split by character.</summary>
    private List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(string.Empty); return result; }
        if (maxWidth <= 0) { result.Add(text); return result; }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var words = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { result.Add(string.Empty); continue; }

            var current = string.Empty;
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (font.MeasureText(candidate, _textPaint) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0) result.Add(current);
                current = string.Empty;

                if (font.MeasureText(word, _textPaint) <= maxWidth)
                {
                    current = word;
                    continue;
                }

                // Word is wider than the column: hard-break it.
                var piece = string.Empty;
                foreach (var ch in word)
                {
                    if (font.MeasureText(piece + ch, _textPaint) > maxWidth && piece.Length > 0)
                    {
                        result.Add(piece);
                        piece = string.Empty;
                    }
                    piece += ch;
                }
                current = piece;
            }
            result.Add(current);
        }
        return result;
    }

    private static (SKTypeface Regular, SKTypeface Bold) LoadTypefaces()
    {
        SKTypeface? regular = null, bold = null;
        try
        {
            regular = SKTypeface.FromFamilyName("sans-serif", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            bold = SKTypeface.FromFamilyName("sans-serif", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Font lookup failed, using default typeface: {ex.Message}");
        }
        regular ??= SKTypeface.Default;
        bold ??= regular;
        return (regular, bold);
    }

    public void Dispose()
    {
        try { Finish(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PdfWriter finish failed: {ex.Message}"); }
        _document.Dispose();
        _fillPaint.Dispose();
        _textPaint.Dispose();
        if (!ReferenceEquals(_bold, _regular)) _bold.Dispose();
        _regular.Dispose();
    }
}

/// <summary>Column definition for <see cref="PdfWriter.Table"/>; <see cref="Weight"/> is relative.</summary>
public sealed record PdfTableColumn(string Header, float Weight = 1);

/// <summary>A table cell: text plus optional bold/colour emphasis. Strings convert implicitly.</summary>
public sealed record PdfTableCell(string Text, bool Bold = false, SKColor? Color = null)
{
    public static readonly PdfTableCell Empty = new(string.Empty);
    public static implicit operator PdfTableCell(string text) => new(text ?? string.Empty);
}

public sealed class PdfTable
{
    public List<PdfTableColumn> Columns { get; } = new();
    public List<PdfTableCell[]> Rows { get; } = new();

    public PdfTable(params PdfTableColumn[] columns) => Columns.AddRange(columns);

    public void AddRow(params PdfTableCell[] cells) => Rows.Add(cells);
}
