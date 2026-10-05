using System;
using System.IO;
using static FileFlipper.Loc;

namespace FileFlipper.Converters;

public sealed class DocumentTarget : FormatTarget { }

/// <summary>Text document conversions: every format is read into <see cref="Document"/> and written back out.</summary>
public static class DocumentConverter
{
    public static readonly DocumentTarget[] Targets =
    {
        new() { Title = "DOCX", Ext = "docx" },
        new() { Title = "PDF", Ext = "pdf" },
        new() { Title = "RTF", Ext = "rtf" },
        new() { Title = "MD", Ext = "md", Aliases = new[] { "markdown" } },
        new() { Title = "TXT", Ext = "txt", Aliases = new[] { "text" } },
        new() { Title = "HTML", Ext = "html", Aliases = new[] { "htm" } },
        new() { Title = "ODT", Ext = "odt" },
    };

    public static readonly string[] ReadableExtensions = { "docx", "docm", "rtf", "odt", "html", "htm", "txt", "text", "md", "markdown" };

    public static Document Read(string path)
    {
        try
        {
            var document = FileKinds.Extension(path) switch
            {
                "docx" or "docm" => DocxReader.Read(path),
                "odt" => OdtReader.Read(path),
                "rtf" => RtfReader.Read(path),
                "html" or "htm" => HtmlReader.Read(path),
                "md" or "markdown" => PlainTextReader.ReadMarkdown(path),
                _ => PlainTextReader.ReadText(path),
            };
            return document;
        }
        catch (ConversionException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                                      or System.Xml.XmlException or FormatException)
        {
            throw ConversionException.Unreadable(path);
        }
    }

    public static string Convert(string path, DocumentTarget target)
    {
        var document = Read(path);
        var output = OutputNaming.Next(path, target.Ext);
        Write(document, output, target);
        return output;
    }

    public static string StripFormatting(string path)
    {
        var document = Read(path);
        var output = OutputNaming.Next(path, "txt", FileKinds.Extension(path) is "txt" or "text" ? " (plain)" : "");
        TextFiles.Write(output, document.PlainText());
        return output;
    }

    public static void Write(Document document, string output, DocumentTarget target)
    {
        try
        {
            switch (target.Ext)
            {
                case "md": TextFiles.Write(output, MarkdownWriter.Write(document)); break;
                case "txt": TextFiles.Write(output, document.PlainText()); break;
                case "html": HtmlWriter.Write(document, output, Path.GetFileNameWithoutExtension(output)); break;
                case "docx": DocxWriter.Write(document, output); break;
                case "rtf": RtfWriter.Write(document, output); break;
                case "odt": OdtWriter.Write(document, output); break;
                case "pdf": PdfDocumentWriter.Write(document, output); break;
                default: throw ConversionException.Unsupported(target.Title);
            }
        }
        catch (Exception error) when (error is not ConversionException)
        {
            FileSizes.TryDelete(output);
            throw new ConversionException(L("Couldn't write %@", Path.GetFileName(output)) + " (" + error.Message + ")");
        }
    }
}
