using SkiaSharp;

namespace HealthTracker.Services.Pdf;

/// <summary>
/// Loads image files (and, on Android, renders PDF pages) into downscaled JPEG-backed
/// <see cref="SKImage"/>s suitable for embedding. Skia's PDF backend re-uses the encoded JPEG
/// bytes of an image created from encoded data, so re-encoding here keeps the output small.
/// </summary>
public static class PdfImageHelper
{
    public const int MaxLongSidePixels = 1400;
    public const int JpegQuality = 80;

    private static readonly SKSamplingOptions ResizeSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Decodes an image file, applies EXIF orientation, downsizes and re-encodes as JPEG.</summary>
    public static SKImage? LoadImageFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec == null) return null;

        var info = codec.Info;
        var longSide = Math.Max(info.Width, info.Height);
        if (longSide <= 0) return null;

        // Let the codec decode at a reduced size when it can (cheap for JPEG), then resize exactly.
        var desiredScale = Math.Min(1f, (float)MaxLongSidePixels / longSide);
        var scaledSize = codec.GetScaledDimensions(desiredScale);
        var decodeInfo = new SKImageInfo(scaledSize.Width, scaledSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

        var bitmap = SKBitmap.Decode(codec, decodeInfo) ?? SKBitmap.Decode(codec);
        if (bitmap == null) return null;

        try
        {
            bitmap = Replace(bitmap, ApplyOrigin(bitmap, codec.EncodedOrigin));
            bitmap = Replace(bitmap, Downscale(bitmap));
            return EncodeToImage(bitmap);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Disposes <paramref name="old"/> when a transform produced a new bitmap.</summary>
    private static SKBitmap Replace(SKBitmap old, SKBitmap replacement)
    {
        if (!ReferenceEquals(old, replacement)) old.Dispose();
        return replacement;
    }

    /// <summary>Downsizes a bitmap so its long side is at most <see cref="MaxLongSidePixels"/>.</summary>
    public static SKBitmap Downscale(SKBitmap bitmap)
    {
        var longSide = Math.Max(bitmap.Width, bitmap.Height);
        if (longSide <= MaxLongSidePixels) return bitmap;

        var scale = (float)MaxLongSidePixels / longSide;
        var target = new SKImageInfo(
            Math.Max(1, (int)Math.Round(bitmap.Width * scale)),
            Math.Max(1, (int)Math.Round(bitmap.Height * scale)),
            bitmap.ColorType, bitmap.AlphaType);
        return bitmap.Resize(target, ResizeSampling) ?? bitmap;
    }

    /// <summary>Flattens onto white and encodes as JPEG, returning an image backed by the encoded bytes.</summary>
    public static SKImage? EncodeToImage(SKBitmap bitmap)
    {
        using var flattened = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flattened))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        using var data = flattened.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data == null ? null : SKImage.FromEncodedData(data);
    }

    /// <summary>Creates an image from already-encoded bytes (e.g. a JPEG produced by the platform).</summary>
    public static SKImage? FromEncodedBytes(byte[] bytes) =>
        bytes.Length == 0 ? null : SKImage.FromEncodedData(bytes);

    private static SKBitmap ApplyOrigin(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return bitmap;

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? bitmap.Height : bitmap.Width;
        var h = swap ? bitmap.Width : bitmap.Height;

        var rotated = new SKBitmap(new SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType));
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.Transparent);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:      // mirror horizontally
                canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight:   // 180°
                canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft:    // mirror vertically
                canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop:       // transpose
                canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop:      // 90° clockwise
                canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom:   // transverse
                canvas.Translate(w, h); canvas.RotateDegrees(-90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftBottom:    // 90° counter-clockwise
                canvas.Translate(0, h); canvas.RotateDegrees(-90); break;
        }

        canvas.DrawBitmap(bitmap, 0, 0);
        return rotated;
    }

    /// <summary>
    /// Renders every page of a PDF file to downscaled JPEG bytes. Returns an empty list when
    /// rendering is unavailable or fails. Uses the platform renderer on Android.
    /// </summary>
    public static List<byte[]> RenderPdfPages(string pdfPath)
    {
        var result = new List<byte[]>();
#if ANDROID
        try
        {
            using var fd = Android.OS.ParcelFileDescriptor.Open(
                new Java.IO.File(pdfPath),
                Android.OS.ParcelFileMode.ReadOnly);
            if (fd is null) return result;
            using var renderer = new Android.Graphics.Pdf.PdfRenderer(fd);

            for (int pageIndex = 0; pageIndex < renderer.PageCount; pageIndex++)
            {
                using var page = renderer.OpenPage(pageIndex);
                // Page.Width/Height are in points (1/72"); render at up to 2x but never beyond the embed cap.
                var longSide = Math.Max(page.Width, page.Height);
                var scale = longSide > 0 ? Math.Min(2f, (float)MaxLongSidePixels / longSide) : 1f;
                int width = Math.Max(1, (int)(page.Width * scale));
                int height = Math.Max(1, (int)(page.Height * scale));

                using var bitmap = Android.Graphics.Bitmap.CreateBitmap(width, height,
                    Android.Graphics.Bitmap.Config.Argb8888!);
                bitmap.EraseColor(Android.Graphics.Color.White);
                page.Render(bitmap, null, null, Android.Graphics.Pdf.PdfRenderMode.ForDisplay);

                using var ms = new MemoryStream();
                bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, JpegQuality, ms);
                result.Add(ms.ToArray());
                bitmap.Recycle();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PDF page render failed: {ex.Message}");
        }
#endif
        return result;
    }
}
