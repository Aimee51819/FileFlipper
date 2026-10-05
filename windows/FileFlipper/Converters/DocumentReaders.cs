using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HtmlAgilityPack;
using W = System.Windows.Documents;

namespace FileFlipper.Converters;

/// <summary>
/// Word (.docx), read straight from the document XML so that heading styles, bulleted and numbered
/// lists, bold, italic, links and tables survive (ported from the Mac app's WordMarkdown).
/// </summary>
public static class DocxReader
{
    private sealed class Style
    {
        public string Name = "";
        public string? BasedOn;
        public string? NumId;
        public int? Level;
        public int? OutlineLevel;
        public bool Bold;
        public bool Italic;
        public bool Underline;
    }

    public static Document Read(string path)
    {
        using var package = new OfficePackage(path);
        var root = package.Xml("word/document.xml");
        var body = root.Child("body") ?? throw ConversionException.Unreadable(path);
        var context = new Context(package);
        var document = new Document();
        context.Blocks(body, document.Blocks);
        return document;
    }

    private sealed class Context
    {
        private readonly Dictionary<string, (string Target, string Type)> links;
        private readonly Dictionary<string, Style> styles = new();
        /// <summary>numId → (level → is ordered list)</summary>
        private readonly Dictionary<string, Dictionary<int, bool>> numbering = new();
        /// <summary>Running counters for numbered lists, per numId and level.</summary>
        private readonly Dictionary<string, Dictionary<int, int>> counters = new();

        public Context(OfficePackage package)
        {
            links = package.Relationships("word/document.xml");
            foreach (var element in package.Xml("word/styles.xml").Children("style"))
            {
                var id = element.Attr("styleId");
                if (id == null) continue;
                var pPr = element.Child("pPr");
                styles[id] = new Style
                {
                    Name = (element.Child("name").Attr("val") ?? id).ToLowerInvariant(),
                    BasedOn = element.Child("basedOn").Attr("val"),
                    NumId = pPr.Child("numPr").Child("numId").Attr("val"),
                    Level = pPr.Child("numPr").Child("ilvl").AttrInt("val"),
                    OutlineLevel = pPr.Child("outlineLvl").AttrInt("val"),
                    Bold = IsOn(element.Child("rPr").Child("b")),
                    Italic = IsOn(element.Child("rPr").Child("i")),
                    Underline = Underlined(element.Child("rPr").Child("u")),
                };
            }
            var numberingRoot = package.Xml("word/numbering.xml");
            var abstracts = new Dictionary<string, Dictionary<int, bool>>();
            foreach (var element in numberingRoot.Children("abstractNum"))
            {
                var id = element.Attr("abstractNumId");
                if (id == null) continue;
                var levels = new Dictionary<int, bool>();
                foreach (var level in element.Children("lvl"))
                {
                    var format = level.Child("numFmt").Attr("val") ?? "bullet";
                    levels[level.AttrInt("ilvl") ?? 0] = format != "bullet" && format != "none";
                }
                abstracts[id] = levels;
            }
            foreach (var element in numberingRoot.Children("num"))
            {
                var id = element.Attr("numId");
                var abstractId = element.Child("abstractNumId").Attr("val");
                if (id != null && abstractId != null && abstracts.TryGetValue(abstractId, out var levels)) numbering[id] = levels;
            }
        }

        private static bool IsOn(XElement? element)
        {
            if (element == null) return false;
            var value = element.Attr("val");
            return value == null || !(value is "0" or "false" or "off");
        }

        private static bool Underlined(XElement? element) => element != null && element.Attr("val") != "none";

        /// <summary>Follows basedOn so inherited heading levels and lists are found.</summary>
        private Style Resolved(string? id)
        {
            var chain = new List<Style>();
            var current = id;
            while (current != null && styles.TryGetValue(current, out var style) && chain.Count < 10)
            {
                chain.Add(style);
                current = style.BasedOn;
            }
            var result = new Style { Name = chain.FirstOrDefault()?.Name ?? "" };
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var style = chain[i];
                result.NumId = style.NumId ?? result.NumId;
                result.Level = style.Level ?? result.Level;
                result.OutlineLevel = style.OutlineLevel ?? result.OutlineLevel;
                result.Bold |= style.Bold;
                result.Italic |= style.Italic;
                result.Underline |= style.Underline;
            }
            return result;
        }

        public void Blocks(XElement container, List<Block> blocks)
        {
            foreach (var element in container.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "p":
                        if (element.DescendantsNamed("br").Any(b => b.Attr("type") == "page") && blocks.Count > 0) blocks.Add(new PageBreak());
                        var paragraph = Paragraph(element);
                        if (paragraph != null) blocks.Add(paragraph);
                        break;
                    case "tbl":
                        var table = new Table();
                        foreach (var row in element.Children("tr"))
                        {
                            var cells = new List<List<Inline>>();
                            foreach (var cell in row.Children("tc"))
                            {
                                var content = new List<Inline>();
                                foreach (var p in cell.DescendantsNamed("p"))
                                {
                                    if (content.Count > 0) content.Add(new Inline("\n"));
                                    content.AddRange(Inlines(p, new Style()));
                                }
                                cells.Add(InlineList.Trim(content));
                            }
                            table.Rows.Add(cells);
                        }
                        if (table.Rows.Count > 0) blocks.Add(table);
                        break;
                    case "sdt":
                        var sdtContent = element.Child("sdtContent");
                        if (sdtContent != null) Blocks(sdtContent, blocks);
                        break;
                }
            }
        }

        private Paragraph? Paragraph(XElement p)
        {
            var pPr = p.Child("pPr");
            var style = Resolved(pPr.Child("pStyle").Attr("val"));
            var inlines = InlineList.Trim(Inlines(p, style));
            if (inlines.Count == 0) return null;
            var paragraph = new Paragraph();
            paragraph.Inlines.AddRange(inlines);

            // Headings: Title, "heading N", or an outline level.
            int? heading = null;
            if (style.Name == "title") heading = 1;
            else if (style.Name.StartsWith("heading ") && int.TryParse(style.Name.Substring(8), out var n)) heading = n;
            else if ((pPr.Child("outlineLvl").AttrInt("val") ?? style.OutlineLevel) is int outline && outline < 9) heading = outline + 1;
            if (heading is int h)
            {
                paragraph.Heading = Math.Clamp(h, 1, 6);
                return paragraph;
            }

            int? StyleLevel()
            {
                var last = style.Name.Split(' ').LastOrDefault();
                return int.TryParse(last, out var value) ? Math.Max(0, value - 1) : null;
            }

            // Lists: numbering on the paragraph, or from its style.
            var numId = pPr.Child("numPr").Child("numId").Attr("val") ?? style.NumId;
            if (numId != null && numId != "0")
            {
                int level = pPr.Child("numPr").Child("ilvl").AttrInt("val") ?? style.Level ?? StyleLevel() ?? 0;
                bool ordered = numbering.TryGetValue(numId, out var levels) && levels.TryGetValue(level, out var o) ? o : style.Name.Contains("number");
                paragraph.List = ordered ? ListKind.Ordered : ListKind.Bullet;
                paragraph.Level = level;
                if (ordered)
                {
                    if (!counters.TryGetValue(numId, out var counts)) counters[numId] = counts = new Dictionary<int, int>();
                    counts[level] = counts.GetValueOrDefault(level) + 1;
                    foreach (var deeper in counts.Keys.Where(k => k > level).ToList()) counts[deeper] = 0;
                    paragraph.Number = counts[level];
                }
                return paragraph;
            }
            // "List Bullet 2" and friends: the trailing number is the nesting level.
            if (style.Name.Contains("list bullet")) { paragraph.List = ListKind.Bullet; paragraph.Level = StyleLevel() ?? 0; }
            else if (style.Name.Contains("list number")) { paragraph.List = ListKind.Ordered; paragraph.Level = StyleLevel() ?? 0; }
            else if (style.Name is "quote" or "intense quote") paragraph.Quote = true;
            return paragraph;
        }

        /// <summary>Runs of a paragraph, with formatting from the run, falling back to the paragraph style.</summary>
        public List<Inline> Inlines(XElement p, Style style)
        {
            var pieces = new List<Inline>();
            void Collect(XElement container, string? link)
            {
                foreach (var node in container.Elements())
                {
                    switch (node.Name.LocalName)
                    {
                        case "r":
                            var rPr = node.Child("rPr");
                            bool bold = rPr.Child("b") is { } b ? IsOn(b) : style.Bold;
                            bool italic = rPr.Child("i") is { } i ? IsOn(i) : style.Italic;
                            bool underline = rPr.Child("u") is { } u ? Underlined(u) : style.Underline;
                            var text = new StringBuilder();
                            foreach (var part in node.Elements())
                            {
                                switch (part.Name.LocalName)
                                {
                                    case "t": text.Append(part.Value); break;
                                    case "tab": text.Append('\t'); break;
                                    case "br" when part.Attr("type") != "page":
                                    case "cr": text.Append('\n'); break;
                                    case "noBreakHyphen": text.Append('-'); break;
                                }
                            }
                            if (text.Length > 0) pieces.Add(new Inline(text.ToString(), bold, italic, underline, link));
                            break;
                        case "hyperlink":
                            var target = node.RelationshipId() is { } id && links.TryGetValue(id, out var rel) ? rel.Target
                                : node.Attr("anchor") is { } anchor ? "#" + anchor : null;
                            Collect(node, target);
                            break;
                        case "smartTag":
                        case "ins":
                        case "fldSimple":
                        case "sdt":
                        case "sdtContent":
                        case "customXml":
                            Collect(node, link);
                            break;
                    }
                }
            }
            Collect(p, null);
            return InlineList.Merge(pieces);
        }
    }
}

/// <summary>OpenDocument Text (.odt): headings, lists, tables and character styles from content.xml.</summary>
public static class OdtReader
{
    private sealed record CharStyle(bool Bold, bool Italic, bool Underline);

    public static Document Read(string path)
    {
        using var package = new OfficePackage(path);
        var content = package.Xml("content.xml") ?? throw ConversionException.Unreadable(path);
        var styles = new Dictionary<string, CharStyle>();
        var listStyles = new Dictionary<string, XElement>();
        foreach (var root in new[] { package.Xml("styles.xml"), content })
        {
            if (root == null) continue;
            foreach (var style in root.DescendantsNamed("style"))
            {
                var name = style.Attr("name");
                var props = style.Child("text-properties");
                if (name == null) continue;
                var weight = props.Attr("font-weight");
                styles[name] = new CharStyle(
                    weight is "bold" || (int.TryParse(weight, out var w) && w >= 600),
                    props.Attr("font-style") is "italic" or "oblique",
                    props.Attr("text-underline-style") is { } u && u != "none");
            }
            foreach (var list in root.DescendantsNamed("list-style"))
            {
                if (list.Attr("name") is { } name) listStyles[name] = list;
            }
        }

        var document = new Document();
        var text = content.Child("body").Child("text") ?? throw ConversionException.Unreadable(path);
        var counters = new Dictionary<int, int>();

        List<Inline> Inlines(XElement element, CharStyle style, string? link)
        {
            var result = new List<Inline>();
            foreach (var node in element.Nodes())
            {
                if (node is XText textNode)
                {
                    var value = Regex.Replace(textNode.Value, @"[\r\n\t ]+", " ");
                    result.Add(new Inline(value, style.Bold, style.Italic, style.Underline, link));
                    continue;
                }
                if (node is not XElement child) continue;
                switch (child.Name.LocalName)
                {
                    case "span":
                        var spanStyle = child.Attr("style-name") is { } s && styles.TryGetValue(s, out var found)
                            ? new CharStyle(style.Bold || found.Bold, style.Italic || found.Italic, style.Underline || found.Underline)
                            : style;
                        result.AddRange(Inlines(child, spanStyle, link));
                        break;
                    case "a":
                        result.AddRange(Inlines(child, style, child.Attr("href")));
                        break;
                    case "s":
                        result.Add(new Inline(new string(' ', child.AttrInt("c") ?? 1), style.Bold, style.Italic, style.Underline, link));
                        break;
                    case "tab":
                        result.Add(new Inline("\t", style.Bold, style.Italic, style.Underline, link));
                        break;
                    case "line-break":
                        result.Add(new Inline("\n"));
                        break;
                    case "note":
                    case "annotation":
                    case "bookmark":
                    case "bookmark-start":
                    case "bookmark-end":
                        break;
                    default:
                        result.AddRange(Inlines(child, style, link));
                        break;
                }
            }
            return result;
        }

        CharStyle ParagraphStyle(XElement element) =>
            element.Attr("style-name") is { } name && styles.TryGetValue(name, out var style) ? style : new CharStyle(false, false, false);

        bool IsOrdered(XElement? listStyle, int level)
        {
            var entry = listStyle?.Elements().FirstOrDefault(e => e.AttrInt("level") == level + 1);
            return entry?.Name.LocalName == "list-level-style-number";
        }

        void Walk(XElement container, List<Block> blocks, int listLevel, XElement? listStyle)
        {
            foreach (var element in container.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "h":
                        var heading = new Paragraph { Heading = Math.Clamp(element.AttrInt("outline-level") ?? 1, 1, 6) };
                        heading.Inlines.AddRange(InlineList.Trim(Inlines(element, ParagraphStyle(element), null)));
                        if (!heading.IsEmpty) blocks.Add(heading);
                        break;
                    case "p":
                        var paragraph = new Paragraph();
                        paragraph.Inlines.AddRange(InlineList.Trim(Inlines(element, ParagraphStyle(element), null)));
                        if (!paragraph.IsEmpty) blocks.Add(paragraph);
                        break;
                    case "list":
                        var style = element.Attr("style-name") is { } styleName && listStyles.TryGetValue(styleName, out var ls) ? ls : listStyle;
                        int level = listLevel + 1;
                        bool ordered = IsOrdered(style, level);
                        counters[level] = 0;
                        foreach (var item in element.Children("list-item").Concat(element.Children("list-header")))
                        {
                            bool firstParagraph = true;
                            foreach (var child in item.Elements())
                            {
                                if (child.Name.LocalName == "list")
                                {
                                    Walk(new XElement("wrapper", child), blocks, level, style);
                                    continue;
                                }
                                var inner = new List<Block>();
                                Walk(new XElement("wrapper", child), inner, level, style);
                                foreach (var block in inner)
                                {
                                    if (block is Paragraph p && firstParagraph && p.Heading == 0)
                                    {
                                        p.List = ordered ? ListKind.Ordered : ListKind.Bullet;
                                        p.Level = level;
                                        if (ordered) p.Number = counters[level] = counters.GetValueOrDefault(level) + 1;
                                        firstParagraph = false;
                                    }
                                    blocks.Add(block);
                                }
                            }
                        }
                        break;
                    case "table":
                        var table = new Table();
                        foreach (var row in element.DescendantsNamed("table-row"))
                        {
                            var cells = new List<List<Inline>>();
                            foreach (var cell in row.Elements().Where(e => e.Name.LocalName is "table-cell" or "covered-table-cell"))
                            {
                                var content = new List<Inline>();
                                foreach (var p in cell.Elements().Where(e => e.Name.LocalName is "p" or "h"))
                                {
                                    if (content.Count > 0) content.Add(new Inline("\n"));
                                    content.AddRange(Inlines(p, ParagraphStyle(p), null));
                                }
                                int repeat = Math.Min(cell.AttrInt("number-columns-repeated") ?? 1, 50);
                                for (int i = 0; i < repeat; i++) cells.Add(InlineList.Trim(content));
                            }
                            // Trailing empty cells are usually just padding.
                            while (cells.Count > 0 && cells[^1].Count == 0) cells.RemoveAt(cells.Count - 1);
                            if (cells.Count > 0) table.Rows.Add(cells);
                        }
                        if (table.Rows.Count > 0) blocks.Add(table);
                        break;
                    case "section":
                    case "index-body":
                    case "table-of-content":
                    case "wrapper":
                        Walk(element, blocks, listLevel, listStyle);
                        break;
                }
            }
        }

        Walk(text, document.Blocks, -1, null);
        return document;
    }
}

/// <summary>Web pages: headings, paragraphs, lists, tables, quotes, code and inline formatting.</summary>
public static class HtmlReader
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "noscript", "template", "svg", "iframe", "object", "canvas", "button", "select", "input", "textarea",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "main", "header", "footer", "aside", "nav", "figure", "figcaption", "address",
        "dl", "dt", "dd", "form", "fieldset", "center", "details", "summary", "body", "html", "hr",
    };

    public static Document Read(string path)
    {
        var html = new HtmlDocument();
        html.LoadHtml(TextFiles.Read(path));
        var document = new Document();
        var walker = new Walker(document);
        walker.Walk(html.DocumentNode.SelectSingleNode("//body") ?? html.DocumentNode, new Inline(""), 0);
        walker.Flush();
        return document;
    }

    private sealed class Walker
    {
        private readonly Document document;
        private List<Inline> current = new();
        private Paragraph? template;
        private bool quote;
        private readonly Stack<(ListKind Kind, int Count)> lists = new();

        public Walker(Document document) { this.document = document; }

        public void Flush()
        {
            var inlines = InlineList.Trim(current.Select(i => i with { Text = i.Text }).ToList());
            current = new List<Inline>();
            var paragraph = template ?? new Paragraph();
            template = null;
            if (inlines.Count == 0) return;
            paragraph.Inlines.AddRange(inlines);
            paragraph.Quote |= quote;
            document.Blocks.Add(paragraph);
        }

        public void Walk(HtmlNode node, Inline style, int depth)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Text)
                {
                    var text = HtmlEntity.DeEntitize(child.InnerText);
                    text = Regex.Replace(text, @"\s+", " ");
                    if (text.Length > 0) current.Add(style with { Text = text });
                    continue;
                }
                if (child.NodeType != HtmlNodeType.Element) continue;
                var name = child.Name.ToLowerInvariant();
                if (Skipped.Contains(name)) continue;

                switch (name)
                {
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        Flush();
                        template = new Paragraph { Heading = name[1] - '0' };
                        Walk(child, style, depth + 1);
                        Flush();
                        break;
                    case "br":
                        current.Add(new Inline("\n"));
                        break;
                    case "pre":
                        Flush();
                        var pre = new Paragraph { Preformatted = true };
                        pre.Inlines.Add(new Inline(HtmlEntity.DeEntitize(child.InnerText).Trim('\n', '\r'), Code: true));
                        document.Blocks.Add(pre);
                        break;
                    case "blockquote":
                        Flush();
                        var wasQuote = quote;
                        quote = true;
                        Walk(child, style, depth + 1);
                        Flush();
                        quote = wasQuote;
                        break;
                    case "ul": case "ol":
                        Flush();
                        lists.Push((name == "ol" ? ListKind.Ordered : ListKind.Bullet, 0));
                        Walk(child, style, depth + 1);
                        Flush();
                        lists.Pop();
                        break;
                    case "li":
                        Flush();
                        var (kind, count) = lists.Count > 0 ? lists.Pop() : (ListKind.Bullet, 0);
                        count++;
                        lists.Push((kind, count));
                        template = new Paragraph { List = kind, Level = Math.Max(0, lists.Count - 1), Number = count };
                        Walk(child, style, depth + 1);
                        Flush();
                        break;
                    case "table":
                        Flush();
                        var table = new Table();
                        foreach (var row in child.Descendants("tr"))
                        {
                            // Skip rows that belong to a nested table.
                            if (row.Ancestors("table").FirstOrDefault() != child) continue;
                            var cells = new List<List<Inline>>();
                            foreach (var cell in row.ChildNodes.Where(n => n.Name is "td" or "th"))
                            {
                                var cellWalker = new Walker(new Document());
                                cellWalker.Walk(cell, new Inline(""), depth + 1);
                                cellWalker.Flush();
                                var content = new List<Inline>();
                                foreach (var block in cellWalker.document.Blocks.OfType<Paragraph>())
                                {
                                    if (content.Count > 0) content.Add(new Inline("\n"));
                                    content.AddRange(block.Inlines);
                                }
                                cells.Add(content);
                            }
                            if (cells.Count > 0) table.Rows.Add(cells);
                        }
                        if (table.Rows.Count > 0) document.Blocks.Add(table);
                        break;
                    case "b": case "strong":
                        Walk(child, style with { Bold = true }, depth + 1);
                        break;
                    case "i": case "em": case "cite": case "dfn":
                        Walk(child, style with { Italic = true }, depth + 1);
                        break;
                    case "u": case "ins":
                        Walk(child, style with { Underline = true }, depth + 1);
                        break;
                    case "code": case "kbd": case "samp": case "tt":
                        Walk(child, style with { Code = true }, depth + 1);
                        break;
                    case "a":
                        var href = child.GetAttributeValue("href", "");
                        Walk(child, href.Length > 0 && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                            ? style with { Link = HtmlEntity.DeEntitize(href) } : style, depth + 1);
                        break;
                    case "img":
                        var alt = child.GetAttributeValue("alt", "");
                        if (alt.Length > 0) current.Add(style with { Text = alt });
                        break;
                    default:
                        if (BlockTags.Contains(name))
                        {
                            Flush();
                            Walk(child, style, depth + 1);
                            Flush();
                        }
                        else
                        {
                            Walk(child, style, depth + 1);
                        }
                        break;
                }
            }
        }
    }
}

/// <summary>Plain text and Markdown.</summary>
public static class PlainTextReader
{
    public static Document ReadText(string path) => Document.FromPlainPages(new[] { TextFiles.Read(path) });

    /// <summary>A small Markdown reader: headings, lists, quotes, code blocks, tables, bold, italic, code and links.</summary>
    public static Document ReadMarkdown(string path)
    {
        var lines = TextFiles.Read(path).Replace("\r\n", "\n").Split('\n');
        var document = new Document();
        var paragraph = new List<string>();
        var counters = new Dictionary<int, int>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var block = new Paragraph();
            block.Inlines.AddRange(ParseInline(JoinLines(paragraph)));
            document.Blocks.Add(block);
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) { FlushParagraph(); counters.Clear(); continue; }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushParagraph();
                var fence = trimmed.Substring(0, 3);
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(fence); i++) code.Add(lines[i]);
                var pre = new Paragraph { Preformatted = true };
                pre.Inlines.Add(new Inline(string.Join("\n", code), Code: true));
                document.Blocks.Add(pre);
                continue;
            }
            var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.*?)\s*#*$");
            if (heading.Success)
            {
                FlushParagraph();
                var block = new Paragraph { Heading = heading.Groups[1].Length };
                block.Inlines.AddRange(ParseInline(heading.Groups[2].Value));
                document.Blocks.Add(block);
                continue;
            }
            if (Regex.IsMatch(trimmed, @"^([-*_])(\s*\1){2,}$"))
            {
                FlushParagraph();
                continue;
            }
            // Tables: a header row followed by a |---|---| separator.
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}"))
            {
                FlushParagraph();
                var table = new Table();
                table.Rows.Add(Cells(trimmed));
                for (i += 2; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) table.Rows.Add(Cells(lines[i].Trim()));
                i--;
                document.Blocks.Add(table);
                continue;
            }
            var item = Regex.Match(line, @"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$");
            if (item.Success)
            {
                FlushParagraph();
                int indent = item.Groups[1].Value.Replace("\t", "    ").Length;
                int level = Math.Min(8, indent / 2);
                bool ordered = char.IsDigit(item.Groups[2].Value[0]);
                var block = new Paragraph { List = ordered ? ListKind.Ordered : ListKind.Bullet, Level = level };
                if (ordered)
                {
                    block.Number = counters[level] = counters.GetValueOrDefault(level) + 1;
                    foreach (var deeper in counters.Keys.Where(k => k > level).ToList()) counters.Remove(deeper);
                }
                block.Inlines.AddRange(ParseInline(item.Groups[3].Value));
                document.Blocks.Add(block);
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++) quote.Add(lines[i].Trim().TrimStart('>').Trim());
                i--;
                var block = new Paragraph { Quote = true };
                block.Inlines.AddRange(ParseInline(JoinLines(quote)));
                document.Blocks.Add(block);
                continue;
            }
            paragraph.Add(trimmed);
        }
        FlushParagraph();
        return document;
    }

    /// <summary>Soft line breaks become spaces, except between CJK characters.</summary>
    private static string JoinLines(List<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0 && !(PdfFonts.IsCjk(builder[^1]) && line.Length > 0 && PdfFonts.IsCjk(line[0]))) builder.Append(' ');
            builder.Append(line);
        }
        return builder.ToString();
    }

    private static List<List<Inline>> Cells(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|')) inner = inner.Substring(1);
        if (inner.EndsWith('|') && !inner.EndsWith("\\|")) inner = inner.Substring(0, inner.Length - 1);
        return Regex.Split(inner, @"(?<!\\)\|").Select(cell => ParseInline(cell.Trim().Replace("\\|", "|"))).ToList();
    }

    /// <summary>**bold**, *italic*, `code`, [text](url) and backslash escapes.</summary>
    public static List<Inline> ParseInline(string text)
    {
        var result = new List<Inline>();
        bool bold = false, italic = false;
        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length > 0) result.Add(new Inline(buffer.ToString(), bold, italic));
            buffer.Clear();
        }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_{}[]()#+-.!|<>".Contains(text[i + 1]))
            {
                buffer.Append(text[++i]);
                continue;
            }
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    result.Add(new Inline(text.Substring(i + 1, end - i - 1), bold, italic, Code: true));
                    i = end;
                    continue;
                }
            }
            if (c == '[')
            {
                var link = Regex.Match(text.Substring(i), @"^\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)");
                if (link.Success)
                {
                    Flush();
                    foreach (var inner in ParseInline(link.Groups[1].Value))
                        result.Add(inner with { Bold = inner.Bold || bold, Italic = inner.Italic || italic, Link = link.Groups[2].Value });
                    i += link.Length - 1;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                // Only toggle on when a closing marker exists.
                if (bold || text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal) > 0)
                {
                    Flush();
                    bold = !bold;
                    i++;
                    continue;
                }
            }
            if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) || italic)))
            {
                if (italic || text.IndexOf(c, i + 1) > 0)
                {
                    Flush();
                    italic = !italic;
                    continue;
                }
            }
            buffer.Append(c);
        }
        Flush();
        return result;
    }
}

/// <summary>
/// Rich Text Format, read with WPF's RTF reader (the same one WordPad-style editors in .NET use),
/// then mapped to the document model. Headings are guessed from font sizes, like the Mac app does.
/// </summary>
public static class RtfReader
{
    public static Document Read(string path) => Sta.Run(() =>
    {
        var flow = new W.FlowDocument();
        var range = new W.TextRange(flow.ContentStart, flow.ContentEnd);
        try
        {
            using var stream = File.OpenRead(path);
            range.Load(stream, System.Windows.DataFormats.Rtf);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException)
        {
            throw ConversionException.Unreadable(path);
        }

        var sizes = new Dictionary<double, int>();
        foreach (var run in Descendants<W.Run>(flow.Blocks))
        {
            sizes[run.FontSize] = sizes.GetValueOrDefault(run.FontSize) + run.Text.Length;
        }
        double bodySize = sizes.Count == 0 ? 12 : sizes.MaxBy(s => s.Value).Key;
        var document = new Document();
        Blocks(flow.Blocks, document.Blocks, bodySize, -1);
        return document;
    });

    private static IEnumerable<T> Descendants<T>(IEnumerable<W.Block> blocks) where T : W.TextElement
    {
        foreach (var block in blocks)
        {
            foreach (var found in All<T>(block)) yield return found;
        }
    }

    private static IEnumerable<T> All<T>(System.Windows.DependencyObject root) where T : W.TextElement
    {
        if (root is T match) yield return match;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            foreach (var found in All<T>(child)) yield return found;
        }
    }

    private static void Blocks(IEnumerable<W.Block> blocks, List<Block> output, double bodySize, int listLevel)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case W.Paragraph p:
                    var paragraph = new Paragraph();
                    paragraph.Inlines.AddRange(InlineList.Trim(Inlines(p.Inlines, null)));
                    if (paragraph.IsEmpty) break;
                    // Some writers put the bullet or number into the text instead of a list.
                    var literal = Regex.Match(paragraph.PlainText, @"^\s*([•◦▪‣·–-]|\d+[.)])\s+");
                    if (literal.Success && paragraph.Inlines[0].Text.Length >= literal.Length)
                    {
                        paragraph.List = char.IsDigit(literal.Groups[1].Value[0]) ? ListKind.Ordered : ListKind.Bullet;
                        if (paragraph.List == ListKind.Ordered) paragraph.Number = int.TryParse(literal.Groups[1].Value.TrimEnd('.', ')'), out var n) ? n : 1;
                        paragraph.Inlines[0] = paragraph.Inlines[0] with { Text = paragraph.Inlines[0].Text.Substring(literal.Length) };
                    }
                    else if (HeadingLevel(p, bodySize) is int level)
                    {
                        paragraph.Heading = level;
                    }
                    output.Add(paragraph);
                    break;

                case W.List list:
                    bool ordered = list.MarkerStyle is System.Windows.TextMarkerStyle.Decimal or System.Windows.TextMarkerStyle.LowerLatin
                        or System.Windows.TextMarkerStyle.UpperLatin or System.Windows.TextMarkerStyle.LowerRoman or System.Windows.TextMarkerStyle.UpperRoman;
                    int number = list.StartIndex;
                    foreach (var item in list.ListItems)
                    {
                        var inner = new List<Block>();
                        Blocks(item.Blocks, inner, bodySize, listLevel + 1);
                        bool first = true;
                        foreach (var child in inner)
                        {
                            if (first && child is Paragraph { List: ListKind.None } itemParagraph)
                            {
                                itemParagraph.List = ordered ? ListKind.Ordered : ListKind.Bullet;
                                itemParagraph.Level = listLevel + 1;
                                itemParagraph.Number = number;
                                itemParagraph.Heading = 0;
                                first = false;
                            }
                            output.Add(child);
                        }
                        number++;
                    }
                    break;

                case W.Table t:
                    var table = new Table();
                    foreach (var group in t.RowGroups)
                    {
                        foreach (var row in group.Rows)
                        {
                            var cells = new List<List<Inline>>();
                            foreach (var cell in row.Cells)
                            {
                                var inner = new List<Block>();
                                Blocks(cell.Blocks, inner, bodySize, listLevel);
                                var content = new List<Inline>();
                                foreach (var paragraphBlock in inner.OfType<Paragraph>())
                                {
                                    if (content.Count > 0) content.Add(new Inline("\n"));
                                    content.AddRange(paragraphBlock.Inlines);
                                }
                                cells.Add(content);
                            }
                            table.Rows.Add(cells);
                        }
                    }
                    if (table.Rows.Count > 0) output.Add(table);
                    break;

                case W.Section section:
                    Blocks(section.Blocks, output, bodySize, listLevel);
                    break;
            }
        }
    }

    private static List<Inline> Inlines(IEnumerable<W.Inline> inlines, string? link)
    {
        var result = new List<Inline>();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case W.Run run:
                    result.Add(new Inline(run.Text,
                        Bold: run.FontWeight.ToOpenTypeWeight() >= 600,
                        Italic: run.FontStyle != System.Windows.FontStyles.Normal,
                        Underline: Underlined(run),
                        Link: link));
                    break;
                case W.LineBreak:
                    result.Add(new Inline("\n"));
                    break;
                case W.Hyperlink hyperlink:
                    result.AddRange(Inlines(hyperlink.Inlines, hyperlink.NavigateUri?.ToString() ?? link));
                    break;
                case W.Span span:
                    result.AddRange(Inlines(span.Inlines, link));
                    break;
            }
        }
        return result;
    }

    private static bool Underlined(W.Run run)
    {
        System.Windows.DependencyObject? element = run;
        while (element is W.Inline inline)
        {
            if (inline.TextDecorations?.Any(d => d.Location == System.Windows.TextDecorationLocation.Underline) == true) return true;
            element = inline.Parent;
        }
        return false;
    }

    private static int? HeadingLevel(W.Paragraph paragraph, double bodySize)
    {
        var runs = All<W.Run>(paragraph).Where(r => r.Text.Trim().Length > 0).ToList();
        int length = runs.Sum(r => r.Text.Length);
        if (length == 0 || length >= 200) return null;
        double largest = runs.Max(r => r.FontSize);
        bool allBold = runs.All(r => r.FontWeight.ToOpenTypeWeight() >= 600);
        // Word's defaults: Heading 1 is 16 pt, Heading 2 13 pt, Heading 3 12 pt bold, over 11 pt body text.
        if (largest >= bodySize * 1.4) return 1;
        if (largest >= bodySize * 1.15) return 2;
        if (largest >= bodySize * 1.05 && allBold) return 3;
        return null;
    }
}
