using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using static FileFlipper.Loc;

namespace FileFlipper.Converters;

/// <summary>
/// PowerPoint (.pptx) → PDF or Markdown.
///
/// Reads the slide XML itself and draws what it finds: text boxes and placeholders (with the master's
/// default sizes, colours, alignment and bullets), pictures, filled shapes, tables and backgrounds,
/// including the decorations on the slide layout and master. Charts, SmartArt and animations are not
/// drawn. (Ported from the Mac app, which has the same limits.)
/// </summary>
public static class PresentationConverter
{
    // MARK: Model

    public sealed class Run
    {
        public string Text = "";
        public double Size;
        public bool Bold, Italic, Underline;
        public XColor Color;
        public string? Typeface;
        public Run Copy() => (Run)MemberwiseClone();
    }

    public sealed class Paragraph
    {
        public List<Run> Runs = new();
        public TextAlign Alignment = TextAlign.Left;
        public int Level;
        public string? Bullet;
        public double EmptySize = 18;
    }

    public sealed class TextBox
    {
        public List<Paragraph> Paragraphs = new();
        public string Anchor = "t";
        public (double Top, double Left, double Bottom, double Right) Insets = (3.6, 7.2, 3.6, 7.2);
        public bool IsTitle;
        public string PlainText => string.Join("\n", Paragraphs.Select(p => string.Concat(p.Runs.Select(r => r.Text))));
    }

    public sealed record TableCell(TextBox Text, XColor? Fill);

    public abstract record Item(XRect Rect);
    public sealed record ShapeItem(XRect Rect, double Rotation, string Geometry, XColor? Fill, XColor? Line, double LineWidth, TextBox? Text) : Item(Rect);
    public sealed record PictureItem(XRect Rect, double Rotation, byte[] Image) : Item(Rect);
    public sealed record TableItem(XRect Rect, List<double> Columns, List<(double Height, List<TableCell> Cells)> Rows) : Item(Rect);

    public sealed class Slide
    {
        public XColor Background = XColors.White;
        public byte[]? BackgroundImage;
        public List<Item> Items = new();
    }

    public sealed record Deck(XSize Size, List<Slide> Slides);

    // MARK: Convert

    public static string ToPdf(string path)
    {
        var deck = Read(path);
        var output = OutputNaming.Next(path, "pdf");
        try
        {
            using var pdf = new PdfDocument();
            foreach (var slide in deck.Slides)
            {
                var page = pdf.AddPage();
                page.Width = XUnit.FromPoint(deck.Size.Width);
                page.Height = XUnit.FromPoint(deck.Size.Height);
                using var graphics = XGraphics.FromPdfPage(page);
                Draw(slide, deck.Size, graphics);
            }
            pdf.Save(output);
        }
        catch (Exception error) when (error is not ConversionException)
        {
            FileSizes.TryDelete(output);
            throw new ConversionException(L("Couldn't write %@", Path.GetFileName(output)) + " (" + error.Message + ")");
        }
        return output;
    }

    public static string ToMarkdown(string path)
    {
        var deck = Read(path);
        var parts = new List<string>();
        for (int index = 0; index < deck.Slides.Count; index++)
        {
            string? title = null;
            var lines = new List<string>();
            // Reading order: top to bottom, then left to right.
            var ordered = deck.Slides[index].Items.OrderBy(i => i, Comparer<Item>.Create((a, b) =>
                Math.Abs(a.Rect.Top - b.Rect.Top) > 8 ? a.Rect.Top.CompareTo(b.Rect.Top) : a.Rect.Left.CompareTo(b.Rect.Left))).ToList();
            foreach (var item in ordered)
            {
                switch (item)
                {
                    case ShapeItem { Text: { } text }:
                        if (text.IsTitle && title == null)
                        {
                            title = text.PlainText.Replace("\n", " ");
                            continue;
                        }
                        var block = new List<string>();
                        foreach (var paragraph in text.Paragraphs)
                        {
                            var content = string.Concat(paragraph.Runs.Select(r => r.Text)).Trim();
                            if (content.Length == 0) continue;
                            block.Add(paragraph.Bullet != null
                                ? new string(' ', paragraph.Level * 2) + "- " + MarkdownWriter.Escape(content)
                                : MarkdownWriter.Escape(content));
                        }
                        if (block.Count > 0) lines.Add(string.Join("\n", block));
                        break;
                    case TableItem table:
                        var grid = table.Rows.Select(r => r.Cells.Select(c => MarkdownWriter.Escape(c.Text.PlainText).Replace("\n", " ").Replace("|", "\\|")).ToList()).ToList();
                        if (grid.Count > 0) lines.Add(MarkdownWriter.Table(grid));
                        break;
                }
            }
            var heading = $"## {L("Slide")} {index + 1}" + (title != null ? ": " + MarkdownWriter.Escape(title) : "");
            parts.Add(string.Join("\n\n", new[] { heading }.Concat(lines)));
        }
        var output = OutputNaming.Next(path, "md");
        TextFiles.Write(output, string.Join("\n\n", parts) + "\n");
        return output;
    }

    // MARK: Reading

    private const double Emu = 12_700;   // EMU per point

    private sealed record Part(string Path, Dictionary<string, (string Target, string Type)> Relationships);

    private sealed class Defaults
    {
        public Dictionary<string, XColor> Theme = new();
        public double TitleSize = 44;
        public double[] BodySizes = { 28, 24, 20, 20, 20, 20, 20, 20, 20 };
        public double OtherSize = 18;
        public TextAlign TitleAlignment = TextAlign.Left;
    }

    public static Deck Read(string path)
    {
        using var package = new OfficePackage(path);
        var presentation = package.Xml("ppt/presentation.xml") ?? throw ConversionException.Unreadable(path);
        var presentationRels = package.Relationships("ppt/presentation.xml");
        var size = new XSize(960, 540);
        if (presentation.Child("sldSz") is { } sldSz && sldSz.AttrDouble("cx") is double cx && sldSz.AttrDouble("cy") is double cy)
            size = new XSize(cx / Emu, cy / Emu);

        var slides = new List<Slide>();
        foreach (var reference in presentation.Child("sldIdLst").Children("sldId"))
        {
            var id = reference.RelationshipId();
            if (id == null || !presentationRels.TryGetValue(id, out var rel)) continue;
            var root = package.Xml(rel.Target);
            if (root == null || root.Attr("show") == "0") continue;
            slides.Add(ReadSlide(root, rel.Target, package));
        }
        if (slides.Count == 0) throw new ConversionException(L("%@ has no slides", Path.GetFileName(path)));
        return new Deck(size, slides);
    }

    private static Slide ReadSlide(XElement root, string path, OfficePackage package)
    {
        var slidePart = new Part(path, package.Relationships(path));
        var layoutPath = slidePart.Relationships.Values.FirstOrDefault(r => r.Type.EndsWith("/slideLayout")).Target;
        var layoutRoot = layoutPath != null ? package.Xml(layoutPath) : null;
        var layoutPart = layoutPath != null ? new Part(layoutPath, package.Relationships(layoutPath)) : null;
        var masterPath = layoutPart?.Relationships.Values.FirstOrDefault(r => r.Type.EndsWith("/slideMaster")).Target;
        var masterRoot = masterPath != null ? package.Xml(masterPath) : null;
        var masterPart = masterPath != null ? new Part(masterPath, package.Relationships(masterPath)) : null;

        var defaults = new Defaults();
        var themePath = masterPart?.Relationships.Values.FirstOrDefault(r => r.Type.EndsWith("/theme")).Target;
        if (themePath != null && package.Xml(themePath) is { } theme) defaults.Theme = ThemeColors(theme);
        if (masterRoot.Child("txStyles") is { } styles)
        {
            if (styles.Child("titleStyle").Child("lvl1pPr") is { } title)
            {
                if (title.Child("defRPr").AttrDouble("sz") is double s) defaults.TitleSize = s / 100;
                defaults.TitleAlignment = Alignment(title.Attr("algn")) ?? TextAlign.Left;
            }
            if (styles.Child("bodyStyle") is { } body)
            {
                for (int level = 1; level <= 9; level++)
                {
                    if (body.Child($"lvl{level}pPr").Child("defRPr").AttrDouble("sz") is double s) defaults.BodySizes[level - 1] = s / 100;
                }
            }
            if (styles.Child("otherStyle").Child("lvl1pPr").Child("defRPr").AttrDouble("sz") is double other) defaults.OtherSize = other / 100;
        }

        var reader = new TreeReader(package, defaults, layoutRoot.Child("cSld").Child("spTree"), masterRoot.Child("cSld").Child("spTree"));
        var slide = new Slide();
        // Background: slide, then layout, then master.
        foreach (var (element, part) in new[] { (root, slidePart), (layoutRoot, layoutPart), (masterRoot, masterPart) })
        {
            if (element == null || part == null || element.Child("cSld").Child("bg") is not { } bg) continue;
            if (bg.Child("bgPr") is { } props)
            {
                if (props.Child("solidFill") is { } fill && reader.Color(fill) is XColor color) { slide.Background = color; break; }
                if (props.Child("blipFill") is { } blip && reader.Image(blip, part) is { } image) { slide.BackgroundImage = image; break; }
            }
            if (bg.Child("bgRef") is { } bgRef && reader.Color(bgRef) is XColor refColor) { slide.Background = refColor; break; }
        }
        // Decorations from the master and layout (not their placeholders), then the slide itself.
        bool showMaster = root.Attr("showMasterSp") != "0" && layoutRoot.Attr("showMasterSp") != "0";
        if (showMaster && masterRoot.Child("cSld").Child("spTree") is { } masterTree && masterPart != null)
            slide.Items.AddRange(reader.Items(masterTree, masterPart, skipPlaceholders: true, new Transform()));
        if (root.Attr("showMasterSp") != "0" && layoutRoot.Child("cSld").Child("spTree") is { } layoutTree && layoutPart != null)
            slide.Items.AddRange(reader.Items(layoutTree, layoutPart, skipPlaceholders: true, new Transform()));
        if (root.Child("cSld").Child("spTree") is { } tree)
            slide.Items.AddRange(reader.Items(tree, slidePart, skipPlaceholders: false, new Transform()));
        return slide;
    }

    private static Dictionary<string, XColor> ThemeColors(XElement theme)
    {
        var colors = new Dictionary<string, XColor>();
        var scheme = theme.Descendant("clrScheme");
        if (scheme == null) return colors;
        foreach (var entry in scheme.Elements())
        {
            var name = entry.Name.LocalName;
            if (entry.Child("srgbClr").Attr("val") is { } rgb && Hex(rgb) is XColor c1) colors[name] = c1;
            else if (entry.Child("sysClr").Attr("lastClr") is { } sys && Hex(sys) is XColor c2) colors[name] = c2;
        }
        void Alias(string alias, string name) { if (colors.TryGetValue(name, out var c)) colors[alias] = c; }
        Alias("tx1", "dk1"); Alias("bg1", "lt1"); Alias("tx2", "dk2"); Alias("bg2", "lt2");
        return colors;
    }

    public static XColor? Hex(string value)
    {
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)) return null;
        return XColor.FromArgb((number >> 16) & 0xFF, (number >> 8) & 0xFF, number & 0xFF);
    }

    private static TextAlign? Alignment(string? value) => value switch
    {
        "ctr" => TextAlign.Center,
        "r" => TextAlign.Right,
        "l" or "just" or "dist" => TextAlign.Left,
        _ => null,
    };

    private struct Transform
    {
        public double Dx, Dy, Sx, Sy;
        public Transform() { Dx = 0; Dy = 0; Sx = 1; Sy = 1; }
        public XRect Apply(XRect r) => new(Dx + r.X * Sx, Dy + r.Y * Sy, r.Width * Sx, r.Height * Sy);
    }

    /// <summary>Walks a shape tree. Holds what's needed to resolve placeholders, colours and images.</summary>
    private sealed class TreeReader
    {
        private readonly OfficePackage package;
        private readonly Defaults defaults;
        private readonly XElement? layoutTree;
        private readonly XElement? masterTree;

        public TreeReader(OfficePackage package, Defaults defaults, XElement? layoutTree, XElement? masterTree)
        {
            this.package = package;
            this.defaults = defaults;
            this.layoutTree = layoutTree;
            this.masterTree = masterTree;
        }

        public List<Item> Items(XElement tree, Part part, bool skipPlaceholders, Transform transform)
        {
            var result = new List<Item>();
            foreach (var element in tree.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "sp":
                        var placeholder = element.Child("nvSpPr").Child("nvPr").Child("ph");
                        if (placeholder != null && skipPlaceholders) continue;
                        if (Shape(element, placeholder, part, transform) is { } shape) result.Add(shape);
                        break;
                    case "pic":
                        if (Picture(element, part, transform) is { } picture) result.Add(picture);
                        break;
                    case "graphicFrame":
                        if (Table(element, transform) is { } table) result.Add(table);
                        break;
                    case "grpSp":
                        if (element.Child("grpSpPr").Child("xfrm") is not { } xfrm)
                        {
                            result.AddRange(Items(element, part, skipPlaceholders, transform));
                            continue;
                        }
                        var outer = Frame(xfrm) ?? new XRect();
                        var childOffset = Point(xfrm.Child("chOff"), "x", "y") ?? outer.Location;
                        var childSize = Size(xfrm.Child("chExt")) ?? outer.Size;
                        var inner = new Transform
                        {
                            Sx = childSize.Width > 0 ? outer.Width / childSize.Width : 1,
                            Sy = childSize.Height > 0 ? outer.Height / childSize.Height : 1,
                        };
                        inner.Dx = outer.X - childOffset.X * inner.Sx;
                        inner.Dy = outer.Y - childOffset.Y * inner.Sy;
                        // Compose: parent ∘ inner.
                        var composed = new Transform
                        {
                            Dx = transform.Dx + inner.Dx * transform.Sx, Dy = transform.Dy + inner.Dy * transform.Sy,
                            Sx = transform.Sx * inner.Sx, Sy = transform.Sy * inner.Sy,
                        };
                        result.AddRange(Items(element, part, skipPlaceholders, composed));
                        break;
                    case "AlternateContent":
                        if (element.Child("Fallback") is { } fallback) result.AddRange(Items(fallback, part, skipPlaceholders, transform));
                        break;
                }
            }
            return result;
        }

        // MARK: Shapes and text

        private Item? Shape(XElement element, XElement? placeholder, Part part, Transform transform)
        {
            var props = element.Child("spPr");
            var inherited = placeholder != null ? InheritedPlaceholders(placeholder) : new List<XElement>();
            var xfrm = props.Child("xfrm") ?? inherited.Select(e => e.Child("spPr").Child("xfrm")).FirstOrDefault(x => x != null);
            if (xfrm == null || Frame(xfrm) is not XRect local) return null;
            var rect = transform.Apply(local);
            double rotation = (xfrm.AttrDouble("rot") ?? 0) / 60_000;

            // A picture used as the shape's fill.
            if (props.Child("blipFill") is { } blip && Image(blip, part) is { } image) return new PictureItem(rect, rotation, image);

            var geometry = props.Child("prstGeom").Attr("prst") ?? (placeholder == null ? "rect" : "none");
            XColor? fill = null, line = null;
            double lineWidth = 1;
            if (props != null)
            {
                if (props.Child("noFill") == null && props.Child("solidFill") is { } solid) fill = Color(solid);
                if (props.Child("ln") is { } ln)
                {
                    if (ln.Child("noFill") == null && ln.Child("solidFill") is { } lnFill) line = Color(lnFill);
                    if (ln.AttrDouble("w") is double w) lineWidth = w / Emu;
                }
                // Shapes drawn in PowerPoint take their colours from the theme style.
                if (element.Child("style") is { } style && placeholder == null)
                {
                    if (fill == null && props.Child("noFill") == null && props.Child("solidFill") == null
                        && style.Child("fillRef") is { } fillRef && fillRef.Attr("idx") != "0") fill = Color(fillRef);
                    if (line == null && props.Child("ln").Child("noFill") == null
                        && style.Child("lnRef") is { } lnRef && lnRef.Attr("idx") != "0") line = Color(lnRef);
                }
            }

            var fontColor = element.Child("style").Child("fontRef") is { } fontRef ? Color(fontRef) : null;
            var text = element.Child("txBody") is { } body ? TextBox(body, placeholder, inherited, fontColor) : null;
            bool hasText = text != null && text.PlainText.Trim().Length > 0;
            if (fill == null && line == null && !hasText) return null;
            return new ShapeItem(rect, rotation, geometry, fill, line, lineWidth, hasText ? text : null);
        }

        private static string PlaceholderKind(XElement placeholder) => placeholder.Attr("type") ?? "body";

        /// <summary>Layout, then master placeholders this one inherits from.</summary>
        private List<XElement> InheritedPlaceholders(XElement placeholder)
        {
            var type = PlaceholderKind(placeholder);
            var index = placeholder.Attr("idx");
            XElement? Find(XElement? tree, bool matchIndex) => tree.DescendantsNamed("sp").FirstOrDefault(sp =>
            {
                var ph = sp.Child("nvSpPr").Child("nvPr").Child("ph");
                if (ph == null) return false;
                if (matchIndex && index != null && ph.Attr("idx") == index) return true;
                var kind = PlaceholderKind(ph);
                return kind == type || (type == "ctrTitle" && kind == "title") || (type == "subTitle" && kind == "body");
            });
            return new[] { Find(layoutTree, true), Find(masterTree, false) }.Where(e => e != null).Cast<XElement>().ToList();
        }

        public TextBox TextBox(XElement body, XElement? placeholder, List<XElement> inherited, XColor? defaultColor)
        {
            var kind = placeholder != null ? PlaceholderKind(placeholder) : null;
            bool isTitle = kind is "title" or "ctrTitle";
            bool isBody = kind is "body" or "obj";
            var bodyPr = body.Child("bodyPr");
            var inheritedBodyPr = inherited.Select(e => e.Child("txBody").Child("bodyPr")).Where(e => e != null).ToList();
            string? BodyAttr(string name) => bodyPr.Attr(name) ?? inheritedBodyPr.Select(e => e.Attr(name)).FirstOrDefault(v => v != null);
            double Inset(string name, double fallback) => double.TryParse(BodyAttr(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / Emu : fallback;

            var box = new TextBox
            {
                IsTitle = isTitle,
                Anchor = BodyAttr("anchor") ?? (isTitle ? "ctr" : "t"),
                Insets = (Inset("tIns", 3.6), Inset("lIns", 7.2), Inset("bIns", 3.6), Inset("rIns", 7.2)),
            };
            double fontScale = (bodyPr.Child("normAutofit").AttrDouble("fontScale") ?? 100_000) / 100_000;

            // Level styles: the shape's own list style, then the inherited placeholders'.
            var listStyles = new[] { body.Child("lstStyle") }.Concat(inherited.Select(e => e.Child("txBody").Child("lstStyle"))).Where(e => e != null).Cast<XElement>().ToList();
            List<XElement> LevelProps(int level) => listStyles.Select(s => s.Child($"lvl{level + 1}pPr")).Where(e => e != null).Cast<XElement>().ToList();
            var textColor = defaultColor ?? (defaults.Theme.TryGetValue("tx1", out var tx1) ? tx1 : XColors.Black);

            foreach (var p in body.Children("p"))
            {
                var paragraph = new Paragraph();
                var pPr = p.Child("pPr");
                paragraph.Level = pPr.AttrInt("lvl") ?? 0;
                var levelStyles = LevelProps(paragraph.Level);
                paragraph.Alignment = Alignment(pPr.Attr("algn") ?? levelStyles.Select(s => s.Attr("algn")).FirstOrDefault(a => a != null))
                    ?? (isTitle ? defaults.TitleAlignment : TextAlign.Left);

                double baseSize;
                if (levelStyles.Select(s => s.Child("defRPr").AttrDouble("sz")).FirstOrDefault(v => v != null) is double sz) baseSize = sz / 100;
                else if (isTitle) baseSize = defaults.TitleSize;
                else if (isBody) baseSize = defaults.BodySizes[Math.Min(paragraph.Level, 8)];
                else if (kind == "subTitle") baseSize = defaults.BodySizes[0] * 0.8;
                else baseSize = defaults.OtherSize;

                // Bullets: explicit on the paragraph, else from the list style, else body placeholders get "•".
                var bulletSources = (pPr != null ? new[] { pPr } : Array.Empty<XElement>()).Concat(levelStyles).ToList();
                if (bulletSources.Any(s => s.Child("buNone") != null) && pPr.Child("buChar") == null && pPr.Child("buAutoNum") == null)
                    paragraph.Bullet = null;
                else if (bulletSources.Select(s => s.Child("buChar").Attr("char")).FirstOrDefault(c => c != null) is { } character)
                    paragraph.Bullet = character;
                else if (bulletSources.Any(s => s.Child("buAutoNum") != null))
                    paragraph.Bullet = "#";
                else if (isBody)
                    paragraph.Bullet = "•";

                foreach (var node in p.Elements())
                {
                    switch (node.Name.LocalName)
                    {
                        case "r":
                        case "fld":
                            var rPr = node.Child("rPr");
                            double size = ((rPr.AttrDouble("sz") is double runSize ? runSize / 100 : baseSize)) * fontScale;
                            var run = new Run { Text = node.Child("t").Text(), Size = size, Color = textColor };
                            run.Bold = rPr.Attr("b") is "1" or "true" || (rPr.Attr("b") == null && levelStyles.Any(s => s.Child("defRPr").Attr("b") is "1" or "true"));
                            run.Italic = rPr.Attr("i") is "1" or "true";
                            run.Underline = rPr.Attr("u") is { } u && u != "none";
                            if (rPr.Child("solidFill") is { } fill && Color(fill) is XColor c) run.Color = c;
                            else if (levelStyles.Select(s => s.Child("defRPr").Child("solidFill")).FirstOrDefault(f => f != null) is { } inheritedFill
                                     && Color(inheritedFill) is XColor ic) run.Color = ic;
                            run.Typeface = rPr.Child("ea").Attr("typeface") is { } ea && PdfFonts.HasCjk(run.Text) ? ea : rPr.Child("latin").Attr("typeface") ?? rPr.Child("ea").Attr("typeface");
                            paragraph.Runs.Add(run);
                            break;
                        case "br":
                            paragraph.Runs.Add(new Run { Text = "\n", Size = baseSize * fontScale, Color = textColor });
                            break;
                        case "endParaRPr":
                            paragraph.EmptySize = (node.AttrDouble("sz") is double endSize ? endSize / 100 : baseSize) * fontScale;
                            break;
                    }
                }
                if (paragraph.Runs.All(r => r.Text.Trim().Length == 0)) paragraph.Bullet = null;
                box.Paragraphs.Add(paragraph);
            }
            return box;
        }

        // MARK: Pictures and tables

        private Item? Picture(XElement element, Part part, Transform transform)
        {
            var xfrm = element.Child("spPr").Child("xfrm");
            if (xfrm == null || Frame(xfrm) is not XRect local || element.Child("blipFill") is not { } blip || Image(blip, part) is not { } image) return null;
            double rotation = (xfrm.AttrDouble("rot") ?? 0) / 60_000;
            return new PictureItem(transform.Apply(local), rotation, image);
        }

        public byte[]? Image(XElement blipFill, Part part)
        {
            var id = blipFill.Child("blip").Attr("embed");
            if (id == null || !part.Relationships.TryGetValue(id, out var rel)) return null;
            return package.Data(rel.Target);
        }

        private Item? Table(XElement element, Transform transform)
        {
            if (element.Child("xfrm") is not { } xfrm || Frame(xfrm) is not XRect local || element.Descendant("tbl") is not { } tbl) return null;
            var columns = tbl.Child("tblGrid").Children("gridCol").Select(c => (c.AttrDouble("w") ?? 0) / Emu * transform.Sx).ToList();
            // PowerPoint's default table style: accent-coloured header row with white bold text,
            // then banded rows in light tints of the same colour.
            var tblPr = tbl.Child("tblPr");
            bool styled = tblPr.Child("tableStyleId") != null || tblPr.Attr("firstRow") == "1";
            var accent = defaults.Theme.TryGetValue("accent1", out var a1) ? a1 : XColor.FromArgb(69, 115, 196);
            var rows = new List<(double, List<TableCell>)>();
            int rowIndex = 0;
            foreach (var tr in tbl.Children("tr"))
            {
                double height = (tr.AttrDouble("h") ?? 0) / Emu * transform.Sy;
                bool isHeader = styled && rowIndex == 0 && tblPr.Attr("firstRow") != "0";
                bool banded = styled && tblPr.Attr("bandRow") != "0";
                var cells = tr.Children("tc").Select(tc =>
                {
                    var box = tc.Child("txBody") is { } body ? TextBox(body, null, new List<XElement>(), null) : new TextBox();
                    box.Anchor = tc.Child("tcPr").Attr("anchor") ?? "t";
                    XColor? fill = tc.Child("tcPr").Child("solidFill") is { } f ? Color(f) : null;
                    if (fill == null && styled)
                    {
                        if (isHeader)
                        {
                            fill = accent;
                            foreach (var paragraph in box.Paragraphs)
                                foreach (var run in paragraph.Runs) { run.Bold = true; run.Color = XColors.White; }
                        }
                        else if (banded)
                        {
                            fill = Blend(accent, XColors.White, rowIndex % 2 == 1 ? 0.8 : 0.9);
                        }
                    }
                    return new TableCell(box, fill);
                }).ToList();
                rows.Add((height, cells));
                rowIndex++;
            }
            return new TableItem(transform.Apply(local), columns, rows);
        }

        // MARK: Geometry and colour

        public static XRect? Frame(XElement xfrm)
        {
            if (Point(xfrm.Child("off"), "x", "y") is not XPoint origin || Size(xfrm.Child("ext")) is not XSize size) return null;
            return new XRect(origin, size);
        }

        public static XPoint? Point(XElement? element, string xName, string yName)
        {
            if (element.AttrDouble(xName) is not double x || element.AttrDouble(yName) is not double y) return null;
            return new XPoint(x / Emu, y / Emu);
        }

        public static XSize? Size(XElement? element)
        {
            if (element.AttrDouble("cx") is not double cx || element.AttrDouble("cy") is not double cy) return null;
            return new XSize(cx / Emu, cy / Emu);
        }

        /// <summary>Colour from an element holding srgbClr / schemeClr / sysClr / prstClr, with lumMod/lumOff/alpha.</summary>
        public XColor? Color(XElement holder)
        {
            XColor? baseColor = null;
            XElement? spec = null;
            if (holder.Child("srgbClr") is { } srgb) { baseColor = Hex(srgb.Attr("val") ?? ""); spec = srgb; }
            else if (holder.Child("schemeClr") is { } scheme)
            {
                var name = scheme.Attr("val") ?? "";
                baseColor = defaults.Theme.TryGetValue(name, out var themed) ? themed : null;
                spec = scheme;
            }
            else if (holder.Child("sysClr") is { } sys) { baseColor = Hex(sys.Attr("lastClr") ?? ""); spec = sys; }
            else if (holder.Child("prstClr") is { } preset)
            {
                baseColor = (preset.Attr("val") ?? "") switch
                {
                    "black" => XColors.Black, "white" => XColors.White, "red" => XColors.Red, "blue" => XColors.Blue,
                    "green" => XColors.Green, "yellow" => XColors.Yellow, "gray" => XColors.Gray, _ => null,
                };
                spec = preset;
            }
            if (baseColor is not XColor color) return null;
            if (spec == null) return color;

            var (hue, saturation, brightness) = ToHsb(color);
            double alpha = 1;
            double? Value(string name) => spec.Child(name).AttrDouble("val") is double v ? v / 100_000 : null;
            var lumMod = Value("lumMod");
            var lumOff = Value("lumOff");
            var shade = Value("shade");
            var tint = Value("tint");
            if (lumMod != null || lumOff != null)
            {
                // Approximate HSL luminance changes in HSB space (as the Mac app does).
                double mod = lumMod ?? 1, off = lumOff ?? 0;
                if (off > 0) saturation *= mod;
                brightness = brightness * mod + off;
            }
            if (shade is double s) brightness *= s;
            if (tint is double t) { saturation *= t; brightness += (1 - brightness) * (1 - t); }
            if (Value("alpha") is double a) alpha = a;
            var result = FromHsb(hue, Math.Clamp(saturation, 0, 1), Math.Clamp(brightness, 0, 1));
            return XColor.FromArgb((int)Math.Round(alpha * 255), result.R, result.G, result.B);
        }
    }

    private static (double H, double S, double B) ToHsb(XColor color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = ((g - b) / delta) % 6;
            else if (max == g) hue = (b - r) / delta + 2;
            else hue = (r - g) / delta + 4;
            hue /= 6;
            if (hue < 0) hue += 1;
        }
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static XColor FromHsb(double h, double s, double v)
    {
        double i = Math.Floor(h * 6), f = h * 6 - i;
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        var (r, g, b) = ((int)i % 6) switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
        return XColor.FromArgb((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    private static XColor Blend(XColor color, XColor with, double fraction) => XColor.FromArgb(
        (int)Math.Round(color.R + (with.R - color.R) * fraction),
        (int)Math.Round(color.G + (with.G - color.G) * fraction),
        (int)Math.Round(color.B + (with.B - color.B) * fraction));

    // MARK: Drawing

    private static void Draw(Slide slide, XSize size, XGraphics graphics)
    {
        graphics.DrawRectangle(new XSolidBrush(slide.Background), 0, 0, size.Width, size.Height);
        if (slide.BackgroundImage != null) DrawImage(graphics, slide.BackgroundImage, new XRect(0, 0, size.Width, size.Height));
        foreach (var item in slide.Items)
        {
            switch (item)
            {
                case ShapeItem shape:
                    Rotated(graphics, shape.Rect, shape.Rotation, () =>
                    {
                        var rect = shape.Rect;
                        XBrush? brush = shape.Fill is XColor fill ? new XSolidBrush(fill) : null;
                        XPen? pen = shape.Line is XColor line ? new XPen(line, Math.Max(0.25, shape.LineWidth)) : null;
                        if (brush != null || pen != null)
                        {
                            switch (shape.Geometry)
                            {
                                case "ellipse":
                                    if (brush != null) graphics.DrawEllipse(brush, rect);
                                    if (pen != null) graphics.DrawEllipse(pen, rect);
                                    break;
                                case "roundRect":
                                    double r = Math.Min(rect.Width, rect.Height) * 0.1667 * 2;
                                    var ellipse = new XSize(r, r);
                                    if (brush != null) graphics.DrawRoundedRectangle(brush, rect, ellipse);
                                    if (pen != null) graphics.DrawRoundedRectangle(pen, rect, ellipse);
                                    break;
                                case "none":
                                    break;
                                default:
                                    if (brush != null) graphics.DrawRectangle(brush, rect);
                                    if (pen != null) graphics.DrawRectangle(pen, rect);
                                    break;
                            }
                        }
                        if (shape.Text != null) DrawText(graphics, shape.Text, rect);
                    });
                    break;
                case PictureItem picture:
                    Rotated(graphics, picture.Rect, picture.Rotation, () => DrawImage(graphics, picture.Image, picture.Rect));
                    break;
                case TableItem table:
                    double y = table.Rect.Top;
                    var border = new XPen(XColor.FromArgb(191, 191, 191), 0.75);
                    foreach (var row in table.Rows)
                    {
                        double x = table.Rect.Left;
                        // Rows grow to fit their text, like PowerPoint does.
                        double height = row.Height;
                        for (int index = 0; index < row.Cells.Count && index < table.Columns.Count; index++)
                            height = Math.Max(height, MeasuredHeight(row.Cells[index].Text, table.Columns[index]));
                        for (int index = 0; index < row.Cells.Count && index < table.Columns.Count; index++)
                        {
                            var cellRect = new XRect(x, y, table.Columns[index], height);
                            if (row.Cells[index].Fill is XColor fill) graphics.DrawRectangle(new XSolidBrush(fill), cellRect);
                            graphics.DrawRectangle(border, cellRect);
                            DrawText(graphics, row.Cells[index].Text, cellRect);
                            x += table.Columns[index];
                        }
                        y += height;
                    }
                    break;
            }
        }
    }

    private static void Rotated(XGraphics graphics, XRect rect, double degrees, Action body)
    {
        if (degrees == 0) { body(); return; }
        var state = graphics.Save();
        graphics.RotateAtTransform(degrees, new XPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        body();
        graphics.Restore(state);
    }

    private static void DrawImage(XGraphics graphics, byte[] data, XRect rect)
    {
        try
        {
            // Re-encode through WIC so any format Windows can open (including EMF previews as bitmaps) works.
            using var input = new MemoryStream(data);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(input, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                                                                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            bool alpha = ImageConverter.HasAlpha(frame);
            var bytes = alpha || decoder.CodecInfo?.FileExtensions?.Contains("jpg") != true
                ? ImageConverter.Encode(frame, alpha ? ImageConverter.PngFormat : ImageConverter.JpegFormat, 0.92)
                : data;
            using var stream = new MemoryStream(bytes);
            using var image = XImage.FromStream(stream);
            graphics.DrawImage(image, rect);
        }
        catch
        {
            // Pictures Windows can't decode (e.g. vector EMF/WMF) are skipped.
        }
    }

    private static List<TextParagraph> Layout(TextBox box)
    {
        var result = new List<TextParagraph>();
        int number = 0;
        bool first = true;
        foreach (var paragraph in box.Paragraphs)
        {
            double size = paragraph.Runs.FirstOrDefault()?.Size ?? paragraph.EmptySize;
            var laid = new TextParagraph
            {
                Align = paragraph.Alignment,
                LineSpacing = 1.0,
                SpaceBefore = first ? 0 : size * 0.25,
                EmptySize = paragraph.EmptySize,
            };
            double indent = paragraph.Level * 24;
            if (paragraph.Bullet != null)
            {
                number++;
                var marker = paragraph.Bullet == "#" ? number + "." : paragraph.Bullet;
                laid.FirstIndent = indent;
                laid.Indent = indent + size;
                laid.Bullet = new TextRun(marker, new TextStyle(size, Color: paragraph.Runs.FirstOrDefault()?.Color ?? XColors.Black));
            }
            else
            {
                laid.FirstIndent = indent;
                laid.Indent = indent;
                number = 0;
            }
            if (paragraph.Runs.Count == 0) laid.Runs.Add(new TextRun(" ", new TextStyle(paragraph.EmptySize)));
            foreach (var run in paragraph.Runs)
            {
                laid.Runs.Add(new TextRun(run.Text, new TextStyle(run.Size, run.Bold, run.Italic, run.Underline, run.Color, run.Typeface)));
            }
            result.Add(laid);
            first = false;
        }
        return result;
    }

    private static double MeasuredHeight(TextBox box, double width)
    {
        double inner = Math.Max(1, width - box.Insets.Left - box.Insets.Right);
        return Math.Ceiling(PdfLayout.Height(PdfLayout.Lines(Layout(box), inner))) + box.Insets.Top + box.Insets.Bottom;
    }

    private static void DrawText(XGraphics graphics, TextBox box, XRect rect)
    {
        var inner = new XRect(rect.X + box.Insets.Left, rect.Y + box.Insets.Top,
                              Math.Max(1, rect.Width - box.Insets.Left - box.Insets.Right),
                              Math.Max(1, rect.Height - box.Insets.Top - box.Insets.Bottom));
        PdfLayout.DrawBox(graphics, Layout(box), inner, box.Anchor);
    }
}
