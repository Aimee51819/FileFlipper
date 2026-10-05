using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using static FileFlipper.Loc;

namespace FileFlipper.Converters;

/// <summary>Read access to an Office Open XML / OpenDocument package (a ZIP of XML parts).</summary>
public sealed class OfficePackage : IDisposable
{
    private readonly ZipArchive archive;
    private readonly Dictionary<string, ZipArchiveEntry> entries;

    public OfficePackage(string path)
    {
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new ConversionException(L("Couldn't read %@", Path.GetFileName(path)) + " (" + L("Not a valid Office file") + ")");
        }
        entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries) entries[entry.FullName.TrimStart('/')] = entry;
    }

    public void Dispose() => archive.Dispose();

    public bool Contains(string path) => entries.ContainsKey(path);

    public byte[]? Data(string path)
    {
        if (!entries.TryGetValue(path, out var entry)) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public XElement? Xml(string path)
    {
        if (!entries.TryGetValue(path, out var entry)) return null;
        try
        {
            using var stream = entry.Open();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Relationship id → (absolute package path, type), for the part at <paramref name="path"/>.</summary>
    public Dictionary<string, (string Target, string Type)> Relationships(string path)
    {
        var result = new Dictionary<string, (string, string)>();
        var directory = DirectoryOf(path);
        var relsPath = (directory.Length > 0 ? directory + "/" : "") + "_rels/" + path.Split('/').Last() + ".rels";
        var root = Xml(relsPath);
        if (root == null) return result;
        foreach (var relationship in root.Children("Relationship"))
        {
            var id = relationship.Attr("Id");
            var target = relationship.Attr("Target");
            if (id == null || target == null) continue;
            // External targets (web links) are kept as written.
            var external = relationship.Attr("TargetMode") == "External";
            result[id] = (external ? target : Resolve(target, directory), relationship.Attr("Type") ?? "");
        }
        return result;
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path.Substring(0, slash);
    }

    /// <summary>Resolves "../media/image1.png" relative to a directory inside the package.</summary>
    public static string Resolve(string target, string directory)
    {
        target = Uri.UnescapeDataString(target);
        if (target.StartsWith('/')) return target.Substring(1);
        var parts = directory.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var piece in target.Split('/'))
        {
            if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (piece != "." && piece.Length > 0) parts.Add(piece);
        }
        return string.Join("/", parts);
    }
}

/// <summary>XML helpers shared by the Office converters: everything matches on local names, ignoring namespaces.</summary>
public static class XmlExtensions
{
    public static IEnumerable<XElement> Children(this XElement? element, string name) =>
        element == null ? Enumerable.Empty<XElement>() : element.Elements().Where(e => e.Name.LocalName == name);

    public static XElement? Child(this XElement? element, string name) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>First descendant with this local name, depth first.</summary>
    public static XElement? Descendant(this XElement? element, string name) =>
        element?.Descendants().FirstOrDefault(e => e.Name.LocalName == name);

    public static IEnumerable<XElement> DescendantsNamed(this XElement? element, string name) =>
        element == null ? Enumerable.Empty<XElement>() : element.Descendants().Where(e => e.Name.LocalName == name);

    /// <summary>Attribute by local name (so "r:embed" is just "embed").</summary>
    public static string? Attr(this XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    public static double? AttrDouble(this XElement? element, string name) =>
        double.TryParse(element.Attr(name), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    public static int? AttrInt(this XElement? element, string name) =>
        int.TryParse(element.Attr(name), System.Globalization.NumberStyles.Integer,
                     System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>The relationship id ("r:id"), which can share its local name with a plain "id" attribute.</summary>
    public static string? RelationshipId(this XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Contains("relationships"))?.Value;

    public static string Text(this XElement? element) => element?.Value ?? "";
}
