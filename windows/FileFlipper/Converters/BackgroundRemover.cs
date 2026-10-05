using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FileFlipper.Loc;

namespace FileFlipper.Converters;

/// <summary>
/// The image "Cutout" tool.
///
/// Pictures on a plain background (logos, icons, screenshots, product shots on white) are handled
/// like a magic wand: the background colour is flood-filled from the edges, and the soft rim around
/// the subject is made semi-transparent with the background colour taken out of it, so no light
/// fringe is left behind. (The Mac app also lifts subjects out of photos with Apple's Vision
/// framework; Windows has no built-in equivalent that apps can use, so photos are not supported.)
/// </summary>
public static class BackgroundRemover
{
    /// <summary>Colour distance (0…441) up to which a pixel counts as background.</summary>
    private const double Tolerance = 36;
    /// <summary>Pixels between <see cref="Tolerance"/> and this distance near the background become semi-transparent.</summary>
    private const double SoftLimit = 80;
    /// <summary>How far (in pixels) the soft rim may reach into the subject.</summary>
    private const int SoftDepth = 6;

    public static BitmapSource RemoveBackground(BitmapSource image, string name)
    {
        return RemoveSolidBackground(image)
            ?? throw new ConversionException(L("Cutout works on pictures with a plain background, like logos, icons and product shots (%@)", name));
    }

    /// <summary>Returns <c>null</c> when the background isn't a single plain colour.</summary>
    public static BitmapSource? RemoveSolidBackground(BitmapSource source)
    {
        var image = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int width = image.PixelWidth, height = image.PixelHeight;
        if (width <= 2 || height <= 2) return null;
        int count = width * height;
        var pixels = new byte[count * 4];
        image.CopyPixels(pixels, width * 4, 0);

        // 1. Estimate the background colour from the outermost pixels.
        var border = new List<int>(2 * (width + height));
        for (int x = 0; x < width; x++) { border.Add(x); border.Add((height - 1) * width + x); }
        for (int y = 1; y < height - 1; y++) { border.Add(y * width); border.Add(y * width + width - 1); }

        var opaqueBorder = border.Where(i => pixels[i * 4 + 3] > 250).ToList();
        // A mostly transparent border means the background is already gone (or there is none).
        if (opaqueBorder.Count * 2 <= border.Count) return null;
        double Median(int channel)
        {
            var values = opaqueBorder.Select(i => pixels[i * 4 + channel]).OrderBy(v => v).ToList();
            return values[values.Count / 2];
        }
        var background = new[] { Median(0), Median(1), Median(2) };

        double Distance(int index)
        {
            int offset = index * 4;
            double alpha = pixels[offset + 3];
            if (alpha < 8) return 0;   // already transparent: treat as background
            // Un-premultiply before comparing.
            double scale = 255 / alpha;
            double d0 = pixels[offset] * scale - background[0];
            double d1 = pixels[offset + 1] * scale - background[1];
            double d2 = pixels[offset + 2] * scale - background[2];
            return Math.Sqrt(d0 * d0 + d1 * d1 + d2 * d2);
        }

        // 2. It has to be a *plain* background: nearly all of the border must match it.
        int matching = border.Count(i => Distance(i) <= Tolerance);
        if (matching < border.Count * 0.85) return null;

        // 3. Flood-fill the background from the edges.
        var isBackground = new bool[count];
        var queue = new List<int>(count / 4);
        foreach (var index in border)
        {
            if (!isBackground[index] && Distance(index) <= Tolerance)
            {
                isBackground[index] = true;
                queue.Add(index);
            }
        }
        int head = 0;
        while (head < queue.Count)
        {
            int index = queue[head++];
            ForEachNeighbour(index, width, height, next =>
            {
                if (!isBackground[next] && Distance(next) <= Tolerance)
                {
                    isBackground[next] = true;
                    queue.Add(next);
                }
            });
        }
        if (head == 0 || head >= count) return null;   // nothing found, or nothing left

        // 4. Soft rim: walk a few pixels into the subject from the background edge.
        var alphaMap = new double[count];
        Array.Fill(alphaMap, 1.0);
        var depth = new int[count];
        Array.Fill(depth, -1);
        var rim = new List<int>();
        foreach (var index in queue)
        {
            alphaMap[index] = 0;
            ForEachNeighbour(index, width, height, next =>
            {
                if (!isBackground[next] && depth[next] < 0)
                {
                    depth[next] = 1;
                    rim.Add(next);
                }
            });
        }
        head = 0;
        while (head < rim.Count)
        {
            int index = rim[head++];
            double d = Distance(index);
            if (d >= SoftLimit) continue;   // solid subject: stop here
            alphaMap[index] = Math.Min(alphaMap[index], Math.Max(0, (d - Tolerance) / (SoftLimit - Tolerance)));
            if (depth[index] >= SoftDepth) continue;
            ForEachNeighbour(index, width, height, next =>
            {
                if (!isBackground[next] && depth[next] < 0)
                {
                    depth[next] = depth[index] + 1;
                    rim.Add(next);
                }
            });
        }

        // 5. Apply: remove the background colour that bled into semi-transparent pixels.
        for (int index = 0; index < count; index++)
        {
            double a = alphaMap[index];
            if (a >= 1) continue;
            int offset = index * 4;
            double original = pixels[offset + 3] / 255.0;
            double newAlpha = a * original;
            for (int channel = 0; channel < 3; channel++)
            {
                double observed = original > 0 ? pixels[offset + channel] / original : 0;
                double subject = observed - (1 - a) * background[channel];
                // Premultiplied values can never exceed the alpha.
                pixels[offset + channel] = (byte)Math.Max(0, Math.Min(newAlpha * 255, subject * original));
            }
            pixels[offset + 3] = (byte)Math.Max(0, Math.Min(255, newAlpha * 255));
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        var straight = new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return straight;
    }

    private static void ForEachNeighbour(int index, int width, int height, Action<int> body)
    {
        int x = index % width;
        if (x > 0) body(index - 1);
        if (x < width - 1) body(index + 1);
        if (index >= width) body(index - width);
        if (index < (height - 1) * width) body(index + width);
    }
}
