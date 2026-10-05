using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileFlipper.Converters;
using static FileFlipper.Loc;

namespace FileFlipper;

/// <summary>One bubble on the picker arc.</summary>
public sealed class PickerItem
{
    public required string Title { get; init; }
    /// <summary>Icon-font glyph inside the bubble. Formats get one from <see cref="Catalog.FormatGlyph"/>.</summary>
    public string? Glyph { get; init; }
    /// <summary>One line shown near the pointer while the bubble is hovered.</summary>
    public string? Detail { get; init; }
    /// <summary>Takes the dropped files and returns the files (or folders) it created.</summary>
    public required Func<IReadOnlyList<string>, List<string>> Action { get; init; }
}

public enum FileKind { Image, Pdf, Document, Presentation, Spreadsheet, Video, Audio, Other }

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, params string[] extensions)
        {
            foreach (var ext in extensions) map[ext] = kind;
        }
        Add(FileKind.Image, "png", "jpg", "jpeg", "jpe", "jfif", "heic", "heif", "avif", "webp", "tif", "tiff", "gif",
            "bmp", "dib", "ico", "jxr", "wdp", "dng", "cr2", "cr3", "nef", "nrw", "arw", "orf", "rw2", "raf", "srw", "pef", "raw");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Document, DocumentConverter.ReadableExtensions);
        Add(FileKind.Presentation, "pptx", "pptm", "ppsx");
        Add(FileKind.Spreadsheet, "xlsx", "xlsm");
        Add(FileKind.Video, "mp4", "m4v", "mov", "qt", "avi", "wmv", "mkv", "3gp", "3g2", "mts", "m2ts", "webm");
        Add(FileKind.Audio, "mp3", "m4a", "aac", "wav", "wave", "wma", "flac", "aif", "aiff", "alac", "amr", "ac3");
        return map;
    }

    public static FileKind Of(string path)
    {
        var ext = Extension(path);
        return ByExtension.TryGetValue(ext, out var kind) ? kind : FileKind.Other;
    }

    /// <summary>Lower-case extension without the dot.</summary>
    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
}

/// <summary>Decides what appears on the picker for a given set of dragged files.</summary>
public static class Catalog
{
    public static List<PickerItem> Items(IReadOnlyList<string> paths, bool tools)
    {
        if (paths.Count == 0) return new();
        var first = paths[0];
        var kind = FileKinds.Of(first);
        if (tools) return ToolItems(kind, paths.Count(p => FileKinds.Of(p) == kind));
        return FormatItems(kind, FileKinds.Extension(first));
    }

    // MARK: Formats (Shift)

    private static List<PickerItem> FormatItems(FileKind kind, string sourceExt)
    {
        var items = new List<PickerItem>();
        switch (kind)
        {
            case FileKind.Image:
                foreach (var target in ImageConverter.Targets.Where(t => !t.Matches(sourceExt) && ImageConverter.CanWrite(t)))
                {
                    items.Add(new PickerItem { Title = target.Title, Action = PerFile(kind, p => new() { ImageConverter.Convert(p, target) }) });
                }
                items.Add(new PickerItem { Title = L("PDF"), Action = PerFile(kind, p => new() { PdfConverter.MakePdf(new[] { p }, p) }) });
                break;

            case FileKind.Pdf:
                foreach (var name in new[] { "PNG", "JPG", "TIFF", "HEIC" })
                {
                    var target = ImageConverter.Targets.FirstOrDefault(t => t.Title == name);
                    if (target == null || !ImageConverter.CanWrite(target)) continue;
                    items.Add(new PickerItem { Title = name, Action = PerFile(kind, p => PdfConverter.ToImages(p, target)) });
                }
                items.Add(new PickerItem { Title = L("TXT"), Action = PerFile(kind, p => new() { PdfConverter.ToText(p) }) });
                items.Add(new PickerItem
                {
                    Title = L("MD"), Detail = L("Save as Markdown (OCR for scans)"),
                    Action = PerFile(kind, p => new() { PdfConverter.ToMarkdown(p) }),
                });
                foreach (var target in DocumentConverter.Targets.Where(t => t.Ext is "docx" or "rtf"))
                {
                    items.Add(new PickerItem { Title = target.Title, Action = PerFile(kind, p => new() { PdfConverter.ToDocument(p, target) }) });
                }
                break;

            case FileKind.Document:
                foreach (var target in DocumentConverter.Targets.Where(t => !t.Matches(sourceExt)))
                {
                    items.Add(new PickerItem
                    {
                        Title = target.Title,
                        Detail = target.Ext == "md" ? L("Markdown, ready for AI") : null,
                        Action = PerFile(kind, p => new() { DocumentConverter.Convert(p, target) }),
                    });
                }
                break;

            case FileKind.Presentation:
                items.Add(new PickerItem { Title = L("PDF"), Detail = L("One page per slide"), Action = PerFile(kind, p => new() { PresentationConverter.ToPdf(p) }) });
                items.Add(new PickerItem { Title = L("MD"), Detail = L("Slide titles and text as Markdown"), Action = PerFile(kind, p => new() { PresentationConverter.ToMarkdown(p) }) });
                break;

            case FileKind.Spreadsheet:
                items.Add(new PickerItem { Title = L("PDF"), Detail = L("Every sheet as a table"), Action = PerFile(kind, p => new() { SpreadsheetConverter.ToPdf(p) }) });
                items.Add(new PickerItem { Title = L("MD"), Detail = L("Every sheet as a Markdown table"), Action = PerFile(kind, p => new() { SpreadsheetConverter.ToMarkdown(p) }) });
                break;

            case FileKind.Video:
                foreach (var target in MediaConverter.VideoTargets.Where(t => !t.Matches(sourceExt)))
                {
                    items.Add(new PickerItem { Title = target.Title, Action = PerFile(kind, p => new() { MediaConverter.ConvertVideo(p, target) }) });
                }
                break;

            case FileKind.Audio:
                foreach (var target in MediaConverter.AudioTargets.Where(t => !t.Matches(sourceExt)))
                {
                    items.Add(new PickerItem { Title = target.Title, Action = PerFile(kind, p => new() { MediaConverter.ConvertAudio(p, target) }) });
                }
                break;
        }
        return items;
    }

    // MARK: Tools (Ctrl + Shift)

    private static List<PickerItem> ToolItems(FileKind kind, int count)
    {
        var items = new List<PickerItem>();
        PickerItem Tool(string title, string glyph, string detail, Func<string, string> body) => new()
        {
            Title = L(title), Glyph = glyph, Detail = L(detail), Action = PerFile(kind, p => new() { body(p) }),
        };

        switch (kind)
        {
            case FileKind.Image:
                items.Add(Tool("Crop", Glyph.Crop, "Crop to a selected area", ImageConverter.Crop));
                items.Add(Tool("Compress", Glyph.Compress, "Make the image smaller", ImageConverter.Compress));
                items.Add(Tool("Clean", Glyph.Clean, "Remove GPS and camera info", ImageConverter.StripMetadata));
                items.Add(Tool("50%", Glyph.Compress, "Half the width and height", ImageConverter.ResizeHalf));
                items.Add(Tool("Rotate", Glyph.Rotate, "Rotate 90° clockwise", ImageConverter.Rotate));
                items.Add(Tool("Flip", Glyph.Flip, "Mirror left to right", ImageConverter.Flip));
                items.Add(Tool("B&W", Glyph.Grayscale, "Black and white", ImageConverter.Grayscale));
                items.Add(Tool("Cutout", Glyph.Cutout, "Remove a plain background", ImageConverter.RemoveBackground));
                if (count > 1)
                {
                    items.Add(new PickerItem { Title = L("Merge"), Glyph = Glyph.Merge, Detail = L("Combine into one PDF"), Action = Merge(kind) });
                }
                break;

            case FileKind.Pdf:
                items.Add(Tool("Compress", Glyph.Compress, "Make the PDF smaller", PdfConverter.Compress));
                items.Add(Tool("Clean", Glyph.Clean, "Remove author info", PdfConverter.StripMetadata));
                items.Add(Tool("Rotate", Glyph.Rotate, "Rotate every page 90°", PdfConverter.Rotate));
                items.Add(new PickerItem
                {
                    Title = L("Split"), Glyph = Glyph.Split, Detail = L("One PDF per page"),
                    Action = PerFile(kind, p => PdfConverter.Split(p)),
                });
                items.Add(Tool("Text", Glyph.Text, "Extract text, OCR for scans", PdfConverter.ToText));
                if (count > 1)
                {
                    items.Add(new PickerItem { Title = L("Merge"), Glyph = Glyph.Merge, Detail = L("Combine the PDFs into one"), Action = Merge(kind) });
                }
                break;

            case FileKind.Video:
                items.Add(Tool("Compress", Glyph.Compress, "Make the video smaller", MediaConverter.CompressVideo));
                items.Add(Tool("720p", Glyph.Compress, "Convert to 720p", MediaConverter.ResizeVideo720));
                items.Add(Tool("Mute", Glyph.Mute, "Remove the sound", MediaConverter.Mute));
                items.Add(Tool("Audio", Glyph.Audio, "Keep only the sound (M4A)", MediaConverter.ExtractAudio));
                items.Add(Tool("Frame", Glyph.Camera, "Save one frame as an image", MediaConverter.Snapshot));
                break;

            case FileKind.Audio:
                items.Add(Tool("Compress", Glyph.Compress, "Make the file smaller", MediaConverter.CompressAudio));
                items.Add(Tool("Mono", Glyph.Mono, "Convert to mono", MediaConverter.Mono));
                break;

            case FileKind.Document:
                items.Add(Tool("Plain", Glyph.Plain, "Remove all formatting", DocumentConverter.StripFormatting));
                break;
        }
        return items;
    }

    // MARK: Helpers

    /// <summary>Icon for a format bubble, by its label.</summary>
    public static string FormatGlyph(string title) => title switch
    {
        "PDF" => Glyph.Pdf,
        "TXT" => Glyph.Document,
        "MD" => Glyph.Markdown,
        "DOCX" or "DOC" or "ODT" or "RTF" => Glyph.Document,
        "HTML" => Glyph.Code,
        "GIF" => Glyph.Gif,
        "MP4" or "MOV" or "WMV" => Glyph.Video,
        "M4A" or "MP3" or "WAV" or "FLAC" or "WMA" => Glyph.Audio,
        _ => Glyph.Photo,
    };

    /// <summary>Runs <paramref name="body"/> for every dropped file of the given kind.</summary>
    private static Func<IReadOnlyList<string>, List<string>> PerFile(FileKind kind, Func<string, List<string>> body) => paths =>
    {
        var outputs = new List<string>();
        Exception? firstError = null;
        foreach (var path in paths.Where(p => FileKinds.Of(p) == kind))
        {
            try
            {
                outputs.AddRange(body(path));
            }
            catch (Exception error)
            {
                if (error is ConversionException { IsCancelled: true }) throw;
                firstError ??= error;
            }
        }
        if (outputs.Count == 0 && firstError != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
        }
        return outputs;
    };

    private static Func<IReadOnlyList<string>, List<string>> Merge(FileKind kind) => paths =>
    {
        var matching = paths.Where(p => FileKinds.Of(p) == kind).ToList();
        if (matching.Count == 0) return new();
        return new() { PdfConverter.MakePdf(matching, matching[0], "Merged") };
    };
}

/// <summary>A target format: label on the picker + file extension.</summary>
public class FormatTarget
{
    public required string Title { get; init; }
    public required string Ext { get; init; }
    public string[] Aliases { get; init; } = Array.Empty<string>();

    public bool Matches(string ext) => ext == Ext || Aliases.Contains(ext);
}
