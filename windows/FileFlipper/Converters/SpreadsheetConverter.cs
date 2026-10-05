using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using static FileFlipper.Loc;

namespace FileFlipper.Converters;

/// <summary>
/// Excel (.xlsx) → PDF or Markdown. Reads the cell values of every sheet (text, numbers, dates,
/// percentages) and lays them out as tables; formulas show their last calculated value.
/// </summary>
public static class SpreadsheetConverter
{
    public sealed record Sheet(string Name, List<List<string>> Rows, HashSet<int> NumericColumns);

    // MARK: Convert

    public static string ToPdf(string path)
    {
        var sheets = Read(path);
        var output = OutputNaming.Next(path, "pdf");
        try
        {
            RenderPdf(sheets, output);
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
        var sheets = Read(path);
        var parts = sheets.Select(sheet =>
        {
            var rows = sheet.Rows.Select(r => r.Select(c => MarkdownWriter.Escape(c).Replace("|", "\\|").Replace("\n", " ")).ToList()).ToList();
            return "## " + MarkdownWriter.Escape(sheet.Name) + "\n\n" + MarkdownWriter.Table(rows);
        });
        var output = OutputNaming.Next(path, "md");
        TextFiles.Write(output, string.Join("\n\n", parts) + "\n");
        return output;
    }

    // MARK: Reading

    public static List<Sheet> Read(string path)
    {
        using var package = new OfficePackage(path);
        var workbook = package.Xml("xl/workbook.xml") ?? throw ConversionException.Unreadable(path);
        var relationships = package.Relationships("xl/workbook.xml");
        var shared = SharedStrings(package);
        var formats = CellFormats(package);
        bool date1904 = workbook.Child("workbookPr").Attr("date1904") is "1" or "true";

        var sheets = new List<Sheet>();
        foreach (var sheet in workbook.Child("sheets").Children("sheet"))
        {
            if (sheet.Attr("state") is "hidden" or "veryHidden") continue;
            var id = sheet.RelationshipId();
            if (id == null || !relationships.TryGetValue(id, out var rel)) continue;
            var root = package.Xml(rel.Target);
            if (root == null) continue;
            var name = sheet.Attr("name") ?? "Sheet";
            var grid = new Dictionary<int, Dictionary<int, (string Text, bool Numeric)>>();
            int implicitRow = 0;
            foreach (var row in root.Child("sheetData").Children("row"))
            {
                int rowIndex = (row.AttrInt("r") ?? implicitRow + 1) - 1;
                implicitRow = rowIndex + 1;
                if (row.Attr("hidden") is "1" or "true") continue;
                int implicitColumn = 0;
                foreach (var cell in row.Children("c"))
                {
                    int column = cell.Attr("r") is { } reference && Position(reference) is { } position ? position.Column : implicitColumn;
                    implicitColumn = column + 1;
                    var (text, numeric) = Value(cell, shared, formats, date1904);
                    if (text.Length == 0) continue;
                    if (!grid.TryGetValue(rowIndex, out var cells)) grid[rowIndex] = cells = new();
                    cells[column] = (text, numeric);
                }
            }
            if (grid.Count == 0) continue;
            int firstRow = grid.Keys.Min(), lastRow = grid.Keys.Max();
            var columns = grid.Values.SelectMany(c => c.Keys).ToList();
            int firstColumn = columns.Min(), lastColumn = columns.Max();
            var rows = new List<List<string>>();
            var numericCounts = new Dictionary<int, int>();
            var totalCounts = new Dictionary<int, int>();
            for (int r = firstRow; r <= lastRow; r++)
            {
                var line = new List<string>();
                for (int c = firstColumn; c <= lastColumn; c++)
                {
                    if (grid.TryGetValue(r, out var cells) && cells.TryGetValue(c, out var entry))
                    {
                        line.Add(entry.Text);
                        if (r != firstRow)
                        {
                            totalCounts[c - firstColumn] = totalCounts.GetValueOrDefault(c - firstColumn) + 1;
                            if (entry.Numeric) numericCounts[c - firstColumn] = numericCounts.GetValueOrDefault(c - firstColumn) + 1;
                        }
                    }
                    else
                    {
                        line.Add("");
                    }
                }
                rows.Add(line);
            }
            var numericColumns = totalCounts.Where(t => numericCounts.GetValueOrDefault(t.Key) * 2 > t.Value).Select(t => t.Key).ToHashSet();
            sheets.Add(new Sheet(name, rows, numericColumns));
        }
        if (sheets.Count == 0) throw new ConversionException(L("%@ has no data", Path.GetFileName(path)));
        return sheets;
    }

    private static List<string> SharedStrings(OfficePackage package)
    {
        var root = package.Xml("xl/sharedStrings.xml");
        return root.Children("si").Select(item =>
            item.Child("t") is { } direct ? direct.Value : string.Concat(item.Children("r").Select(r => r.Child("t").Text()))).ToList();
    }

    private abstract record NumberStyle;
    private sealed record General : NumberStyle;
    private sealed record DateStyle(bool Time) : NumberStyle;
    private sealed record TimeStyle : NumberStyle;
    private sealed record PercentStyle(int Decimals) : NumberStyle;
    private sealed record FixedStyle(int Decimals, bool Grouping) : NumberStyle;

    /// <summary>Number style for each cell style index (the "s" attribute).</summary>
    private static List<NumberStyle> CellFormats(OfficePackage package)
    {
        var root = package.Xml("xl/styles.xml");
        var custom = new Dictionary<int, string>();
        foreach (var format in root.Child("numFmts").Children("numFmt"))
        {
            if (format.AttrInt("numFmtId") is int id) custom[id] = format.Attr("formatCode") ?? "";
        }
        return root.Child("cellXfs").Children("xf").Select(xf =>
        {
            int id = xf.AttrInt("numFmtId") ?? 0;
            switch (id)
            {
                case >= 14 and <= 17: return new DateStyle(false);
                case 22: return new DateStyle(true);
                case >= 18 and <= 21:
                case >= 45 and <= 47: return new TimeStyle();
                case 9: return new PercentStyle(0);
                case 10: return new PercentStyle(2);
                case 1: return new FixedStyle(0, false);
                case 2: return new FixedStyle(2, false);
                case 3: return new FixedStyle(0, true);
                case 4: return new FixedStyle(2, true);
            }
            if (!custom.TryGetValue(id, out var code)) return (NumberStyle)new General();
            // Ignore quoted literals and colour/condition sections when sniffing the format.
            var bare = Regex.Replace(Regex.Replace(code, "\"[^\"]*\"", ""), @"\[[^\]]*\]", "").ToLowerInvariant();
            if (bare.Contains('y') || bare.Contains('d') || (bare.Contains('m') && !bare.Contains('0'))) return new DateStyle(bare.Contains('h'));
            if (bare.Contains('h') || bare.Contains('s')) return new TimeStyle();
            var decimalsMatch = Regex.Match(bare, @"\.(0+)");
            int decimals = decimalsMatch.Success ? decimalsMatch.Groups[1].Length : 0;
            if (bare.Contains('%')) return new PercentStyle(decimals);
            if (bare.Contains('0') || bare.Contains('#')) return new FixedStyle(decimals, bare.Contains(','));
            return new General();
        }).ToList();
    }

    private static (string, bool) Value(XElement cell, List<string> shared, List<NumberStyle> formats, bool date1904)
    {
        var raw = cell.Child("v").Text();
        switch (cell.Attr("t"))
        {
            case "s":
                return (int.TryParse(raw, out var index) && index >= 0 && index < shared.Count ? shared[index] : "", false);
            case "inlineStr":
                var item = cell.Child("is");
                return (item.Child("t") is { } t ? t.Value : string.Concat(item.Children("r").Select(r => r.Child("t").Text())), false);
            case "b":
                return (raw == "1" ? "TRUE" : "FALSE", false);
            case "str":
            case "e":
                return (raw, false);
            default:
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return (raw, false);
                var style = cell.AttrInt("s") is int s && s >= 0 && s < formats.Count ? formats[s] : new General();
                return (Format(number, style, date1904), true);
        }
    }

    private static string Format(double number, NumberStyle style, bool date1904)
    {
        var invariant = CultureInfo.InvariantCulture;
        switch (style)
        {
            case DateStyle date:
                var epoch = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
                try
                {
                    var value = epoch.AddDays(number);
                    return value.ToString(date.Time ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", invariant);
                }
                catch (ArgumentOutOfRangeException) { return number.ToString(invariant); }
            case TimeStyle:
                int seconds = (int)Math.Round((number % 1) * 86_400);
                return $"{seconds / 3600:00}:{seconds % 3600 / 60:00}";
            case PercentStyle percent:
                return (number * 100).ToString("F" + percent.Decimals, invariant) + "%";
            case FixedStyle fixedStyle:
                return number.ToString((fixedStyle.Grouping ? "N" : "F") + fixedStyle.Decimals, invariant);
            default:
                if (number == Math.Round(number) && Math.Abs(number) < 1e15) return ((long)number).ToString(invariant);
                return number.ToString("G11", invariant);
        }
    }

    /// <summary>"C12" → (column 2, row 11), zero-based.</summary>
    private static (int Column, int Row)? Position(string reference)
    {
        int column = 0;
        var digits = "";
        foreach (var c in reference.ToUpperInvariant())
        {
            if (c >= 'A' && c <= 'Z') column = column * 26 + (c - 'A' + 1);
            else if (char.IsDigit(c)) digits += c;
        }
        if (column == 0 || !int.TryParse(digits, out var row) || row <= 0) return null;
        return (column - 1, row - 1);
    }

    // MARK: PDF

    private static readonly XColor Brown = XColor.FromArgb(64, 38, 26);
    private static readonly XColor Peach = XColor.FromArgb(153, 255, 224, 194);

    private static void RenderPdf(List<Sheet> sheets, string output)
    {
        using var pdf = new PdfDocument();
        const double margin = 36, rowHeight = 16, padding = 5, titleHeight = 26;

        foreach (var sheet in sheets)
        {
            int columnCount = sheet.Rows.Max(r => r.Count);
            if (columnCount == 0) continue;
            // Natural column widths, capped so one long cell can't take over the page.
            var widths = Enumerable.Repeat(36.0, columnCount).ToArray();
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                var row = sheet.Rows[r];
                for (int c = 0; c < row.Count; c++)
                {
                    if (row[c].Length == 0) continue;
                    var font = PdfFonts.Get(new TextStyle(9, Bold: r == 0), row[c]);
                    double width = Math.Ceiling(PdfLayout.Measure(row[c].Replace('\n', ' '), font)) + padding * 2 + 6;
                    widths[c] = Math.Min(Math.Max(widths[c], width), 220);
                }
            }
            double total = widths.Sum();
            // Portrait if it fits, otherwise landscape; then shrink a little, then split columns.
            bool landscape = total > 612 - margin * 2;
            var page = landscape ? new XSize(792, 612) : new XSize(612, 792);
            double usable = page.Width - margin * 2;
            double scale = Math.Max(0.7, Math.Min(1, usable / total));
            var chunks = new List<(int Start, int End)>();
            int start = 0;
            double running = 0;
            for (int column = 0; column < columnCount; column++)
            {
                if (running + widths[column] * scale > usable && column > start)
                {
                    chunks.Add((start, column)); start = column; running = 0;
                }
                running += widths[column] * scale;
            }
            chunks.Add((start, columnCount));

            var header = sheet.Rows[0];
            var body = sheet.Rows.Skip(1).ToList();
            int rowsPerPage = Math.Max(1, (int)((page.Height - margin * 2 - titleHeight) / (rowHeight * scale)) - 1);

            foreach (var chunk in chunks)
            {
                int first = 0;
                do
                {
                    var pdfPage = pdf.AddPage();
                    pdfPage.Width = XUnit.FromPoint(page.Width);
                    pdfPage.Height = XUnit.FromPoint(page.Height);
                    using var graphics = XGraphics.FromPdfPage(pdfPage);

                    var title = sheet.Name;
                    if (chunks.Count > 1) title += $"  ({L("columns")} {ColumnName(chunk.Start)}–{ColumnName(chunk.End - 1)})";
                    var titleFont = PdfFonts.Get(new TextStyle(13, Bold: true), title);
                    graphics.DrawString(title, titleFont, new XSolidBrush(Brown), margin, margin + PdfFonts.Ascent(titleFont), XStringFormats.BaseLineLeft);

                    int last = Math.Min(body.Count, first + rowsPerPage);
                    double y = margin + titleHeight;
                    DrawRow(graphics, header, chunk, widths, scale, y, rowHeight, true, sheet.NumericColumns, margin, padding, false);
                    y += rowHeight * scale;
                    for (int index = first; index < last; index++)
                    {
                        DrawRow(graphics, body[index], chunk, widths, scale, y, rowHeight, false, sheet.NumericColumns, margin, padding, index % 2 == 1);
                        y += rowHeight * scale;
                    }
                    first = last;
                } while (first < body.Count);
            }
        }
        if (pdf.PageCount == 0) throw new ConversionException(L("Nothing to put in the PDF"));
        pdf.Save(output);
    }

    private static void DrawRow(XGraphics graphics, List<string> row, (int Start, int End) chunk, double[] widths, double scale, double y,
                                double height, bool isHeader, HashSet<int> numeric, double margin, double padding, bool striped)
    {
        double x = margin;
        double h = height * scale;
        var border = new XPen(XColor.FromArgb(209, 209, 209), 0.5);
        for (int column = chunk.Start; column < chunk.End; column++)
        {
            double w = widths[column] * scale;
            var cell = new XRect(x, y, w, h);
            if (isHeader) graphics.DrawRectangle(new XSolidBrush(Peach), cell);
            else if (striped) graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(247, 247, 247)), cell);
            graphics.DrawRectangle(border, cell);

            var text = column < row.Count ? row[column].Replace('\n', ' ') : "";
            if (text.Length > 0)
            {
                var font = PdfFonts.Get(new TextStyle(9 * scale, Bold: isHeader), text);
                var fitted = PdfLayout.Fit(text, font, w - padding * 2 * scale);
                double baseline = y + (h - PdfFonts.LineHeight(font)) / 2 + PdfFonts.Ascent(font);
                bool right = !isHeader && numeric.Contains(column);
                double textX = right ? x + w - padding * scale - PdfLayout.Measure(fitted, font) : x + padding * scale;
                graphics.DrawString(fitted, font, new XSolidBrush(XColor.FromArgb(26, 26, 26)), textX, baseline, XStringFormats.BaseLineLeft);
            }
            x += w;
        }
    }

    private static string ColumnName(int index)
    {
        index += 1;
        var name = "";
        while (index > 0)
        {
            int remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }
        return name;
    }
}
