using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FileFlipper;

/// <summary>
/// Localized text: the English text is the key; translations live in Localization/zh-Hans.strings
/// (the same format as the Mac app's Localizable.strings).
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, string> Table = Load();

    public static bool IsChinese { get; } = DetectChinese();

    private static bool DetectChinese()
    {
        // Simplified Chinese (zh-CN, zh-SG, zh-Hans-…); Traditional Chinese falls back to English.
        var name = CultureInfo.CurrentUICulture.Name;
        if (!name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var traditional in new[] { "Hant", "TW", "HK", "MO" })
        {
            if (name.Contains(traditional, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static Dictionary<string, string> Load()
    {
        var table = new Dictionary<string, string>();
        if (!DetectChinese()) return table;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("zh-Hans.strings");
        if (stream == null) return table;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        // "key" = "value";
        var pattern = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*;");
        foreach (Match match in pattern.Matches(text))
        {
            table[Unescape(match.Groups[1].Value)] = Unescape(match.Groups[2].Value);
        }
        return table;
    }

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");

    /// <summary>Looks up <paramref name="key"/> and fills each "%@" with the next argument.</summary>
    public static string L(string key, params object[] arguments)
    {
        var text = Table.TryGetValue(key, out var translated) ? translated : key;
        if (arguments.Length == 0) return text;
        var builder = new StringBuilder();
        int index = 0, start = 0;
        while (true)
        {
            int at = text.IndexOf("%@", start, StringComparison.Ordinal);
            if (at < 0) break;
            builder.Append(text, start, at - start);
            builder.Append(index < arguments.Length ? Convert.ToString(arguments[index], CultureInfo.CurrentCulture) : "");
            index++;
            start = at + 2;
        }
        builder.Append(text, start, text.Length - start);
        return builder.ToString();
    }
}

/// <summary>A conversion failed (or the user backed out) with a message meant for the user.</summary>
public sealed class ConversionException : Exception
{
    public bool IsCancelled { get; }

    public ConversionException(string message, bool cancelled = false) : base(message)
    {
        IsCancelled = cancelled;
    }

    public static ConversionException Cancelled() => new(Loc.L("Cancelled"), cancelled: true);
    public static ConversionException Unreadable(string path) => new(Loc.L("Couldn't read %@", Path.GetFileName(path)));
    public static ConversionException WriteFailed(string path) => new(Loc.L("Couldn't write %@", Path.GetFileName(path)));
    public static ConversionException Unsupported(string what) => new(Loc.L("%@ isn't supported on this PC", what));
}

/// <summary>Converted copies are saved next to the original, never overwriting anything.</summary>
public static class OutputNaming
{
    /// <summary>
    /// <c>Photo.heic</c> + suffix " (compressed)" + ext "jpg" → <c>Photo (compressed).jpg</c>,
    /// then <c>Photo (compressed) (2).jpg</c>, … (the way File Explorer numbers copies).
    /// </summary>
    public static string Next(string path, string ext, string suffix = "")
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path) + suffix;
        return Unique(directory, name, ext);
    }

    public static string Unique(string directory, string name, string ext)
    {
        string Candidate(string baseName) => Path.Combine(directory, baseName + "." + ext);
        var candidate = Candidate(name);
        int counter = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Candidate($"{name} ({counter})");
            counter++;
        }
        return candidate;
    }

    /// <summary>Creates a new folder next to <paramref name="path"/> (used when one file produces many outputs).</summary>
    public static string MakeFolder(string path, string suffix)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path) + suffix;
        var folder = Path.Combine(directory, name);
        int counter = 2;
        while (Directory.Exists(folder) || File.Exists(folder))
        {
            folder = Path.Combine(directory, $"{name} ({counter})");
            counter++;
        }
        Directory.CreateDirectory(folder);
        return folder;
    }
}

public static class FileSizes
{
    public static long Bytes(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>For "Compress" tools: keeps <paramref name="output"/> only if it really is smaller.</summary>
    public static string RequireSmaller(string output, string original)
    {
        if (Bytes(output) < Bytes(original)) return output;
        TryDelete(output);
        throw new ConversionException(Loc.L("%@ is already as small as it gets", Path.GetFileName(original)));
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>Some WPF classes (the RTF reader, FlowDocument) only work on an STA thread.</summary>
public static class Sta
{
    public static T Run<T>(Func<T> body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return body();
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}

/// <summary>Writes text files the way people expect on Windows: UTF-8 with Windows line endings.</summary>
public static class TextFiles
{
    public static void Write(string path, string text, bool bom = false)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(path, normalized, new UTF8Encoding(bom));
    }

    /// <summary>Reads a text file: BOM if present, else UTF-8 if valid, else the system code page (e.g. GBK).</summary>
    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            try { return Encoding.GetEncoding(codePage).GetString(bytes); }
            catch { return Encoding.Latin1.GetString(bytes); }
        }
    }
}
