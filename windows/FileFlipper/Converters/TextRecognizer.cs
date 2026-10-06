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
/// Windows' engines read one language each, so when both a Chinese and a Latin-script language are
/// installed, a page is read with the Chinese engine first and re-read with the other one if it
/// turns out to contain hardly any Chinese.
/// </summary>
public static class TextRecognizer
{
    private static readonly Lazy<(OcrEngine? Cjk, OcrEngine? Latin)> Engines = new(CreateEngines);

    private static (OcrEngine?, OcrEngine?) CreateEngines()
    {
        var available = OcrEngine.AvailableRecognizerLanguages.ToList();
        bool IsCjk(Windows.Globalization.Language l) => l.LanguageTag.StartsWith("zh") || l.LanguageTag.StartsWith("ja") || l.LanguageTag.StartsWith("ko");
        var cjkLanguage = available.FirstOrDefault(l => l.LanguageTag.StartsWith("zh-Hans") || l.LanguageTag == "zh-CN")
            ?? available.FirstOrDefault(IsCjk);
        OcrEngine? latin = OcrEngine.TryCreateFromUserProfileLanguages();
        if (latin == null || IsCjk(latin.RecognizerLanguage))
        {
            var latinLanguage = available.FirstOrDefault(l => l.LanguageTag.StartsWith("en")) ?? available.FirstOrDefault(l => !IsCjk(l));
            latin = latinLanguage != null ? OcrEngine.TryCreateFromLanguage(latinLanguage) : null;
        }
        var cjk = cjkLanguage != null ? OcrEngine.TryCreateFromLanguage(cjkLanguage) : null;
        return (cjk, latin);
    }

    public static bool HasEngine => Engines.Value.Cjk != null || Engines.Value.Latin != null;

    /// <summary>A page counts as having text if there's more than a stray header or page number.</summary>
    public static bool HasTextLayer(string text) => text.Count(c => !char.IsWhiteSpace(c)) >= 20;

    /// <summary>OCR one page. Returns an empty string if nothing is readable.</summary>
    public static string Recognize(WinPdf.PdfPage page)
    {
        if (!HasEngine) return "";
        // 3x (216 dpi) gives the engine enough detail for small print.
        var image = PdfRenderer.Render(page, 3, OcrEngine.MaxImageDimension);
        return Recognize(image);
    }

    public static string Recognize(BitmapSource image)
    {
        var (cjk, latin) = Engines.Value;
        if (cjk == null && latin == null) return "";
        var bgra = ImageConverter.ToBgra32(image);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pixels));

        string Read(OcrEngine engine)
        {
            try { return Layout(engine.RecognizeAsync(bitmap).Get()); }
            catch { return ""; }
        }
        if (cjk == null) return Read(latin!);
        var text = Read(cjk);
        if (latin == null) return text;
        // Mostly Latin script: the Chinese engine splits words and uses full-width punctuation, so read it again.
        int letters = text.Count(char.IsLetter);
        int cjkCount = text.Count(PdfFonts.IsCjk);
        return letters == 0 || cjkCount < letters * 0.1 ? Read(latin) : text;
    }

    /// <summary>Lines top to bottom, with a blank line wherever the gap between lines suggests a new paragraph.</summary>
    private static string Layout(OcrResult result)
    {
        var lines = result.Lines
            .Where(l => l.Words.Count > 0)
            .Select(l => (Text: Join(l), Top: l.Words.Min(w => w.BoundingRect.Top), Bottom: l.Words.Max(w => w.BoundingRect.Bottom)))
            .ToList();
        if (lines.Count == 0) return "";
        // Typical distance from one line to the next; a clearly bigger step starts a new paragraph.
        var steps = lines.Zip(lines.Skip(1), (a, b) => b.Top - a.Top).Where(s => s > 0).OrderBy(s => s).ToList();
        double step = steps.Count > 0 ? steps[steps.Count / 4] : double.MaxValue;   // lower quartile: plain line spacing
        var builder = new StringBuilder(lines[0].Text);
        for (int i = 1; i < lines.Count; i++)
        {
            builder.Append(lines[i].Top - lines[i - 1].Top > step * 1.5 ? "\n\n" : "\n").Append(lines[i].Text);
        }
        return builder.ToString();
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
