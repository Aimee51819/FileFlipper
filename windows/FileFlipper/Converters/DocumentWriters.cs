using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace FileFlipper.Converters;

/// <summary>Word (.docx): a minimal but complete WordprocessingML package with real heading, list and table styles.</summary>
public static class DocxWriter
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Write(Document document, string output)
    {
        var links = new List<string>();
        var body = new StringBuilder();
        // Each ordered list gets its own numbering instance so it starts again at 1.
        var orderedInstances = 0;
        int currentOrdered = 0;
        bool previousWasOrdered = false;

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    var props = new StringBuilder();
                    if (p.Heading > 0) props.Append($"<w:pStyle w:val=\"Heading{Math.Clamp(p.Heading, 1, 6)}\"/>");
                    else if (p.Quote) props.Append("<w:pStyle w:val=\"Quote\"/>");
                    else if (p.Preformatted) props.Append("<w:pStyle w:val=\"Code\"/>");
                    else if (p.List != ListKind.None) props.Append("<w:pStyle w:val=\"ListParagraph\"/>");
                    if (p.List != ListKind.None)
                    {
                        int numId;
                        if (p.List == ListKind.Ordered)
                        {
                            if (!previousWasOrdered) currentOrdered = 2 + orderedInstances++;
                            numId = currentOrdered;
                        }
                        else numId = 1;
                        props.Append($"<w:numPr><w:ilvl w:val=\"{Math.Clamp(p.Level, 0, 8)}\"/><w:numId w:val=\"{numId}\"/></w:numPr>");
                    }
                    previousWasOrdered = p.List == ListKind.Ordered || (previousWasOrdered && p.List == ListKind.Bullet);
                    body.Append("<w:p>");
                    if (props.Length > 0) body.Append("<w:pPr>").Append(props).Append("</w:pPr>");
                    body.Append(Runs(p.Inlines, links));
                    body.Append("</w:p>");
                    break;

                case Table t when t.Rows.Count > 0:
                    previousWasOrdered = false;
                    int columns = t.Rows.Max(r => r.Count);
                    int columnWidth = 9000 / Math.Max(1, columns);
                    body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/></w:tblPr><w:tblGrid>");
                    for (int c = 0; c < columns; c++) body.Append($"<w:gridCol w:w=\"{columnWidth}\"/>");
                    body.Append("</w:tblGrid>");
                    foreach (var (row, rowIndex) in t.Rows.Select((r, i) => (r, i)))
                    {
                        body.Append("<w:tr>");
                        for (int c = 0; c < columns; c++)
                        {
                            body.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{columnWidth}\" w:type=\"dxa\"/></w:tcPr>");
                            var cell = c < row.Count ? row[c] : new List<Inline>();
                            foreach (var line in SplitLines(cell))
                            {
                                var styled = rowIndex == 0 ? line.Select(i => i with { Bold = true }).ToList() : line;
                                body.Append("<w:p>").Append(Runs(styled, links)).Append("</w:p>");
                            }
                            body.Append("</w:tc>");
                        }
                        body.Append("</w:tr>");
                    }
                    body.Append("</w:tbl><w:p/>");
                    break;

                case PageBreak:
                    previousWasOrdered = false;
                    body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                    break;
            }
        }
        body.Append("<w:sectPr>").Append(PageSize()).Append("<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>");

        var documentXml = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body>{body}</w:body></w:document>";

        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        rels.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        rels.Append("<Relationship Id=\"rIdNumbering\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
        for (int i = 0; i < links.Count; i++)
        {
            rels.Append($"<Relationship Id=\"rIdLink{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{Xml(links[i])}\" TargetMode=\"External\"/>");
        }
        rels.Append("</Relationships>");

        using var file = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", ContentTypes);
        Add(zip, "_rels/.rels", RootRels);
        Add(zip, "word/document.xml", documentXml);
        Add(zip, "word/_rels/document.xml.rels", rels.ToString());
        Add(zip, "word/styles.xml", Styles);
        Add(zip, "word/numbering.xml", Numbering(orderedInstances));
    }

    private static string PageSize() => RegionInfo.CurrentRegion.IsMetric
        ? "<w:pgSz w:w=\"11906\" w:h=\"16838\"/>"     // A4
        : "<w:pgSz w:w=\"12240\" w:h=\"15840\"/>";    // Letter

    public static IEnumerable<List<Inline>> SplitLines(List<Inline> inlines)
    {
        var line = new List<Inline>();
        foreach (var inline in inlines)
        {
            var parts = inline.Text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) { yield return line; line = new List<Inline>(); }
                if (parts[i].Length > 0) line.Add(inline with { Text = parts[i] });
            }
        }
        yield return line;
    }

    private static string Runs(List<Inline> inlines, List<string> links)
    {
        var builder = new StringBuilder();
        foreach (var inline in InlineList.Merge(inlines))
        {
            var run = new StringBuilder("<w:r>");
            var props = new StringBuilder();
            if (inline.Link != null) props.Append("<w:rStyle w:val=\"Hyperlink\"/>");
            if (inline.Code) props.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/>");
            if (inline.Bold) props.Append("<w:b/>");
            if (inline.Italic) props.Append("<w:i/>");
            if (inline.Underline) props.Append("<w:u w:val=\"single\"/>");
            if (props.Length > 0) run.Append("<w:rPr>").Append(props).Append("</w:rPr>");
            var lines = inline.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) run.Append("<w:br/>");
                var tabs = lines[i].Split('\t');
                for (int j = 0; j < tabs.Length; j++)
                {
                    if (j > 0) run.Append("<w:tab/>");
                    if (tabs[j].Length > 0) run.Append("<w:t xml:space=\"preserve\">").Append(Xml(tabs[j])).Append("</w:t>");
                }
            }
            run.Append("</w:r>");
            if (inline.Link != null && !inline.Link.StartsWith('#'))
            {
                links.Add(inline.Link);
                builder.Append($"<w:hyperlink r:id=\"rIdLink{links.Count}\">").Append(run).Append("</w:hyperlink>");
            }
            else
            {
                builder.Append(run);
            }
        }
        return builder.ToString();
    }

    public static string Xml(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            // Control characters are not allowed in XML 1.0.
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
            builder.Append(c switch { '<' => "&lt;", '>' => "&gt;", '&' => "&amp;", '"' => "&quot;", _ => c.ToString() });
        }
        return builder.ToString();
    }

    public static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private const string ContentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
        "</Types>";

    private const string RootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";

    private static string Styles
    {
        get
        {
            var headings = new StringBuilder();
            int[] sizes = { 32, 26, 24, 22, 22, 22 };   // half-points
            for (int level = 1; level <= 6; level++)
            {
                headings.Append($"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/>" +
                    $"<w:pPr><w:keepNext/><w:spacing w:before=\"{(level <= 2 ? 360 : 240)}\" w:after=\"120\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
                    $"<w:rPr><w:b/><w:color w:val=\"{(level <= 2 ? "1F3864" : "2F5496")}\"/><w:sz w:val=\"{sizes[level - 1]}\"/><w:szCs w:val=\"{sizes[level - 1]}\"/></w:rPr></w:style>");
            }
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                $"<w:styles xmlns:w=\"{W}\">" +
                "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Microsoft YaHei\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\" w:eastAsia=\"zh-CN\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
                headings +
                "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:ind w:left=\"720\"/></w:pPr><w:rPr><w:i/><w:color w:val=\"595959\"/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"0\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/><w:sz w:val=\"20\"/></w:rPr></w:style>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:spacing w:after=\"40\"/><w:contextualSpacing/></w:pPr></w:style>" +
                "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/></w:style>" +
                "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
                "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
                "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:basedOn w:val=\"TableNormal\"/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:tblPr><w:tblBorders>" +
                "<w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
                "</w:tblBorders></w:tblPr></w:style>" +
                "</w:styles>";
        }
    }

    private static string Numbering(int orderedInstances)
    {
        var builder = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering xmlns:w=\"{W}\">");
        string[] bullets = { "•", "◦", "▪" };
        builder.Append("<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
        for (int level = 0; level < 9; level++)
        {
            builder.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"{bullets[level % 3]}\"/><w:lvlJc w:val=\"left\"/>" +
                           $"<w:pPr><w:ind w:left=\"{720 + level * 360}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
        }
        builder.Append("</w:abstractNum>");
        string[] formats = { "decimal", "lowerLetter", "lowerRoman" };
        builder.Append("<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
        for (int level = 0; level < 9; level++)
        {
            builder.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{formats[level % 3]}\"/><w:lvlText w:val=\"%{level + 1}.\"/><w:lvlJc w:val=\"left\"/>" +
                           $"<w:pPr><w:ind w:left=\"{720 + level * 360}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
        }
        builder.Append("</w:abstractNum>");
        builder.Append("<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
        for (int i = 0; i < orderedInstances; i++)
        {
            builder.Append($"<w:num w:numId=\"{i + 2}\"><w:abstractNumId w:val=\"1\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride></w:num>");
        }
        builder.Append("</w:numbering>");
        return builder.ToString();
    }
}

/// <summary>Rich Text Format, readable by Word, WordPad and nearly every word processor.</summary>
public static class RtfWriter
{
    public static void Write(Document document, string output)
    {
        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1");
        rtf.Append(@"{\fonttbl{\f0\fswiss\fcharset0 Calibri;}{\f1\fnil\fcharset134 Microsoft YaHei;}{\f2\fmodern\fcharset0 Consolas;}}");
        rtf.Append(@"{\colortbl;\red5\green99\blue193;\red89\green89\blue89;\red31\green56\blue100;}");
        rtf.Append(RegionInfo.CurrentRegion.IsMetric ? @"\paperw11906\paperh16838" : @"\paperw12240\paperh15840");
        rtf.Append(@"\margl1440\margr1440\margt1440\margb1440\viewkind4" + "\n");
        int[] headingSizes = { 32, 26, 24, 22, 22, 22 };

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    rtf.Append(@"\pard\plain\f0\fs22\sa120\sl276\slmult1 ");
                    if (p.Heading > 0)
                    {
                        int level = Math.Clamp(p.Heading, 1, 6);
                        rtf.Append($@"\outlinelevel{level - 1}\sb{(level <= 2 ? 360 : 240)}\keepn\b\cf3\fs{headingSizes[level - 1]} ");
                    }
                    else if (p.List != ListKind.None)
                    {
                        int left = 720 + p.Level * 360;
                        var marker = p.List == ListKind.Ordered ? p.Number + "." : (p.Level % 2 == 0 ? @"\bullet" : "-");
                        rtf.Append($@"\fi-360\li{left}\sa40 {marker}\tab ");
                    }
                    else if (p.Quote) rtf.Append(@"\li720\i\cf2 ");
                    else if (p.Preformatted) rtf.Append(@"\f2\fs20\sa0 ");
                    rtf.Append(Runs(p.Inlines));
                    rtf.Append(@"\par" + "\n");
                    break;

                case Table t when t.Rows.Count > 0:
                    int columns = t.Rows.Max(r => r.Count);
                    int width = 9000 / Math.Max(1, columns);
                    foreach (var (row, rowIndex) in t.Rows.Select((r, i) => (r, i)))
                    {
                        rtf.Append(@"\trowd\trgaph108\trleft0");
                        for (int c = 1; c <= columns; c++)
                        {
                            rtf.Append(@"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10");
                            rtf.Append($@"\cellx{c * width}");
                        }
                        for (int c = 0; c < columns; c++)
                        {
                            var cell = c < row.Count ? row[c] : new List<Inline>();
                            if (rowIndex == 0) cell = cell.Select(i => i with { Bold = true }).ToList();
                            rtf.Append(@"\pard\intbl\plain\f0\fs20 ").Append(Runs(cell)).Append(@"\cell ");
                        }
                        rtf.Append(@"\row" + "\n");
                    }
                    rtf.Append(@"\pard\plain\f0\fs22\par" + "\n");
                    break;

                case PageBreak:
                    rtf.Append(@"\page" + "\n");
                    break;
            }
        }
        rtf.Append('}');
        File.WriteAllText(output, rtf.ToString(), Encoding.ASCII);
    }

    private static string Runs(List<Inline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in InlineList.Merge(inlines))
        {
            var text = Escape(inline.Text);
            var format = new StringBuilder();
            if (inline.Bold) format.Append(@"\b");
            if (inline.Italic) format.Append(@"\i");
            if (inline.Underline) format.Append(@"\ul");
            if (inline.Code) format.Append(@"\f2");
            if (inline.Link != null)
            {
                builder.Append(@"{\field{\*\fldinst{HYPERLINK """).Append(Escape(inline.Link)).Append(@"""}}{\fldrslt{\ul\cf1")
                       .Append(format).Append(' ').Append(text).Append("}}}");
            }
            else if (format.Length > 0)
            {
                builder.Append('{').Append(format).Append(' ').Append(text).Append('}');
            }
            else
            {
                builder.Append(text);
            }
        }
        return builder.ToString();
    }

    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '{': builder.Append(@"\{"); break;
                case '}': builder.Append(@"\}"); break;
                case '\n': builder.Append(@"\line "); break;
                case '\t': builder.Append(@"\tab "); break;
                case '\r': break;
                default:
                    if (c < 0x20) break;
                    if (c < 0x80) builder.Append(c);
                    else builder.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
        return builder.ToString();
    }
}

/// <summary>OpenDocument Text (.odt) for LibreOffice and friends.</summary>
public static class OdtWriter
{
    public static void Write(Document document, string output)
    {
        var body = new StringBuilder();
        var openLists = new Stack<ListKind>();

        void CloseLists(int depth)
        {
            while (openLists.Count > depth)
            {
                openLists.Pop();
                body.Append("</text:list-item></text:list>");
            }
        }

        foreach (var block in document.Blocks)
        {
            if (block is Paragraph { List: not ListKind.None } item)
            {
                int depth = item.Level + 1;
                if (openLists.Count >= depth)
                {
                    CloseLists(depth);
                    if (openLists.Count == depth && openLists.Peek() != item.List)
                    {
                        CloseLists(depth - 1);
                    }
                    else if (openLists.Count == depth)
                    {
                        body.Append("</text:list-item><text:list-item>");
                    }
                }
                while (openLists.Count < depth)
                {
                    var style = item.List == ListKind.Ordered ? "LNumber" : "LBullet";
                    body.Append(openLists.Count == 0 ? $"<text:list text:style-name=\"{style}\">" : "<text:list>");
                    body.Append("<text:list-item>");
                    openLists.Push(item.List);
                }
                body.Append("<text:p text:style-name=\"ListText\">").Append(Spans(item.Inlines)).Append("</text:p>");
                continue;
            }
            CloseLists(0);
            switch (block)
            {
                case Paragraph p when p.Heading > 0:
                    int level = Math.Clamp(p.Heading, 1, 6);
                    body.Append($"<text:h text:style-name=\"Heading_20_{level}\" text:outline-level=\"{level}\">").Append(Spans(p.Inlines)).Append("</text:h>");
                    break;
                case Paragraph p:
                    var styleName = p.Quote ? "Quotations" : p.Preformatted ? "Preformatted_20_Text" : "Text_20_body";
                    body.Append($"<text:p text:style-name=\"{styleName}\">").Append(Spans(p.Inlines)).Append("</text:p>");
                    break;
                case Table t when t.Rows.Count > 0:
                    int columns = t.Rows.Max(r => r.Count);
                    body.Append($"<table:table table:style-name=\"Tbl\"><table:table-column table:number-columns-repeated=\"{columns}\"/>");
                    foreach (var (row, rowIndex) in t.Rows.Select((r, i) => (r, i)))
                    {
                        body.Append("<table:table-row>");
                        for (int c = 0; c < columns; c++)
                        {
                            var cell = c < row.Count ? row[c] : new List<Inline>();
                            body.Append("<table:table-cell table:style-name=\"Cell\" office:value-type=\"string\">");
                            foreach (var line in DocxWriter.SplitLines(cell))
                            {
                                var styled = rowIndex == 0 ? line.Select(i => i with { Bold = true }).ToList() : line;
                                body.Append("<text:p text:style-name=\"Table_20_Contents\">").Append(Spans(styled)).Append("</text:p>");
                            }
                            body.Append("</table:table-cell>");
                        }
                        body.Append("</table:table-row>");
                    }
                    body.Append("</table:table>");
                    break;
                case PageBreak:
                    body.Append("<text:p text:style-name=\"PageBreak\"/>");
                    break;
            }
        }
        CloseLists(0);

        const string ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
            "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
            "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
            "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" office:version=\"1.2\"";

        var content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<office:document-content {ns}>" +
            "<office:automatic-styles>" +
            "<style:style style:name=\"TB\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" style:font-weight-asian=\"bold\"/></style:style>" +
            "<style:style style:name=\"TI\" style:family=\"text\"><style:text-properties fo:font-style=\"italic\" style:font-style-asian=\"italic\"/></style:style>" +
            "<style:style style:name=\"TBI\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" fo:font-style=\"italic\" style:font-weight-asian=\"bold\" style:font-style-asian=\"italic\"/></style:style>" +
            "<style:style style:name=\"TU\" style:family=\"text\"><style:text-properties style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"/></style:style>" +
            "<style:style style:name=\"TC\" style:family=\"text\"><style:text-properties style:font-name=\"Consolas\" fo:font-family=\"Consolas\"/></style:style>" +
            "<style:style style:name=\"PageBreak\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"><style:paragraph-properties fo:break-after=\"page\"/></style:style>" +
            "<style:style style:name=\"Tbl\" style:family=\"table\"><style:table-properties style:width=\"16cm\" table:align=\"margins\"/></style:style>" +
            "<style:style style:name=\"Cell\" style:family=\"table-cell\"><style:table-cell-properties fo:padding=\"0.1cm\" fo:border=\"0.5pt solid #bfbfbf\"/></style:style>" +
            "<text:list-style style:name=\"LBullet\">" + ListLevels(ordered: false) + "</text:list-style>" +
            "<text:list-style style:name=\"LNumber\">" + ListLevels(ordered: true) + "</text:list-style>" +
            "</office:automatic-styles>" +
            $"<office:body><office:text>{body}</office:text></office:body></office:document-content>";

        var styles = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<office:document-styles {ns}><office:styles>" +
            "<style:default-style style:family=\"paragraph\"><style:text-properties style:font-name=\"Calibri\" fo:font-family=\"Calibri\" fo:font-size=\"11pt\" style:font-name-asian=\"Microsoft YaHei\" style:font-family-asian=\"'Microsoft YaHei'\" style:font-size-asian=\"11pt\"/></style:default-style>" +
            "<style:style style:name=\"Standard\" style:family=\"paragraph\" style:class=\"text\"/>" +
            "<style:style style:name=\"Text_20_body\" style:display-name=\"Text body\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"text\"><style:paragraph-properties fo:margin-top=\"0cm\" fo:margin-bottom=\"0.21cm\" fo:line-height=\"115%\"/></style:style>" +
            "<style:style style:name=\"ListText\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"><style:paragraph-properties fo:margin-bottom=\"0.07cm\"/></style:style>" +
            "<style:style style:name=\"Quotations\" style:family=\"paragraph\" style:parent-style-name=\"Text_20_body\"><style:paragraph-properties fo:margin-left=\"1cm\"/><style:text-properties fo:font-style=\"italic\" fo:color=\"#595959\"/></style:style>" +
            "<style:style style:name=\"Preformatted_20_Text\" style:display-name=\"Preformatted Text\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"><style:paragraph-properties fo:background-color=\"#f2f2f2\"/><style:text-properties style:font-name=\"Consolas\" fo:font-family=\"Consolas\" fo:font-size=\"10pt\"/></style:style>" +
            "<style:style style:name=\"Table_20_Contents\" style:display-name=\"Table Contents\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"/>" +
            HeadingStyles() +
            "</office:styles></office:document-styles>";

        const string manifest = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">" +
            "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
            "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>" +
            "</manifest:manifest>";

        using var file = new FileStream(output, FileMode.CreateNew);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        // The media type must come first and be stored uncompressed.
        DocxWriter.Add(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
        DocxWriter.Add(zip, "content.xml", content);
        DocxWriter.Add(zip, "styles.xml", styles);
        DocxWriter.Add(zip, "META-INF/manifest.xml", manifest);
    }

    private static string HeadingStyles()
    {
        var builder = new StringBuilder("<style:style style:name=\"Heading\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:next-style-name=\"Text_20_body\" style:class=\"text\"><style:paragraph-properties fo:margin-top=\"0.42cm\" fo:margin-bottom=\"0.21cm\" fo:keep-with-next=\"always\"/><style:text-properties fo:color=\"#1f3864\" fo:font-weight=\"bold\" style:font-weight-asian=\"bold\"/></style:style>");
        string[] sizes = { "16pt", "13pt", "12pt", "11pt", "11pt", "11pt" };
        for (int level = 1; level <= 6; level++)
        {
            builder.Append($"<style:style style:name=\"Heading_20_{level}\" style:display-name=\"Heading {level}\" style:family=\"paragraph\" style:parent-style-name=\"Heading\" style:next-style-name=\"Text_20_body\" style:default-outline-level=\"{level}\" style:class=\"text\">" +
                           $"<style:text-properties fo:font-size=\"{sizes[level - 1]}\" style:font-size-asian=\"{sizes[level - 1]}\"/></style:style>");
        }
        return builder.ToString();
    }

    private static string ListLevels(bool ordered)
    {
        var builder = new StringBuilder();
        string[] bullets = { "•", "◦", "▪" };
        for (int level = 1; level <= 10; level++)
        {
            var position = $"<style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\"><style:list-level-label-alignment text:label-followed-by=\"listtab\" text:list-tab-stop-position=\"{0.635 * level + 0.635:0.###}cm\" fo:text-indent=\"-0.635cm\" fo:margin-left=\"{0.635 * level + 0.635:0.###}cm\"/></style:list-level-properties>";
            builder.Append(ordered
                ? $"<text:list-level-style-number text:level=\"{level}\" style:num-suffix=\".\" style:num-format=\"{(level % 3 == 1 ? "1" : level % 3 == 2 ? "a" : "i")}\">{position}</text:list-level-style-number>"
                : $"<text:list-level-style-bullet text:level=\"{level}\" text:bullet-char=\"{bullets[(level - 1) % 3]}\">{position}</text:list-level-style-bullet>");
        }
        return builder.ToString().Replace(",", ".");
    }

    private static string Spans(List<Inline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in InlineList.Merge(inlines))
        {
            var text = Text(inline.Text);
            string? style = inline.Code ? "TC" : inline.Bold && inline.Italic ? "TBI" : inline.Bold ? "TB" : inline.Italic ? "TI" : inline.Underline ? "TU" : null;
            if (style != null) text = $"<text:span text:style-name=\"{style}\">{text}</text:span>";
            if (inline.Link != null) text = $"<text:a xlink:type=\"simple\" xlink:href=\"{DocxWriter.Xml(inline.Link)}\">{text}</text:a>";
            builder.Append(text);
        }
        return builder.ToString();
    }

    /// <summary>Escapes text; runs of spaces, tabs and line breaks need their own elements in ODF.</summary>
    private static string Text(string text)
    {
        var builder = new StringBuilder();
        int spaces = 0;
        void FlushSpaces()
        {
            if (spaces == 0) return;
            builder.Append(' ');
            if (spaces > 1) builder.Append($"<text:s text:c=\"{spaces - 1}\"/>");
            spaces = 0;
        }
        foreach (var c in text)
        {
            if (c == ' ') { spaces++; continue; }
            FlushSpaces();
            if (c == '\t') builder.Append("<text:tab/>");
            else if (c == '\n') builder.Append("<text:line-break/>");
            else builder.Append(DocxWriter.Xml(c.ToString()));
        }
        FlushSpaces();
        return builder.ToString();
    }
}

/// <summary>A clean, self-contained HTML page.</summary>
public static class HtmlWriter
{
    public static void Write(Document document, string output, string title)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n");
        html.Append("<style>body{font-family:\"Segoe UI\",\"Microsoft YaHei\",-apple-system,sans-serif;line-height:1.6;max-width:46em;margin:2em auto;padding:0 1em;color:#222}" +
                    "table{border-collapse:collapse;margin:1em 0}td,th{border:1px solid #ccc;padding:.3em .6em;text-align:left}th{background:#f6f6f6}" +
                    "blockquote{margin:1em 0;padding-left:1em;border-left:3px solid #ddd;color:#555}pre{background:#f6f6f6;padding:.8em;overflow:auto}</style>\n");
        html.Append("</head>\n<body>\n");

        var openLists = new Stack<ListKind>();
        void CloseLists(int depth)
        {
            while (openLists.Count > depth)
            {
                html.Append(openLists.Pop() == ListKind.Ordered ? "</li></ol>\n" : "</li></ul>\n");
            }
        }

        foreach (var block in document.Blocks)
        {
            if (block is Paragraph { List: not ListKind.None } item)
            {
                int depth = item.Level + 1;
                if (openLists.Count >= depth)
                {
                    CloseLists(depth);
                    if (openLists.Peek() != item.List) CloseLists(depth - 1);
                    else html.Append("</li>\n");
                }
                while (openLists.Count < depth)
                {
                    html.Append(item.List == ListKind.Ordered ? "<ol>\n" : "<ul>\n");
                    openLists.Push(item.List);
                    if (openLists.Count < depth) html.Append("<li>");
                }
                html.Append("<li>").Append(Inlines(item.Inlines));
                continue;
            }
            CloseLists(0);
            switch (block)
            {
                case Paragraph p when p.IsEmpty:
                    break;
                case Paragraph p when p.Heading > 0:
                    int level = Math.Clamp(p.Heading, 1, 6);
                    html.Append($"<h{level}>").Append(Inlines(p.Inlines)).Append($"</h{level}>\n");
                    break;
                case Paragraph p when p.Preformatted:
                    html.Append("<pre>").Append(WebUtility.HtmlEncode(p.PlainText)).Append("</pre>\n");
                    break;
                case Paragraph p when p.Quote:
                    html.Append("<blockquote><p>").Append(Inlines(p.Inlines)).Append("</p></blockquote>\n");
                    break;
                case Paragraph p:
                    html.Append("<p>").Append(Inlines(p.Inlines)).Append("</p>\n");
                    break;
                case Table t when t.Rows.Count > 0:
                    html.Append("<table>\n");
                    foreach (var (row, rowIndex) in t.Rows.Select((r, i) => (r, i)))
                    {
                        var tag = rowIndex == 0 ? "th" : "td";
                        html.Append("<tr>");
                        foreach (var cell in row) html.Append($"<{tag}>").Append(Inlines(cell)).Append($"</{tag}>");
                        html.Append("</tr>\n");
                    }
                    html.Append("</table>\n");
                    break;
                case PageBreak:
                    html.Append("<hr>\n");
                    break;
            }
        }
        CloseLists(0);
        html.Append("</body>\n</html>\n");
        TextFiles.Write(output, html.ToString());
    }

    private static string Inlines(List<Inline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in InlineList.Merge(inlines))
        {
            var text = WebUtility.HtmlEncode(inline.Text).Replace("\n", "<br>\n");
            if (inline.Code) text = "<code>" + text + "</code>";
            if (inline.Underline) text = "<u>" + text + "</u>";
            if (inline.Italic) text = "<em>" + text + "</em>";
            if (inline.Bold) text = "<strong>" + text + "</strong>";
            if (inline.Link != null) text = $"<a href=\"{WebUtility.HtmlEncode(inline.Link)}\">{text}</a>";
            builder.Append(text);
        }
        return builder.ToString();
    }
}

/// <summary>Lays a document out on A4 (or US Letter) pages with 1" margins, like printing it.</summary>
public static class PdfDocumentWriter
{
    private static readonly XColor HeadingColor = XColor.FromArgb(31, 56, 100);
    private static readonly XColor LinkColor = XColor.FromArgb(5, 99, 193);
    private static readonly XColor QuoteColor = XColor.FromArgb(89, 89, 89);

    public static void Write(Document document, string output)
    {
        var size = RegionInfo.CurrentRegion.IsMetric ? new XSize(595.28, 841.89) : new XSize(612, 792);
        const double margin = 72;
        double width = size.Width - margin * 2;
        var pdf = new PdfSharp.Pdf.PdfDocument();
        PdfPage? page = null;
        XGraphics? graphics = null;
        double y = 0;

        void NewPage()
        {
            graphics?.Dispose();
            page = pdf.AddPage();
            page.Width = XUnit.FromPoint(size.Width);
            page.Height = XUnit.FromPoint(size.Height);
            graphics = XGraphics.FromPdfPage(page);
            y = margin;
        }

        void Link(XRect rect, string url)
        {
            if (page == null || url.StartsWith('#')) return;
            try { page.AddWebLink(new PdfRectangle(new XRect(rect.X, size.Height - rect.Bottom, rect.Width, rect.Height)), url); } catch { }
        }

        void Place(List<LaidOutLine> lines, double left, Action<double, double>? decorate = null)
        {
            foreach (var line in lines)
            {
                if (graphics == null || y + line.SpaceBefore + line.Height > size.Height - margin) NewPage();
                else y += line.SpaceBefore;
                decorate?.Invoke(y, line.Height);
                PdfLayout.Draw(graphics!, line, left, y, Link);
                y += line.Height;
            }
        }

        NewPage();
        bool firstBlock = true;
        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    var paragraph = Layout(p, firstBlock);
                    var lines = PdfLayout.Lines(paragraph, p.Quote ? width - 14 : width);
                    if (lines.Count > 0) lines[0].SpaceBefore = firstBlock ? 0 : paragraph.SpaceBefore;
                    if (lines.Count > 0) lines[^1].Height += paragraph.SpaceAfter;
                    if (p.Quote)
                    {
                        Place(lines, margin + 14, (top, height) =>
                            graphics!.DrawRectangle(new XSolidBrush(XColor.FromArgb(221, 221, 221)), margin, top, 3, height));
                    }
                    else if (p.Preformatted)
                    {
                        Place(lines, margin, (top, height) =>
                            graphics!.DrawRectangle(new XSolidBrush(XColor.FromArgb(244, 244, 244)), margin - 4, top, width + 8, height));
                    }
                    else
                    {
                        Place(lines, margin);
                    }
                    break;

                case Table t when t.Rows.Count > 0:
                    y += firstBlock ? 0 : 6;
                    DrawTable(t, width, margin, size, ref y, ref graphics, NewPage);
                    y += 10;
                    break;

                case PageBreak:
                    NewPage();
                    break;
            }
            firstBlock = false;
        }
        graphics?.Dispose();
        pdf.Save(output);
    }

    private static TextParagraph Layout(Paragraph p, bool first)
    {
        double[] headingSizes = { 22, 18, 15, 13, 12, 11 };
        var paragraph = new TextParagraph { LineSpacing = 1.2, SpaceAfter = 6, SpaceBefore = 0 };
        var baseStyle = new TextStyle(11);
        if (p.Heading > 0)
        {
            baseStyle = new TextStyle(headingSizes[Math.Clamp(p.Heading, 1, 6) - 1], Bold: true, Color: HeadingColor);
            paragraph.SpaceBefore = p.Heading <= 2 ? 16 : 10;
            paragraph.SpaceAfter = 6;
        }
        else if (p.Preformatted)
        {
            baseStyle = new TextStyle(9.5, Family: PdfFonts.Mono);
            paragraph.LineSpacing = 1.1;
            paragraph.SpaceAfter = 8;
        }
        else if (p.Quote)
        {
            baseStyle = new TextStyle(11, Italic: true, Color: QuoteColor);
        }
        if (p.List != ListKind.None)
        {
            paragraph.FirstIndent = 18 * p.Level;
            paragraph.Indent = 18 * p.Level + 18;
            paragraph.SpaceAfter = 3;
            paragraph.Bullet = new TextRun(p.List == ListKind.Ordered ? p.Number + "." : (p.Level % 2 == 0 ? "•" : "◦"), baseStyle);
        }
        foreach (var inline in p.Inlines)
        {
            paragraph.Runs.Add(new TextRun(inline.Text, baseStyle with
            {
                Bold = baseStyle.Bold || inline.Bold,
                Italic = baseStyle.Italic || inline.Italic,
                Underline = inline.Underline,
                Family = inline.Code ? PdfFonts.Mono : baseStyle.Family,
                Color = inline.Link != null ? LinkColor : baseStyle.Color,
                Link = inline.Link,
            }));
        }
        return paragraph;
    }

    private static void DrawTable(Table table, double width, double margin, XSize size, ref double y, ref XGraphics? graphics, Action newPage)
    {
        int columns = table.Rows.Max(r => r.Count);
        if (columns == 0) return;
        const double padding = 4;
        // Natural widths from the text, then scaled to the page width.
        var natural = new double[columns];
        foreach (var row in table.Rows)
        {
            for (int c = 0; c < row.Count; c++)
            {
                var text = string.Concat(row[c].Select(i => i.Text));
                var longest = text.Split('\n').Select(l => PdfLayout.Measure(l, PdfFonts.Get(new TextStyle(9.5), l))).DefaultIfEmpty(0).Max();
                natural[c] = Math.Max(natural[c], Math.Min(longest, 260) + padding * 2 + 2);
            }
        }
        double total = natural.Sum();
        var widths = natural.Select(w => total <= width ? w * Math.Max(1, (width * 0.6) / Math.Max(total, 1)) : Math.Max(30, w * width / total)).ToArray();
        double used = widths.Sum();
        if (used > width) widths = widths.Select(w => w * width / used).ToArray();

        var border = new XPen(XColor.FromArgb(191, 191, 191), 0.5);
        var header = new XSolidBrush(XColor.FromArgb(255, 224, 194));
        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var cells = new List<List<LaidOutLine>>();
            double height = 0;
            for (int c = 0; c < columns; c++)
            {
                var paragraph = new TextParagraph { LineSpacing = 1.1 };
                foreach (var inline in c < row.Count ? row[c] : new List<Inline>())
                {
                    paragraph.Runs.Add(new TextRun(inline.Text, new TextStyle(9.5, Bold: r == 0 || inline.Bold, Italic: inline.Italic)));
                }
                var lines = PdfLayout.Lines(paragraph, widths[c] - padding * 2);
                cells.Add(lines);
                height = Math.Max(height, PdfLayout.Height(lines) + padding * 2);
            }
            if (y + height > size.Height - margin)
            {
                newPage();
            }
            double x = margin;
            for (int c = 0; c < columns; c++)
            {
                var rect = new XRect(x, y, widths[c], height);
                if (r == 0) graphics!.DrawRectangle(header, rect);
                graphics!.DrawRectangle(border, rect);
                double lineTop = y + padding;
                foreach (var line in cells[c])
                {
                    if (lineTop + line.Height > y + height) break;
                    PdfLayout.Draw(graphics, line, x + padding, lineTop);
                    lineTop += line.Height;
                }
                x += widths[c];
            }
            y += height;
        }
    }
}
