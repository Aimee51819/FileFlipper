using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using static FileFlipper.Loc;

namespace FileFlipper.Converters;

public sealed class MediaTarget : FormatTarget { }

/// <summary>
/// Video and audio conversions, built on Windows' Media Foundation transcoder (hardware accelerated
/// where possible). These run on a background thread, so waiting for each step is fine.
/// </summary>
public static class MediaConverter
{
    public static readonly MediaTarget[] VideoTargets =
    {
        new() { Title = "MP4", Ext = "mp4", Aliases = new[] { "m4v" } },
        new() { Title = "GIF", Ext = "gif" },
        new() { Title = "M4A", Ext = "m4a" },
        new() { Title = "MP3", Ext = "mp3" },
    };

    public static readonly MediaTarget[] AudioTargets =
    {
        new() { Title = "MP3", Ext = "mp3" },
        new() { Title = "M4A", Ext = "m4a", Aliases = new[] { "aac" } },
        new() { Title = "WAV", Ext = "wav", Aliases = new[] { "wave" } },
        new() { Title = "FLAC", Ext = "flac" },
    };

    // MARK: Convert

    public static string ConvertVideo(string path, MediaTarget target) => target.Ext switch
    {
        "gif" => ToGif(path),
        "m4a" or "mp3" => ExtractAudio(path, target.Ext, ""),
        _ => Transcode(path, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto), "mp4", ""),
    };

    public static string ConvertAudio(string path, MediaTarget target)
    {
        var source = SourceProfile(path);
        var profile = AudioProfile(target.Ext, AudioEncodingQuality.High);
        // Lossless formats keep the source's sample rate and channels.
        if (target.Ext is "wav" or "flac" && source?.Audio is { } audio)
        {
            profile.Audio.SampleRate = audio.SampleRate;
            profile.Audio.ChannelCount = audio.ChannelCount;
            try
            {
                return Transcode(path, profile, target.Ext, "");
            }
            catch (ConversionException)
            {
                // Windows' WAV writer turns down some sample rates; fall back to its standard 44.1 kHz stereo.
                profile = AudioProfile(target.Ext, AudioEncodingQuality.High);
            }
        }
        return Transcode(path, profile, target.Ext, "");
    }

    // MARK: Video tools

    public static string CompressVideo(string path)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        var source = SourceProfile(path);
        if (source?.Video is { } video && video.Width > 0)
        {
            // About half the bits of a typical encode at this size.
            uint pixels = video.Width * video.Height;
            uint target = (uint)Math.Clamp(pixels * 1.6, 600_000, 6_000_000);
            if (video.Bitrate > 0) target = Math.Min(target, video.Bitrate / 2);
            profile.Video.Bitrate = Math.Max(400_000, target);
        }
        profile.Audio.Bitrate = 128_000;
        return FileSizes.RequireSmaller(Transcode(path, profile, "mp4", " (compressed)"), path);
    }

    public static string ResizeVideo720(string path)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        if (SourceProfile(path)?.Video is { Width: > 0, Height: > 0 } video)
        {
            // Keep the shape: the short side becomes 720 (never upscaled).
            double scale = Math.Min(1, 720.0 / Math.Min(video.Width, video.Height));
            profile.Video.Width = Even(video.Width * scale);
            profile.Video.Height = Even(video.Height * scale);
            if (video.FrameRate.Numerator > 0)
            {
                profile.Video.FrameRate.Numerator = video.FrameRate.Numerator;
                profile.Video.FrameRate.Denominator = video.FrameRate.Denominator;
            }
        }
        return Transcode(path, profile, "mp4", " (720p)");
    }

    public static string ExtractAudio(string path) => ExtractAudio(path, "m4a", " (audio)");

    private static string ExtractAudio(string path, string ext, string suffix)
    {
        if (SourceProfile(path) is { Audio: null })
            throw new ConversionException(L("%@ has no audio", Path.GetFileName(path)));
        return Transcode(path, AudioProfile(ext, AudioEncodingQuality.High), ext, suffix);
    }

    public static string Mute(string path)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Audio = null;
        return Transcode(path, profile, "mp4", " (muted)");
    }

    public static string Snapshot(string path)
    {
        var composition = Composition(path, out var clip);
        var seconds = clip.OriginalDuration.TotalSeconds;
        var time = TimeSpan.FromSeconds(seconds > 0 ? Math.Min(1, seconds / 2) : 0);
        var stream = composition.GetThumbnailAsync(time, 0, 0, VideoFramePrecision.NearestFrame).Get();
        var image = Decode(stream);
        var output = OutputNaming.Next(path, "png", " (snapshot)");
        ImageConverter.Write(new[] { image }, output, ImageConverter.PngFormat, 1);
        return output;
    }

    /// <summary>Up to the first 15 seconds at 10 fps, max 480 px.</summary>
    public static string ToGif(string path)
    {
        var composition = Composition(path, out var clip);
        var duration = Math.Min(clip.OriginalDuration.TotalSeconds, 15);
        if (!(duration > 0)) throw ConversionException.Unreadable(path);

        var properties = clip.GetVideoEncodingProperties();
        double scale = Math.Min(1, 480.0 / Math.Max(1, Math.Max(properties.Width, properties.Height)));
        int width = (int)Math.Max(2, Math.Round(properties.Width * scale));
        int height = (int)Math.Max(2, Math.Round(properties.Height * scale));

        const double fps = 10;
        var times = Enumerable.Range(0, Math.Max(1, (int)(duration * fps))).Select(i => TimeSpan.FromSeconds(i / fps)).ToList();
        var frames = new List<BitmapSource>();
        // Ask for frames in batches so a long clip doesn't hold every stream at once.
        foreach (var batch in times.Chunk(30))
        {
            var streams = composition.GetThumbnailsAsync(batch, width, height, VideoFramePrecision.NearestFrame).Get();
            foreach (var stream in streams)
            {
                try { frames.Add(Decode(stream)); } catch { }
            }
        }
        if (frames.Count == 0) throw new ConversionException(L("Couldn't read frames from %@", Path.GetFileName(path)));

        var output = OutputNaming.Next(path, "gif");
        try
        {
            File.WriteAllBytes(output, GifWriter.Animated(frames, delayCentiseconds: (int)(100 / fps)));
        }
        catch (Exception error)
        {
            FileSizes.TryDelete(output);
            throw new ConversionException(L("Couldn't write %@", Path.GetFileName(output)) + " (" + error.Message + ")");
        }
        return output;
    }

    // MARK: Audio tools

    public static string CompressAudio(string path) =>
        FileSizes.RequireSmaller(Transcode(path, AudioProfile("m4a", AudioEncodingQuality.Medium), "m4a", " (compressed)"), path);

    public static string Mono(string path)
    {
        if (FileKinds.Extension(path) is "wav" or "wave" && WavFile.TryDownmix(path, OutputNaming.Next(path, "wav", " (mono)")) is { } wav)
            return wav;
        var ext = FileKinds.Extension(path) switch
        {
            "mp3" => "mp3",
            "m4a" or "aac" => "m4a",
            "flac" => "flac",
            "wma" => "wma",
            _ => "wav",
        };
        var profile = AudioProfile(ext, AudioEncodingQuality.High);
        profile.Audio.ChannelCount = 1;
        if (SourceProfile(path)?.Audio is { SampleRate: > 0 } audio && ext is "wav" or "flac") profile.Audio.SampleRate = audio.SampleRate;
        if (ext is "mp3" or "m4a" or "wma") profile.Audio.Bitrate = Math.Min(profile.Audio.Bitrate, 128_000);
        return Transcode(path, profile, ext, " (mono)");
    }

    // MARK: Helpers

    private static MediaEncodingProfile AudioProfile(string ext, AudioEncodingQuality quality) => ext switch
    {
        "mp3" => MediaEncodingProfile.CreateMp3(quality),
        "wav" => MediaEncodingProfile.CreateWav(quality),
        "flac" => MediaEncodingProfile.CreateFlac(quality),
        "wma" => MediaEncodingProfile.CreateWma(quality),
        _ => MediaEncodingProfile.CreateM4a(quality),
    };

    private static uint Even(double value) => (uint)Math.Max(2, Math.Round(value / 2) * 2);

    private static StorageFile Input(string path)
    {
        try { return StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).Get(); }
        catch { throw ConversionException.Unreadable(path); }
    }

    private static MediaEncodingProfile? SourceProfile(string path)
    {
        try { return MediaEncodingProfile.CreateFromFileAsync(Input(path)).Get(); }
        catch { return null; }
    }

    private static string Transcode(string path, MediaEncodingProfile profile, string ext, string suffix)
    {
        var input = Input(path);
        var output = OutputNaming.Next(path, ext, suffix);
        StorageFile destination;
        try
        {
            var folder = StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(output))!).Get();
            destination = folder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.FailIfExists).Get();
        }
        catch
        {
            throw ConversionException.WriteFailed(output);
        }

        try
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = transcoder.PrepareFileTranscodeAsync(input, destination, profile).Get();
            if (!prepared.CanTranscode)
            {
                throw new ConversionException(prepared.FailureReason switch
                {
                    TranscodeFailureReason.CodecNotFound => L("Windows can't decode %@ — the codec for it isn't installed", Path.GetFileName(path)),
                    TranscodeFailureReason.InvalidProfile => ConversionException.Unsupported(ext.ToUpperInvariant()).Message,
                    _ => L("Couldn't read %@", Path.GetFileName(path)) + $" ({prepared.FailureReason})",
                });
            }
            prepared.TranscodeAsync().Wait();
        }
        catch (Exception error)
        {
            FileSizes.TryDelete(output);
            if (error is ConversionException) throw;
            throw new ConversionException(L("Export failed") + ": " + error.Message);
        }
        if (FileSizes.Bytes(output) == 0)
        {
            FileSizes.TryDelete(output);
            throw new ConversionException(L("Export failed"));
        }
        return output;
    }

    private static MediaComposition Composition(string path, out MediaClip clip)
    {
        try
        {
            clip = MediaClip.CreateFromFileAsync(Input(path)).Get();
        }
        catch (Exception error) when (error is not ConversionException)
        {
            throw new ConversionException(L("%@ has no video", Path.GetFileName(path)));
        }
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return composition;
    }

    private static BitmapSource Decode(Windows.Graphics.Imaging.ImageStream stream)
    {
        using var managed = stream.AsStreamForRead();
        var decoder = BitmapDecoder.Create(managed, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}

/// <summary>
/// Uncompressed WAV files are downmixed directly: Media Foundation's WAV writer refuses many channel
/// and sample-rate changes, and averaging PCM samples is lossless anyway.
/// </summary>
public static class WavFile
{
    /// <summary>Returns the output path, or null if the file isn't plain PCM / float WAV.</summary>
    public static string? TryDownmix(string path, string output)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); } catch { return null; }
        if (bytes.Length < 44 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE") return null;

        int format = 0, channels = 0, sampleRate = 0, bits = 0, dataOffset = -1, dataLength = 0;
        int position = 12;
        while (position + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            int size = BitConverter.ToInt32(bytes, position + 4);
            int body = position + 8;
            if (size < 0 || body > bytes.Length) break;
            if (id == "fmt " && size >= 16)
            {
                format = BitConverter.ToUInt16(bytes, body);
                channels = BitConverter.ToUInt16(bytes, body + 2);
                sampleRate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToUInt16(bytes, body + 14);
                if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(bytes, body + 24);   // extensible
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = Math.Min(size, bytes.Length - body);
                break;
            }
            position = body + size + (size & 1);
        }
        bool isFloat = format == 3;
        if (dataOffset < 0 || channels < 1 || !(format == 1 || isFloat) || bits is not (8 or 16 or 24 or 32 or 64) || (isFloat && bits < 32)) return null;
        if (channels == 1) throw new ConversionException(L("%@ is already mono", Path.GetFileName(path)));

        int bytesPerSample = bits / 8;
        int frames = dataLength / (bytesPerSample * channels);
        var mono = new byte[frames * bytesPerSample];
        double Read(int offset) => bits switch
        {
            8 => (bytes[offset] - 128) / 128.0,
            16 => BitConverter.ToInt16(bytes, offset) / 32768.0,
            24 => ((bytes[offset] | bytes[offset + 1] << 8 | (sbyte)bytes[offset + 2] << 16)) / 8388608.0,
            32 when isFloat => BitConverter.ToSingle(bytes, offset),
            32 => BitConverter.ToInt32(bytes, offset) / 2147483648.0,
            _ => BitConverter.ToDouble(bytes, offset),
        };
        void Write(int offset, double value)
        {
            value = Math.Clamp(value, -1, 1);
            switch (bits)
            {
                case 8: mono[offset] = (byte)Math.Round(value * 127 + 128); break;
                case 16: BitConverter.TryWriteBytes(mono.AsSpan(offset), (short)Math.Round(value * 32767)); break;
                case 24:
                    int v24 = (int)Math.Round(value * 8388607);
                    mono[offset] = (byte)v24; mono[offset + 1] = (byte)(v24 >> 8); mono[offset + 2] = (byte)(v24 >> 16);
                    break;
                case 32 when isFloat: BitConverter.TryWriteBytes(mono.AsSpan(offset), (float)value); break;
                case 32: BitConverter.TryWriteBytes(mono.AsSpan(offset), (int)Math.Round(value * 2147483647)); break;
                default: BitConverter.TryWriteBytes(mono.AsSpan(offset), value); break;
            }
        }
        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            int start = dataOffset + frame * bytesPerSample * channels;
            for (int channel = 0; channel < channels; channel++) sum += Read(start + channel * bytesPerSample);
            Write(frame * bytesPerSample, sum / channels);
        }

        using var stream = new FileStream(output, FileMode.CreateNew);
        using var writer = new BinaryWriter(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + mono.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)(isFloat ? 3 : 1));
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * bytesPerSample);
        writer.Write((ushort)bytesPerSample);
        writer.Write((ushort)bits);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(mono.Length);
        writer.Write(mono);
        return output;
    }
}

/// <summary>
/// Animated GIFs. WPF's GIF encoder can't set frame delays or looping, so frames are encoded one by one
/// (each with its own 256-colour palette) and stitched together with the right control blocks.
/// </summary>
public static class GifWriter
{
    public static byte[] Animated(IReadOnlyList<BitmapSource> frames, int delayCentiseconds)
    {
        using var output = new MemoryStream();
        bool headerWritten = false;
        foreach (var frame in frames)
        {
            var single = ImageConverter.Encode(frame, ImageConverter.GifFormat, 1);
            var gif = Parse(single);
            if (!headerWritten)
            {
                output.Write(gif.Header);                         // "GIF89a" + logical screen (without global palette)
                output.Write(new byte[] { 0x21, 0xFF, 0x0B });    // NETSCAPE2.0: loop forever
                output.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                output.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
                headerWritten = true;
            }
            // Graphic control extension: no disposal needed (frames are full size and opaque).
            output.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04, (byte)(delayCentiseconds & 0xFF), (byte)(delayCentiseconds >> 8), 0x00, 0x00 });
            // The frame's palette becomes a local colour table.
            var descriptor = (byte[])gif.ImageDescriptor.Clone();
            var palette = gif.Palette.Length > 0 ? gif.Palette : gif.LocalPalette;
            int sizeBits = PaletteSizeBits(palette.Length);
            descriptor[9] = (byte)((descriptor[9] & 0x40) | 0x80 | sizeBits);
            output.Write(descriptor);
            output.Write(palette);
            output.Write(gif.ImageData);
        }
        output.WriteByte(0x3B);
        return output.ToArray();
    }

    private static int PaletteSizeBits(int bytes)
    {
        int entries = bytes / 3;
        int bits = 0;
        while ((2 << bits) < entries) bits++;
        return bits;
    }

    private sealed class Parsed
    {
        public byte[] Header = Array.Empty<byte>();
        public byte[] Palette = Array.Empty<byte>();
        public byte[] LocalPalette = Array.Empty<byte>();
        public byte[] ImageDescriptor = Array.Empty<byte>();
        public byte[] ImageData = Array.Empty<byte>();
    }

    /// <summary>Splits a single-frame GIF into header, palette, image descriptor and LZW data.</summary>
    private static Parsed Parse(byte[] gif)
    {
        var result = new Parsed();
        int position = 6 + 7;
        byte flags = gif[10];
        var header = gif.Take(13).ToArray();
        header[3] = (byte)'8'; header[4] = (byte)'9'; header[5] = (byte)'a';
        header[10] = (byte)(flags & 0x70);   // drop the global colour table flag
        result.Header = header;
        if ((flags & 0x80) != 0)
        {
            int size = 3 * (2 << (flags & 0x07));
            result.Palette = gif.Skip(position).Take(size).ToArray();
            position += size;
        }
        while (position < gif.Length)
        {
            byte block = gif[position];
            if (block == 0x21)
            {
                // Skip extensions (sub-blocks until a zero length).
                position += 2;
                while (position < gif.Length && gif[position] != 0) position += gif[position] + 1;
                position++;
            }
            else if (block == 0x2C)
            {
                result.ImageDescriptor = gif.Skip(position).Take(10).ToArray();
                byte imageFlags = gif[position + 9];
                position += 10;
                if ((imageFlags & 0x80) != 0)
                {
                    int size = 3 * (2 << (imageFlags & 0x07));
                    result.LocalPalette = gif.Skip(position).Take(size).ToArray();
                    position += size;
                }
                int start = position;
                position++;   // LZW minimum code size
                while (position < gif.Length && gif[position] != 0) position += gif[position] + 1;
                position++;
                result.ImageData = gif.Skip(start).Take(position - start).ToArray();
                break;
            }
            else
            {
                break;
            }
        }
        return result;
    }
}
