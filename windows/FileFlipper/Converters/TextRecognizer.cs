using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace FileFlipper.Converters;

/// <summary>Waits for Windows Runtime async calls. Converters run on a background thread, so blocking is fine.</summary>
public static class WinRT
{
    public static T Get<T>(this IAsyncOperation<T> operation) => operation.AsTask().GetAwaiter().GetResult();
    public static void Wait(this IAsyncAction action) => action.AsTask().GetAwaiter().GetResult();
    public static T Get<T, TProgress>(this IAsyncOperationWithProgress<T, TProgress> operation) => operation.AsTask().GetAwaiter().GetResult();
    public static void Wait<TProgress>(this IAsyncActionWithProgress<TProgress> action) => action.AsTask().GetAwaiter().GetResult();
}

/// <summary>Renders PDF pages with Windows' built-in PDF engine (the one Microsoft Edge uses).</summary>
public static class PdfRenderer
{
    public static WinPdf.PdfDocument Open(string path)
    {
        try
        {
            var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).Get();
            return WinPdf.PdfDocument.LoadFromFileAsync(file).Get();
        }
        catch (Exception error) when (error.HResult == unchecked((int)0x8007052B))   // wrong password
        {
            throw new ConversionException(Loc.L("%@ is password protected", Path.GetFileName(path)));
        }
        catch (Exception error) when (error is not ConversionException)
        {
            throw ConversionException.Unreadable(path);
        }
    }

    /// <summary>Renders a page at <paramref name="scale"/> × 72 dpi on white (2 → 144 dpi, like the Mac app).</summary>
    public static BitmapSource Render(WinPdf.PdfPage page, double scale = 2, uint maxDimension = 12000)
    {
        // page.Size is in DIPs (1/96 inch); PDF points are 1/72 inch.
        double width = page.Size.Width * 72 / 96 * scale;
        double height = page.Size.Height * 72 / 96 * scale;
        double shrink = Math.Min(1, maxDimension / Math.Max(width, height));
        var options = new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(width * shrink)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(height * shrink)),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };
        using var stream = new InMemoryRandomAccessStream();
        page.RenderToStreamAsync(stream, options).Wait();
        stream.Seek(0);
        using var managed = stream.AsStreamForRead();
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(managed, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}

/// <summary>
/// Reads text from PDF pages that have no text layer (scanned or photographed pages), using the
/// OCR engine built into Windows. It reads the languages installed in Windows' language settings.
/// </summary>
public static class TextRecognizer
{
    private static readonly Lazy<OcrEngine?> Engine = new(CreateEngine);

    private static OcrEngine? CreateEngine()
    {
        // Prefer Chinese when it's installed, then the user's own languages, then anything available.
        foreach (var tag in new[] { "zh-Hans-CN", "zh-CN", "zh-Hans" })
        {
            var language = new Windows.Globalization.Language(tag);
            if (OcrEngine.IsLanguageSupported(language)) return OcrEngine.TryCreateFromLanguage(language);
        }
        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null);
    }

    public static bool HasEngine => Engine.Value != null;

    /// <summary>A page counts as having text if there's more than a stray header or page number.</summary>
    public static bool HasTextLayer(string text) => text.Count(c => !char.IsWhiteSpace(c)) >= 20;

    /// <summary>OCR one page. Returns an empty string if nothing is readable.</summary>
    public static string Recognize(WinPdf.PdfPage page)
    {
        var engine = Engine.Value;
        if (engine == null) return "";
        // 3x (216 dpi) gives the engine enough detail for small print.
        var image = PdfRenderer.Render(page, 3, OcrEngine.MaxImageDimension);
        return Recognize(image);
    }

    public static string Recognize(BitmapSource image)
    {
        var engine = Engine.Value;
        if (engine == null) return "";
        var bgra = ImageConverter.ToBgra32(image);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pixels));
        OcrResult result;
        try { result = engine.RecognizeAsync(bitmap).Get(); }
        catch { return ""; }
        return string.Join("\n", result.Lines.Select(Join));
    }

    /// <summary>The engine returns words; CJK characters are separate words that must not get spaces between them.</summary>
    private static string Join(OcrLine line)
    {
        var builder = new StringBuilder();
        foreach (var word in line.Words)
        {
            var text = word.Text;
            if (builder.Length > 0 && text.Length > 0 && !(PdfFonts.IsCjk(builder[^1]) && PdfFonts.IsCjk(text[0])))
                builder.Append(' ');
            builder.Append(text);
        }
        return builder.ToString();
    }
}
