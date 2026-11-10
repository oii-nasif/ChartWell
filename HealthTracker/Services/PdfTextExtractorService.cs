using System.Text;
using HealthTracker.Helpers;
using UglyToad.PdfPig;

namespace HealthTracker.Services;

/// <summary>
/// Text extraction from stored PDF reports via PdfPig.
/// Pages are capped (default first 10) and so is the total character count, because the text feeds
/// AI prompts. A page that PdfPig cannot read is skipped rather than failing the whole document.
/// Stored paths may be relative to the app data folder; they are resolved through
/// <see cref="FileStorageHelper.ResolvePath"/> before opening.
/// </summary>
public class PdfTextExtractorService
{
    public const int DefaultMaxPages = 10;
    public const int DefaultMaxChars = 20_000;

    public string ExtractText(string filePath) => ExtractText(filePath, DefaultMaxPages, DefaultMaxChars);

    public string ExtractText(string filePath, int maxPages, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return string.Empty;

        string fullPath;
        try { fullPath = FileStorageHelper.ResolvePath(filePath); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PDF path resolve failed for '{filePath}': {ex.Message}");
            return string.Empty;
        }
        if (!File.Exists(fullPath)) return string.Empty;

        try
        {
            using var pdf = PdfDocument.Open(fullPath);
            var sb = new StringBuilder();
            var pageCount = Math.Min(pdf.NumberOfPages, Math.Max(1, maxPages));

            for (var i = 1; i <= pageCount; i++)
            {
                string pageText;
                try
                {
                    pageText = pdf.GetPage(i).Text;
                }
                catch (Exception ex)
                {
                    // Malformed content streams / unsupported fonts on one page should not lose the rest.
                    System.Diagnostics.Debug.WriteLine($"PDF page {i} extraction failed: {ex.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(pageText)) continue;

                if (sb.Length + pageText.Length > maxChars)
                {
                    var room = maxChars - sb.Length;
                    if (room > 0) sb.Append(pageText, 0, room);
                    sb.AppendLine().Append("[truncated]");
                    break;
                }

                sb.AppendLine(pageText);
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PDF text extraction failed: {ex.Message}");
            return string.Empty;
        }
    }
}
