using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace FileFlipper.Converters;

/// <summary>How a run of text looks.</summary>
public sealed record TextStyle(double Size, bool Bold = false, bool Italic = false, bool Underline = false,
                               XColor? Color = null, string? Family = null, string? Link = null)
{
    public XColor Ink => Color ?? XColors.Black;
}

public sealed record TextRun(string Text, TextStyle Style);

public enum TextAlign { Left, Center, Right }

/// <summary>A paragraph to lay out: runs, alignment, indents (from the box's left edge) and an optional bullet.</summary>
public sealed class TextParagraph
{
    public List<TextRun> Runs { get; } = new();
    public TextAlign Align { get; set; }
    /// <summary>Where the first line (or the bullet) starts.</summary>
    public double FirstIndent { get; set; }
    /// <summary>Where the other lines (and the text after a bullet) start.</summary>
    public double Indent { get; set; }
    public double SpaceBefore { get; set; }
    public double SpaceAfter { get; set; }
    public double LineSpacing { get; set; } = 1.15;
    public TextRun? Bullet { get; set; }
    /// <summary>Line height used when the paragraph has no text.</summary>
    public double EmptySize { get; set; } = 12;
}

public sealed class LaidOutLine
{
    public List<(string Text, XFont Font, TextStyle Style, double X, double Width)> Pieces { get; } = new();
    public double Ascent { get; set; }
    public double Height { get; set; }
    public double SpaceBefore { get; set; }
}

/// <summary>
/// Fonts for PDF output. Text with Chinese, Japanese or Korean characters needs a font that has
/// them, so those runs fall back to Microsoft YaHei (installed on every Windows 10/11 PC).
/// </summary>
public static class PdfFonts
{
    public const string Sans = "Segoe UI";
    public const string Cjk = "Microsoft YaHei";
    public const string Mono = "Consolas";

    private static readonly HashSet<string> Installed = LoadInstalled();
    private static readonly ConcurrentDictionary<(string, double, bool, bool), XFont> Cache = new();
    private static readonly string[] CjkFamilies =
    {
        "microsoft yahei", "微软雅黑", "dengxian", "等线", "simsun", "宋体", "nsimsun", "simhei", "黑体", "kaiti", "楷体",
        "fangsong", "仿宋", "microsoft jhenghei", "pmingliu", "mingliu", "yu gothic", "meiryo", "ms gothic", "ms mincho",
        "malgun gothic", "batang", "gulim", "pingfang sc", "source han sans", "noto sans cjk", "noto sans sc",
    };

    static PdfFonts()
    {
        try { PdfSharp.Fonts.GlobalFontSettings.FontResolver = new CollectionFontResolver(); } catch { }
    }

    private static HashSet<string> LoadInstalled()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                names.Add(family.Source);
                foreach (var name in family.FamilyNames.Values) names.Add(name);
            }
        }
        catch { }
        return names;
    }

    public static bool IsCjk(char c) =>
        (c >= '⺀' && c <= '鿿') || (c >= '가' && c <= '힯') || (c >= '豈' && c <= '﫿')
        || (c >= '︰' && c <= '﹏') || (c >= '＀' && c <= '￯') || (c >= '　' && c <= '〿')
        || char.IsSurrogate(c);

    public static bool HasCjk(string text) => text.Any(IsCjk);

    public static XFont Get(TextStyle style, string text)
    {
        var family = style.Family;
        bool cjkText = HasCjk(text);
        if (cjkText)
        {
            if (family == null || !CjkFamilies.Contains(family.ToLowerInvariant()) || !Installed.Contains(family)) family = Cjk;
        }
        else if (family == null || !Installed.Contains(family))
        {
            family = Sans;
        }
        // Office themes refer to fonts like "+mn-lt"; those aren't real names.
        if (family.StartsWith('+')) family = cjkText ? Cjk : Sans;
        return Font(family, style.Size, style.Bold, style.Italic);
    }

    public static XFont Font(string family, double size, bool bold = false, bool italic = false)
    {
        size = Math.Max(1, Math.Round(size, 2));
        return Cache.GetOrAdd((family, size, bold, italic), key =>
        {
            var styleEx = (bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
            try
            {
                return new XFont(key.Item1, key.Item2, styleEx, new XPdfFontOptions(PdfFontEncoding.Unicode));
            }
            catch
            {
                return new XFont(Sans, key.Item2, styleEx, new XPdfFontOptions(PdfFontEncoding.Unicode));
            }
        });
    }

    /// <summary>Distance from the top of a line to the baseline.</summary>
    public static double Ascent(XFont font)
    {
        var metrics = font.Metrics;
        return metrics.UnitsPerEm > 0 ? font.Size * metrics.Ascent / metrics.UnitsPerEm : font.Size * 0.9;
    }

    public static double LineHeight(XFont font) => font.GetHeight();
}

/// <summary>
/// Gives PDFsharp the font files it needs. Fonts are looked up the way WPF finds them (so localized
/// names like 微软雅黑 work too), and faces inside TrueType collections (.ttc) — which is how most
/// Chinese, Japanese and Korean fonts ship on Windows and which PDFsharp can't read — are cut out
/// as standalone fonts.
/// </summary>
public sealed class CollectionFontResolver : PdfSharp.Fonts.IFontResolver
{
    private static readonly ConcurrentDictionary<string, byte[]?> Data = new(StringComparer.OrdinalIgnoreCase);

    public PdfSharp.Fonts.FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        foreach (var family in new[] { familyName, PdfFonts.Sans, "Arial" })
        {
            var typeface = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily(family),
                italic ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal,
                bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyphs) || !glyphs.FontUri.IsFile) continue;
            // WPF marks the face inside a collection with a "#index" fragment.
            int index = int.TryParse(glyphs.FontUri.Fragment.TrimStart('#'), out var value) ? value : 0;
            bool simulateBold = bold && glyphs.Weight.ToOpenTypeWeight() < 600;
            bool simulateItalic = italic && glyphs.Style == System.Windows.FontStyles.Normal;
            return new PdfSharp.Fonts.FontResolverInfo($"{glyphs.FontUri.LocalPath}|{index}", simulateBold, simulateItalic);
        }
        return null;
    }

    public byte[]? GetFont(string faceName) => Data.GetOrAdd(faceName, key =>
    {
        try
        {
            var separator = key.LastIndexOf('|');
            var bytes = File.ReadAllBytes(key.Substring(0, separator));
            int index = int.Parse(key.Substring(separator + 1));
            return IsCollection(bytes) ? ExtractFace(bytes, index) : bytes;
        }
        catch
        {
            return null;
        }
    });
    private static bool IsCollection(byte[] bytes) => bytes.Length > 12 && bytes[0] == 't' && bytes[1] == 't' && bytes[2] == 'c' && bytes[3] == 'f';

    private static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    private static ushort U16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);

    /// <summary>Rebuilds one face of a collection as a standalone font: its table directory plus copies of its tables.</summary>
    private static byte[] ExtractFace(byte[] ttc, int face)
    {
        if (face < 0 || face >= (int)U32(ttc, 8)) face = 0;
        int faceOffset = (int)U32(ttc, 12 + 4 * face);
        int numTables = U16(ttc, faceOffset + 4);
        int headerLength = 12 + 16 * numTables;
        var tables = new List<(int Record, int Offset, int Length)>();
        for (int i = 0; i < numTables; i++)
        {
            int record = faceOffset + 12 + 16 * i;
            tables.Add((record, (int)U32(ttc, record + 8), (int)U32(ttc, record + 12)));
        }
        int total = headerLength + tables.Sum(t => (t.Length + 3) & ~3);
        var font = new byte[total];
        Array.Copy(ttc, faceOffset, font, 0, headerLength);
        int position = headerLength;
        for (int i = 0; i < tables.Count; i++)
        {
            var (_, offset, length) = tables[i];
            Array.Copy(ttc, offset, font, position, length);
            int record = 12 + 16 * i;
            font[record + 8] = (byte)(position >> 24); font[record + 9] = (byte)(position >> 16);
            font[record + 10] = (byte)(position >> 8); font[record + 11] = (byte)position;
            position += (length + 3) & ~3;
        }
        return font;
    }
}

/// <summary>
/// A small line breaker for PDF output (PDFsharp only draws single lines). Breaks at spaces and
/// between CJK characters, keeps closing punctuation with the character before it, and splits words
/// that are wider than the line.
/// </summary>
public static class PdfLayout
{
    private static readonly XGraphics Measurer = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
    private static readonly object MeasureLock = new();
    private const string NoLineStart = "，。、；：？！）》」』】〕〉”’…—,.;:?!)]}%";

    public static double Measure(string text, XFont font)
    {
        if (text.Length == 0) return 0;
        lock (MeasureLock) return Measurer.MeasureString(text, font).Width;
    }

    /// <summary>Lays out <paramref name="paragraphs"/> in a column <paramref name="width"/> points wide.</summary>
    public static List<LaidOutLine> Lines(IEnumerable<TextParagraph> paragraphs, double width)
    {
        var lines = new List<LaidOutLine>();
        bool first = true;
        foreach (var paragraph in paragraphs)
        {
            var paragraphLines = Lines(paragraph, width);
            if (paragraphLines.Count > 0)
            {
                paragraphLines[0].SpaceBefore = first ? 0 : paragraph.SpaceBefore;
                paragraphLines[^1].Height += paragraph.SpaceAfter;
            }
            lines.AddRange(paragraphLines);
            first = false;
        }
        return lines;
    }

    public static double Height(IEnumerable<LaidOutLine> lines) => lines.Sum(l => l.SpaceBefore + l.Height);

    private sealed class Token
    {
        public string Text = "";
        public TextStyle Style = null!;
        public bool IsSpace;
        public bool IsBreak;
    }

    public static List<LaidOutLine> Lines(TextParagraph paragraph, double width)
    {
        var tokens = Tokenize(paragraph.Runs);
        var lines = new List<LaidOutLine>();
        var current = new List<Token>();
        double x = 0;
        bool firstLine = true;
        double Start() => firstLine ? (paragraph.Bullet != null ? paragraph.Indent : paragraph.FirstIndent) : paragraph.Indent;
        double Available() => Math.Max(10, width - Start());

        void Finish()
        {
            lines.Add(Build(current, paragraph, Start(), Available(), firstLine));
            current = new List<Token>();
            x = 0;
            firstLine = false;
        }

        double pendingSpace = 0;
        var pendingSpaces = new List<Token>();
        foreach (var token in tokens)
        {
            if (token.IsBreak)
            {
                Finish();
                pendingSpace = 0; pendingSpaces.Clear();
                continue;
            }
            if (token.IsSpace)
            {
                if (current.Count == 0) continue;   // no spaces at the start of a line
                pendingSpace += Measure(token.Text, PdfFonts.Get(token.Style, "a"));
                pendingSpaces.Add(token);
                continue;
            }
            var font = PdfFonts.Get(token.Style, token.Text);
            double tokenWidth = Measure(token.Text, font);
            if (current.Count > 0 && x + pendingSpace + tokenWidth > Available() && !NoLineStart.Contains(token.Text[0]))
            {
                Finish();
                pendingSpace = 0; pendingSpaces.Clear();
            }
            if (current.Count == 0 && tokenWidth > Available())
            {
                // A single word wider than the line: split it by characters.
                var piece = new StringBuilder();
                foreach (var character in token.Text)
                {
                    var next = piece.ToString() + character;
                    if (piece.Length > 0 && Measure(next, font) > Available())
                    {
                        current.Add(new Token { Text = piece.ToString(), Style = token.Style });
                        Finish();
                        piece.Clear();
                    }
                    piece.Append(character);
                }
                current.Add(new Token { Text = piece.ToString(), Style = token.Style });
                x = Measure(piece.ToString(), font);
                continue;
            }
            current.AddRange(pendingSpaces);
            pendingSpaces.Clear();
            current.Add(token);
            x += pendingSpace + tokenWidth;
            pendingSpace = 0;
        }
        if (current.Count > 0 || lines.Count == 0) Finish();
        return lines;
    }

    private static List<Token> Tokenize(List<TextRun> runs)
    {
        var tokens = new List<Token>();
        foreach (var run in runs)
        {
            var word = new StringBuilder();
            void FlushWord()
            {
                if (word.Length > 0) tokens.Add(new Token { Text = word.ToString(), Style = run.Style });
                word.Clear();
            }
            var text = run.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Replace((char)0xA0, ' ');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n' || c == (char)0x2028 || c == (char)0x0B)
                {
                    FlushWord();
                    tokens.Add(new Token { IsBreak = true, Style = run.Style });
                }
                else if (c == ' ')
                {
                    FlushWord();
                    tokens.Add(new Token { Text = " ", IsSpace = true, Style = run.Style });
                }
                else if (PdfFonts.IsCjk(c))
                {
                    FlushWord();
                    var unit = char.IsHighSurrogate(c) && i + 1 < text.Length ? text.Substring(i++, 2) : c.ToString();
                    // Closing punctuation sticks to the character before it.
                    if (NoLineStart.Contains(c) && tokens.Count > 0 && !tokens[^1].IsSpace && !tokens[^1].IsBreak && tokens[^1].Style == run.Style)
                        tokens[^1].Text += unit;
                    else
                        tokens.Add(new Token { Text = unit, Style = run.Style });
                }
                else
                {
                    word.Append(c);
                }
            }
            FlushWord();
        }
        return tokens;
    }

    private static LaidOutLine Build(List<Token> tokens, TextParagraph paragraph, double start, double available, bool firstLine)
    {
        var line = new LaidOutLine();
        // Trailing spaces don't count.
        while (tokens.Count > 0 && tokens[^1].IsSpace) tokens.RemoveAt(tokens.Count - 1);

        // Merge neighbouring tokens that share a style and font into one piece.
        var pieces = new List<(string Text, XFont Font, TextStyle Style)>();
        foreach (var token in tokens)
        {
            var font = token.IsSpace && pieces.Count > 0 && pieces[^1].Style == token.Style
                ? pieces[^1].Font
                : PdfFonts.Get(token.Style, token.Text);
            if (pieces.Count > 0 && pieces[^1].Style == token.Style && pieces[^1].Font == font)
                pieces[^1] = (pieces[^1].Text + token.Text, font, token.Style);
            else
                pieces.Add((token.Text, font, token.Style));
        }

        double ascent = 0, height = 0;
        foreach (var (_, font, _) in pieces)
        {
            ascent = Math.Max(ascent, PdfFonts.Ascent(font));
            height = Math.Max(height, PdfFonts.LineHeight(font));
        }
        if (pieces.Count == 0)
        {
            var size = paragraph.Runs.Count > 0 ? paragraph.Runs[0].Style.Size : paragraph.EmptySize;
            var font = PdfFonts.Font(PdfFonts.Sans, size);
            ascent = PdfFonts.Ascent(font);
            height = PdfFonts.LineHeight(font);
        }

        double total = pieces.Sum(p => Measure(p.Text, p.Font));
        double x = start + paragraph.Align switch
        {
            TextAlign.Center => Math.Max(0, (available - total) / 2),
            TextAlign.Right => Math.Max(0, available - total),
            _ => 0,
        };
        if (firstLine && paragraph.Bullet != null)
        {
            var bulletFont = PdfFonts.Get(paragraph.Bullet.Style, paragraph.Bullet.Text);
            line.Pieces.Add((paragraph.Bullet.Text, bulletFont, paragraph.Bullet.Style, paragraph.FirstIndent, Measure(paragraph.Bullet.Text, bulletFont)));
            ascent = Math.Max(ascent, PdfFonts.Ascent(bulletFont));
            height = Math.Max(height, PdfFonts.LineHeight(bulletFont));
        }
        foreach (var (text, font, style) in pieces)
        {
            var w = Measure(text, font);
            line.Pieces.Add((text, font, style, x, w));
            x += w;
        }
        // Extra leading goes above the text, like line spacing in Word and PowerPoint.
        var extra = height * (paragraph.LineSpacing - 1);
        line.Ascent = ascent + Math.Max(0, extra);
        line.Height = height + Math.Max(0, extra);
        return line;
    }

    /// <summary>Draws one line with its top at <paramref name="top"/>. Calls <paramref name="onLink"/> for linked text.</summary>
    public static void Draw(XGraphics graphics, LaidOutLine line, double left, double top, Action<XRect, string>? onLink = null)
    {
        double baseline = top + line.Ascent;
        foreach (var (text, font, style, x, width) in line.Pieces)
        {
            if (text.Length == 0) continue;
            var brush = new XSolidBrush(style.Ink);
            graphics.DrawString(text, font, brush, left + x, baseline, XStringFormats.BaseLineLeft);
            if (style.Underline || style.Link != null)
            {
                var pen = new XPen(style.Ink, Math.Max(0.5, style.Size * 0.06));
                double y = baseline + style.Size * 0.13;
                graphics.DrawLine(pen, left + x, y, left + x + width, y);
            }
            if (style.Link != null) onLink?.Invoke(new XRect(left + x, baseline - style.Size, width, style.Size * 1.25), style.Link);
        }
    }

    /// <summary>Lays out and draws paragraphs inside a box, top / middle / bottom anchored.</summary>
    public static void DrawBox(XGraphics graphics, IEnumerable<TextParagraph> paragraphs, XRect box, string anchor = "t")
    {
        var lines = Lines(paragraphs, box.Width);
        double height = Height(lines);
        double y = anchor switch
        {
            "ctr" => box.Top + (box.Height - height) / 2,
            "b" => box.Bottom - height,
            _ => box.Top,
        };
        foreach (var line in lines)
        {
            y += line.SpaceBefore;
            Draw(graphics, line, box.Left, y);
            y += line.Height;
        }
    }

    /// <summary>One line of text, cut short with "…" when it doesn't fit.</summary>
    public static string Fit(string text, XFont font, double width)
    {
        if (Measure(text, font) <= width) return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Measure(text.Substring(0, middle) + "…", font) <= width) low = middle; else high = middle - 1;
        }
        return low == 0 ? "" : text.Substring(0, low) + "…";
    }
}
