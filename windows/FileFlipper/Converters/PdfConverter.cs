using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using static FileFlipper.Loc;

namespace FileFlipper.Converters;

/// <summary>
/// PDF conversions and tools. Pages are rendered by Windows' own PDF engine, text is read with
/// PdfPig (OCR for scans), and PDFs are assembled and edited with PDFsharp.
/// </summary>
public static class PdfConverter
{
    // MARK: Convert

    /// <summary>
    /// One image per page. A single page is saved next to the PDF; several pages go into a folder as
    /// "Page 001.png", "Page 002.png", ... (TIFF keeps all pages in one file).
    /// </summary>
    public static List<string> ToImages(string path, ImageTarget target)
    {
        var document = PdfRenderer.Open(path);
        if (document.PageCount == 0) throw new ConversionException(L("%@ has no pages", Path.GetFileName(path)));

        if (document.PageCount == 1 || target.Container == ImageConverter.TiffFormat)
        {
            var images = new List<BitmapSource>();
            for (uint index = 0; index < document.PageCount; index++)
            {
                using var page = document.GetPage(index);
                images.Add(PdfRenderer.Render(page));
            }
            var output = OutputNaming.Next(path, target.Ext);
            ImageConverter.Write(images, output, target.Container, 0.9);
            return new() { output };
        }

        var folder = OutputNaming.MakeFolder(path, $" ({target.Ext.ToUpperInvariant()})");
        for (uint index = 0; index < document.PageCount; index++)
        {
            using var page = document.GetPage(index);
            var output = Path.Combine(folder, $"Page {index + 1:000}.{target.Ext}");
            ImageConverter.Write(new[] { PdfRenderer.Render(page) }, output, target.Container, 0.9);
        }
        return new() { folder };
    }

    /// <summary>Text of every page. Pages without a text layer (scans, photos) are read with OCR.</summary>
    public static string ToText(string path)
    {
        var pages = PageTexts(path);
        var text = string.Join("\n\n", pages.Select(p => p.Trim()));
        if (text.Trim().Length == 0) throw new ConversionException(L("No text found in %@", Path.GetFileName(path)) + OcrHint());
        var output = OutputNaming.Next(path, "txt");
        TextFiles.Write(output, text + "\n");
        return output;
    }

    /// <summary>Markdown from the page text (OCR for scanned pages), one section per page.</summary>
    public static string ToMarkdown(string path)
    {
        var pages = PageTexts(path);
        if (pages.All(p => p.Trim().Length == 0)) throw new ConversionException(L("No text found in %@", Path.GetFileName(path)) + OcrHint());
        var output = OutputNaming.Next(path, "md");
        TextFiles.Write(output, MarkdownWriter.FromPlainPages(pages));
        return output;
    }

    public static string ToDocument(string path, DocumentTarget target)
    {
        var pages = PageTexts(path);
        if (pages.All(p => p.Trim().Length == 0)) throw new ConversionException(L("No text found in %@", Path.GetFileName(path)) + OcrHint());
        var output = OutputNaming.Next(path, target.Ext);
        DocumentConverter.Write(Document.FromPlainPages(pages), output, target);
        return output;
    }

    private static string OcrHint() =>
        TextRecognizer.HasEngine ? "" : " — " + L("add a language with “Optical character recognition” in Windows Settings to read scans");

    /// <summary>The text of each page, using OCR where a page has no text layer.</summary>
    public static List<string> PageTexts(string path)
    {
        var texts = new List<string>();
        try
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
            foreach (var page in pdf.GetPages())
            {
                string text;
                try { text = ContentOrderTextExtractor.GetText(page); }
                catch { text = page.Text; }
                texts.Add(text);
            }
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
        {
            throw new ConversionException(L("%@ is password protected", Path.GetFileName(path)));
        }
        catch (Exception)
        {
            // PdfPig couldn't parse it; Windows' renderer may still manage, so fall back to OCR for every page.
            texts.Clear();
        }

        Windows.Data.Pdf.PdfDocument? rendered = null;
        if (texts.Count == 0)
        {
            rendered = PdfRenderer.Open(path);
            for (int i = 0; i < rendered.PageCount; i++) texts.Add("");
        }
        for (int index = 0; index < texts.Count; index++)
        {
            if (TextRecognizer.HasTextLayer(texts[index]) || !TextRecognizer.HasEngine) continue;
            rendered ??= PdfRenderer.Open(path);
            if (index >= rendered.PageCount) break;
            using var page = rendered.GetPage((uint)index);
            var recognized = TextRecognizer.Recognize(page);
            if (recognized.Trim().Length > texts[index].Trim().Length) texts[index] = recognized;
        }
        return texts;
    }

    /// <summary>Builds a PDF from images and/or PDFs, in the given order.</summary>
    public static string MakePdf(IReadOnlyList<string> paths, string anchor, string? name = null)
    {
        using var result = new PdfDocument();
        foreach (var path in paths)
        {
            if (FileKinds.Of(path) == FileKind.Pdf)
            {
                using var source = OpenForImport(path);
                foreach (var page in source.Pages) result.AddPage(page);
            }
            else
            {
                AddImagePage(result, path);
            }
        }
        if (result.PageCount == 0) throw new ConversionException(L("Nothing to put in the PDF"));
        var output = name != null
            ? OutputNaming.Unique(Path.GetDirectoryName(anchor) ?? ".", name, "pdf")
            : OutputNaming.Next(anchor, "pdf");
        Save(result, output);
        return output;
    }

    private static void AddImagePage(PdfDocument document, string path)
    {
        var image = ImageConverter.LoadOriented(path);
        // Photos go in as JPEG; pictures with transparency as PNG.
        bool alpha = ImageConverter.HasAlpha(image);
        var bytes = ImageConverter.Encode(image, alpha ? ImageConverter.PngFormat : ImageConverter.JpegFormat, 0.9);
        using var stream = new MemoryStream(bytes);
        using var picture = XImage.FromStream(stream);
        // One point per pixel (72 dpi), shrunk to fit within an A3 page so it prints sensibly.
        double width = image.PixelWidth, height = image.PixelHeight;
        double shrink = Math.Min(1, 1191 / Math.Max(width, height));
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(width * shrink);
        page.Height = XUnit.FromPoint(height * shrink);
        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawImage(picture, 0, 0, width * shrink, height * shrink);
    }

    // MARK: Tools

    public static string Compress(string path)
    {
        using var document = OpenForModify(path);
        var seen = new HashSet<PdfObjectID>();
        foreach (var page in document.Pages) CompressImages(page.Elements.GetDictionary("/Resources"), seen, 0);
        document.Options.CompressContentStreams = true;
        document.Options.NoCompression = false;
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        document.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;
        var output = OutputNaming.Next(path, "pdf", " (compressed)");
        Save(document, output);
        return FileSizes.RequireSmaller(output, path);
    }

    /// <summary>Images are resized for screen reading (at most 1600 px) and stored as JPEG; text is untouched.</summary>
    private static void CompressImages(PdfDictionary? resources, HashSet<PdfObjectID> seen, int depth)
    {
        var objects = resources?.Elements.GetDictionary("/XObject");
        if (objects == null || depth > 5) return;
        foreach (var key in objects.Elements.Keys.ToList())
        {
            if (objects.Elements.GetReference(key)?.Value is not PdfDictionary item) continue;
            if (item.Reference != null && !seen.Add(item.Reference.ObjectID)) continue;
            var subtype = item.Elements.GetName("/Subtype");
            if (subtype == "/Form")
            {
                CompressImages(item.Elements.GetDictionary("/Resources"), seen, depth + 1);
            }
            else if (subtype == "/Image")
            {
                try { RecompressImage(item); } catch { /* leave this image as it is */ }
            }
        }
    }

    private static void RecompressImage(PdfDictionary image)
    {
        if (image.Stream == null || image.Elements.GetBoolean("/ImageMask")) return;
        if (image.Elements.GetInteger("/BitsPerComponent") != 8) return;
        if (image.Elements["/Decode"] != null) return;

        var components = ColorComponents(image.Elements["/ColorSpace"]);
        if (components is not (1 or 3)) return;
        var filter = image.Elements["/Filter"];
        var filterName = filter switch
        {
            PdfName single => single.Value,
            PdfArray { Elements.Count: 1 } array => (array.Elements[0] as PdfName)?.Value,
            _ => null,
        };
        int width = image.Elements.GetInteger("/Width"), height = image.Elements.GetInteger("/Height");
        if (width < 64 || height < 64) return;

        var originalLength = image.Stream.Value.Length;
        BitmapSource bitmap;
        if (filterName == "/DCTDecode")
        {
            using var stream = new MemoryStream(image.Stream.Value);
            bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            if (bitmap.Format == PixelFormats.Cmyk32) return;
        }
        else if (filterName == "/FlateDecode" || filterName == null)
        {
            if (filterName != null && !image.Stream.TryUncompress()) return;
            var raw = image.Stream.Value;
            int stride = width * components;
            if (raw.Length < stride * height) return;
            bitmap = BitmapSource.Create(width, height, 72, 72, components == 3 ? PixelFormats.Rgb24 : PixelFormats.Gray8, null, raw, stride);
        }
        else
        {
            return;
        }

        const double maxSide = 1600;
        double scale = Math.Min(1, maxSide / Math.Max(width, height));
        BitmapSource resized = bitmap;
        if (scale < 1)
        {
            var transformed = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            transformed.Freeze();
            resized = transformed;
        }
        var jpeg = ImageConverter.Encode(resized, ImageConverter.JpegFormat, 0.6);
        if (jpeg.Length >= originalLength) return;

        image.Stream.Value = jpeg;
        image.Elements["/Filter"] = new PdfName("/DCTDecode");
        image.Elements.Remove("/DecodeParms");
        image.Elements.SetInteger("/Width", resized.PixelWidth);
        image.Elements.SetInteger("/Height", resized.PixelHeight);
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements.SetInteger("/Length", jpeg.Length);
        if (components == 1 && resized.Format != PixelFormats.Gray8)
            image.Elements["/ColorSpace"] = new PdfName("/DeviceRGB");
    }

    private static int ColorComponents(PdfItem? colorSpace)
    {
        if (colorSpace is PdfReference reference) colorSpace = reference.Value;
        switch (colorSpace)
        {
            case PdfName name:
                return name.Value switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };
            case PdfArray array when array.Elements.Count >= 2 && (array.Elements[0] as PdfName)?.Value == "/ICCBased":
                var profile = array.Elements[1] is PdfReference r ? r.Value as PdfDictionary : array.Elements[1] as PdfDictionary;
                return profile?.Elements.GetInteger("/N") ?? 0;
            default:
                return 0;
        }
    }

    public static string StripMetadata(string path)
    {
        using var document = OpenForModify(path);
        foreach (var key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator", "/Producer", "/CreationDate", "/ModDate" })
        {
            document.Info.Elements.Remove(key);
        }
        document.Internals.Catalog.Elements.Remove("/Metadata");   // XMP
        var output = OutputNaming.Next(path, "pdf", " (no metadata)");
        Save(document, output);
        return output;
    }

    public static string Rotate(string path)
    {
        using var document = OpenForModify(path);
        foreach (var page in document.Pages) page.Rotate = (page.Rotate + 90) % 360;
        var output = OutputNaming.Next(path, "pdf", " (rotated)");
        Save(document, output);
        return output;
    }

    /// <summary>One PDF per page, in a folder next to the original.</summary>
    public static List<string> Split(string path)
    {
        using var document = OpenForImport(path);
        if (document.PageCount <= 1) throw new ConversionException(L("%@ only has one page", Path.GetFileName(path)));
        var folder = OutputNaming.MakeFolder(path, " (pages)");
        for (int index = 0; index < document.PageCount; index++)
        {
            using var single = new PdfDocument();
            single.AddPage(document.Pages[index]);
            Save(single, Path.Combine(folder, $"Page {index + 1:000}.pdf"));
        }
        return new() { folder };
    }

    // MARK: Helpers

    private static PdfDocument OpenForImport(string path) => Open(path, PdfDocumentOpenMode.Import);
    private static PdfDocument OpenForModify(string path) => Open(path, PdfDocumentOpenMode.Modify);

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException error) when (error.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConversionException(L("%@ is password protected", Path.GetFileName(path)));
        }
        catch (Exception error) when (error is not ConversionException)
        {
            throw new ConversionException(L("Couldn't read %@", Path.GetFileName(path)) + " (" + error.Message + ")");
        }
    }

    private static void Save(PdfDocument document, string output)
    {
        try
        {
            document.Save(output);
        }
        catch (Exception error)
        {
            FileSizes.TryDelete(output);
            throw new ConversionException(L("Couldn't write %@", Path.GetFileName(output)) + " (" + error.Message + ")");
        }
    }
}
