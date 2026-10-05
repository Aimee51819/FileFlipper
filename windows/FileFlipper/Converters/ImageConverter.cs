using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FileFlipper.Loc;

namespace FileFlipper.Converters;

public sealed class ImageTarget : FormatTarget
{
    /// <summary>WIC container format of the encoder.</summary>
    public required Guid Container { get; init; }
}

/// <summary>
/// Image conversions and tools, built on the Windows Imaging Component (the same codecs the Photos app
/// uses), so HEIC, WEBP and camera RAW files open whenever the matching Windows extension is installed.
/// </summary>
public static class ImageConverter
{
    public static readonly Guid PngFormat = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    public static readonly Guid JpegFormat = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    public static readonly Guid TiffFormat = new("163bcc30-e2e9-4f0b-961d-a3e9fdb788a3");
    public static readonly Guid GifFormat = new("1f8a5601-7d4d-4cbd-9c82-1bc8d4eeb9a5");
    public static readonly Guid BmpFormat = new("0af1d87e-fcfe-4188-bdeb-a7906471cbe3");
    public static readonly Guid HeifFormat = new("e1e62521-6787-405b-a339-500715b5763f");

    public static readonly ImageTarget[] Targets =
    {
        new() { Title = "PNG", Ext = "png", Container = PngFormat },
        new() { Title = "JPG", Ext = "jpg", Container = JpegFormat, Aliases = new[] { "jpeg", "jpe", "jfif" } },
        new() { Title = "HEIC", Ext = "heic", Container = HeifFormat, Aliases = new[] { "heif" } },
        new() { Title = "TIFF", Ext = "tiff", Container = TiffFormat, Aliases = new[] { "tif" } },
        new() { Title = "GIF", Ext = "gif", Container = GifFormat },
        new() { Title = "BMP", Ext = "bmp", Container = BmpFormat, Aliases = new[] { "dib" } },
    };

    public static ImageTarget Png => Targets[0];
    public static ImageTarget Jpeg => Targets[1];
    public static ImageTarget Heic => Targets[2];

    private static readonly ConcurrentDictionary<Guid, bool> Writable = new();

    /// <summary>HEIC needs the HEIF and HEVC extensions from the Microsoft Store, so try a tiny encode once.</summary>
    public static bool CanWrite(ImageTarget target) => Writable.GetOrAdd(target.Container, container =>
    {
        try
        {
            var pixels = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr32, null, new byte[16 * 16 * 4], 16 * 4);
            var bytes = Encode(pixels, container, 0.9);
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    });

    public static ImageTarget? TargetFor(string ext) => Targets.FirstOrDefault(t => t.Matches(ext));

    // MARK: Convert

    public static string Convert(string path, ImageTarget target)
    {
        var decoder = Open(path);
        // Keep every frame/page for formats that support it.
        var keepsFrames = target.Container == GifFormat || target.Container == TiffFormat;
        var frames = keepsFrames ? decoder.Frames.ToList() : new List<BitmapFrame> { decoder.Frames[0] };
        var images = frames.Select(f => (BitmapSource)(frames.Count == 1 ? Orient(f) : f)).ToList();
        var output = OutputNaming.Next(path, target.Ext);
        Write(images, output, target.Container, 0.9);
        return output;
    }

    // MARK: Tools

    public static string Compress(string path)
    {
        var decoder = Open(path);
        var image = Orient(decoder.Frames[0]);
        var ext = FileKinds.Extension(path);
        ImageTarget target;
        string outputExt;
        if (Jpeg.Matches(ext))
        {
            target = Jpeg; outputExt = ext;
        }
        else if (HasAlpha(image) && CanWrite(Heic))
        {
            target = Heic; outputExt = "heic";    // keeps transparency
        }
        else if (HasAlpha(image))
        {
            target = Png; outputExt = "png";
        }
        else
        {
            target = Jpeg; outputExt = "jpg";
        }
        var output = OutputNaming.Next(path, outputExt, " (compressed)");
        Write(new[] { image }, output, target.Container, 0.6);
        return FileSizes.RequireSmaller(output, path);
    }

    /// <summary>Opens a crop window; saves the selected area as a new image.</summary>
    public static string Crop(string path)
    {
        var image = LoadOriented(path);
        var rect = System.Windows.Application.Current.Dispatcher.Invoke(() => CropWindow.Run(image, Path.GetFileName(path)));
        if (rect is not { } area || area.Width <= 0 || area.Height <= 0) throw ConversionException.Cancelled();
        var cropped = new CroppedBitmap(image, area);
        cropped.Freeze();
        return SaveLikeSource(cropped, path, " (cropped)");
    }

    /// <summary>Re-encodes the pixels only, dropping EXIF, GPS, camera and other metadata.</summary>
    public static string StripMetadata(string path) => SaveLikeSource(LoadOriented(path), path, " (no metadata)");

    public static string ResizeHalf(string path)
    {
        var image = LoadOriented(path);
        var scaled = new TransformedBitmap(image, new ScaleTransform(0.5, 0.5));
        scaled.Freeze();
        return SaveLikeSource(scaled, path, " (50%)");
    }

    public static string Rotate(string path)
    {
        var rotated = new TransformedBitmap(LoadOriented(path), new RotateTransform(90));   // clockwise
        rotated.Freeze();
        return SaveLikeSource(rotated, path, " (rotated)");
    }

    public static string Flip(string path)
    {
        var flipped = new TransformedBitmap(LoadOriented(path), new ScaleTransform(-1, 1));
        flipped.Freeze();
        return SaveLikeSource(flipped, path, " (flipped)");
    }

    public static string Grayscale(string path)
    {
        var gray = new FormatConvertedBitmap(Flatten(LoadOriented(path)), PixelFormats.Gray8, null, 0);
        gray.Freeze();
        return SaveLikeSource(gray, path, " (grayscale)");
    }

    /// <summary>See <see cref="BackgroundRemover"/>.</summary>
    public static string RemoveBackground(string path)
    {
        var image = LoadOriented(path);
        var cutout = BackgroundRemover.RemoveBackground(image, Path.GetFileName(path));
        var output = OutputNaming.Next(path, "png", " (no background)");
        Write(new[] { cutout }, output, PngFormat, 1);
        return output;
    }

    // MARK: Shared helpers

    public static BitmapDecoder Open(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw ConversionException.Unreadable(path);
            return decoder;
        }
        catch (ConversionException) { throw; }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException
                                      or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            throw new ConversionException(L("Couldn't read %@", Path.GetFileName(path)) + NeedsCodecHint(path));
        }
    }

    private static string NeedsCodecHint(string path)
    {
        var ext = FileKinds.Extension(path);
        if (ext is "heic" or "heif") return " — " + L("install “HEIF Image Extensions” and “HEVC Video Extensions” from the Microsoft Store");
        if (ext is "webp") return " — " + L("install “Webp Image Extensions” from the Microsoft Store");
        if (ext is "avif") return " — " + L("install “AV1 Video Extension” from the Microsoft Store");
        if (FileKinds.Of(path) == FileKind.Image && ext is not ("png" or "jpg" or "jpeg" or "gif" or "bmp" or "tif" or "tiff"))
            return " — " + L("install “Raw Image Extension” from the Microsoft Store");
        return "";
    }

    /// <summary>Decodes the first frame with its EXIF orientation applied.</summary>
    public static BitmapSource LoadOriented(string path) => Orient(Open(path).Frames[0]);

    public static BitmapSource Orient(BitmapFrame frame)
    {
        BitmapSource image = frame;
        ushort orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value)
                orientation = value;
        }
        catch { }

        BitmapSource Apply(BitmapSource source, Transform transform)
        {
            var transformed = new TransformedBitmap(source, transform);
            transformed.Freeze();
            return transformed;
        }
        switch (orientation)
        {
            case 2: image = Apply(image, new ScaleTransform(-1, 1)); break;
            case 3: image = Apply(image, new RotateTransform(180)); break;
            case 4: image = Apply(image, new ScaleTransform(1, -1)); break;
            case 5: image = Apply(Apply(image, new ScaleTransform(-1, 1)), new RotateTransform(270)); break;
            case 6: image = Apply(image, new RotateTransform(90)); break;
            case 7: image = Apply(Apply(image, new ScaleTransform(-1, 1)), new RotateTransform(90)); break;
            case 8: image = Apply(image, new RotateTransform(270)); break;
        }
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        return image;
    }

    public static bool HasAlpha(BitmapSource image)
    {
        var format = image.Format;
        if (format != PixelFormats.Bgra32 && format != PixelFormats.Pbgra32 && format != PixelFormats.Rgba64
            && format != PixelFormats.Prgba64 && format != PixelFormats.Rgba128Float && format != PixelFormats.Prgba128Float
            && format != PixelFormats.Indexed8 && format != PixelFormats.Indexed4 && format != PixelFormats.Indexed2
            && format != PixelFormats.Indexed1)
            return false;
        // Many PNGs carry an alpha channel that is fully opaque; only count real transparency.
        var bgra = ToBgra32(image);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 255) return true;
        }
        return false;
    }

    public static BitmapSource ToBgra32(BitmapSource image)
    {
        if (image.Format == PixelFormats.Bgra32) return image;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>JPEG, BMP and GIF here have no transparency, so composite onto white first.</summary>
    public static BitmapSource Flatten(BitmapSource image)
    {
        if (!HasAlpha(image)) return image;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            context.DrawRectangle(Brushes.White, null, rect);
            context.DrawImage(image, rect);
        }
        var target = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var opaque = new FormatConvertedBitmap(target, PixelFormats.Bgr24, null, 0);
        opaque.Freeze();
        return opaque;
    }

    /// <summary>Saves in the same format as the source when possible, otherwise PNG.</summary>
    private static string SaveLikeSource(BitmapSource image, string path, string suffix)
    {
        var ext = FileKinds.Extension(path);
        var target = TargetFor(ext);
        if (target == null || !CanWrite(target))
        {
            target = Png;
            ext = "png";
        }
        var output = OutputNaming.Next(path, ext, suffix);
        Write(new[] { image }, output, target.Container, 0.9);
        return output;
    }

    public static void Write(IReadOnlyList<BitmapSource> images, string output, Guid container, double quality)
    {
        try
        {
            File.WriteAllBytes(output, Encode(images, container, quality));
        }
        catch (Exception error) when (error is not ConversionException)
        {
            FileSizes.TryDelete(output);
            throw new ConversionException(L("Couldn't write %@", Path.GetFileName(output)) + " (" + error.Message + ")");
        }
    }

    public static byte[] Encode(BitmapSource image, Guid container, double quality) => Encode(new[] { image }, container, quality);

    public static byte[] Encode(IReadOnlyList<BitmapSource> images, Guid container, double quality)
    {
        BitmapEncoder encoder;
        if (container == JpegFormat) encoder = new JpegBitmapEncoder { QualityLevel = (int)Math.Round(quality * 100) };
        else if (container == PngFormat) encoder = new PngBitmapEncoder();
        else if (container == TiffFormat) encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        else if (container == GifFormat) encoder = new GifBitmapEncoder();
        else if (container == BmpFormat) encoder = new BmpBitmapEncoder();
        else encoder = BitmapEncoder.Create(container);

        foreach (var image in images)
        {
            BitmapSource prepared = image;
            if (container == JpegFormat || container == BmpFormat)
            {
                prepared = Flatten(image);
            }
            else if (container == GifFormat)
            {
                prepared = ToIndexed(Flatten(image));
            }
            else if (container == HeifFormat)
            {
                prepared = HasAlpha(image) ? ToBgra32(image) : new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
            }
            encoder.Frames.Add(BitmapFrame.Create(prepared));
        }
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>256 colours picked from the picture itself, which looks far better than the default web palette.</summary>
    public static BitmapSource ToIndexed(BitmapSource image)
    {
        var bgr = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        bgr.Freeze();
        var palette = new BitmapPalette(bgr, 256);
        // Copy the result into a plain bitmap so it can be frozen and used from any thread.
        var indexed = new FormatConvertedBitmap(bgr, PixelFormats.Indexed8, palette, 0);
        int stride = (indexed.PixelWidth + 3) / 4 * 4;
        var pixels = new byte[stride * indexed.PixelHeight];
        indexed.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(indexed.PixelWidth, indexed.PixelHeight, 96, 96, PixelFormats.Indexed8, palette, pixels, stride);
        copy.Freeze();
        return copy;
    }
}
