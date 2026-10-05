using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FileFlipper.Converters;

// The Mac app moves documents between formats with the Cocoa text system (NSAttributedString).
// Windows has nothing like it, so every reader produces this small model and every writer consumes it:
// headings, paragraphs, lists, quotes, tables, and bold / italic / underline / link / code runs.

public sealed record Inline(string Text, bool Bold = false, bool Italic = false, bool Underline = false,
                            string? Link = null, bool Code = false)
{
    public bool SameStyle(Inline other) =>
        Bold == other.Bold && Italic == other.Italic && Underline == other.Underline && Link == other.Link && Code == other.Code;
}

public enum ListKind { None, Bullet, Ordered }

public abstract class Block { }

public sealed class Paragraph : Block
{
    public List<Inline> Inlines { get; } = new();
    /// <summary>1–6 for headings, 0 for body text.</summary>
    public int Heading { get; set; }
    public ListKind List { get; set; }
    /// <summary>Nesting depth of a list item, from 0.</summary>
    public int Level { get; set; }
    /// <summary>The number shown for an ordered list item.</summary>
    public int Number { get; set; } = 1;
    public bool Quote { get; set; }
    public bool Preformatted { get; set; }

    public string PlainText => string.Concat(Inlines.Select(i => i.Text));
    public bool IsEmpty => string.IsNullOrWhiteSpace(PlainText);
}

public sealed class Table : Block
{
    /// <summary>Rows of cells; each cell is a run of inlines ("\n" separates paragraphs inside a cell).</summary>
    public List<List<List<Inline>>> Rows { get; } = new();
}

public sealed class PageBreak : Block { }

public sealed class Document
{
    public List<Block> Blocks { get; } = new();

    public bool IsEmpty => !Blocks.Any(b => b is Paragraph { IsEmpty: false } || b is Table { Rows.Count: > 0 });

    public string PlainText()
    {
        var lines = new List<string>();
        foreach (var block in Blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    var prefix = p.List switch
                    {
                        ListKind.Bullet => new string(' ', p.Level * 4) + "• ",
                        ListKind.Ordered => new string(' ', p.Level * 4) + p.Number + ". ",
                        _ => "",
                    };
                    lines.Add(prefix + p.PlainText);
                    break;
                case Table t:
                    foreach (var row in t.Rows)
                        lines.Add(string.Join("\t", row.Select(c => string.Concat(c.Select(i => i.Text)).Replace("\n", " "))));
                    break;
                case PageBreak:
                    lines.Add("");
                    break;
            }
        }
        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    /// <summary>Plain lines of text (PDF text, OCR output) as paragraphs, one per blank-line-separated block.</summary>
    public static Document FromPlainPages(IEnumerable<string> pages)
    {
        var document = new Document();
        bool firstPage = true;
        foreach (var page in pages)
        {
            if (!firstPage) document.Blocks.Add(new PageBreak());
            firstPage = false;
            var current = new List<string>();
            void Flush()
            {
                if (current.Count == 0) return;
                var paragraph = new Paragraph();
                paragraph.Inlines.Add(new Inline(string.Join("\n", current)));
                document.Blocks.Add(paragraph);
                current.Clear();
            }
            foreach (var raw in page.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) Flush();
                else current.Add(line);
            }
            Flush();
        }
        return document;
    }
}

/// <summary>Merges neighbouring runs that look the same, which keeps Markdown and RTF output tidy.</summary>
public static class InlineList
{
    public static List<Inline> Merge(IEnumerable<Inline> inlines)
    {
        var merged = new List<Inline>();
        foreach (var inline in inlines)
        {
            if (inline.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].SameStyle(inline))
                merged[^1] = merged[^1] with { Text = merged[^1].Text + inline.Text };
            else
                merged.Add(inline);
        }
        return merged;
    }

    /// <summary>Trims leading and trailing whitespace across the runs of a paragraph.</summary>
    public static List<Inline> Trim(List<Inline> inlines)
    {
        var result = Merge(inlines);
        while (result.Count > 0 && result[0].Text.TrimStart().Length == 0) result.RemoveAt(0);
        while (result.Count > 0 && result[^1].Text.TrimEnd().Length == 0) result.RemoveAt(result.Count - 1);
        if (result.Count > 0) result[0] = result[0] with { Text = result[0].Text.TrimStart() };
        if (result.Count > 0) result[^1] = result[^1] with { Text = result[^1].Text.TrimEnd() };
        return result;
    }
}

/// <summary>Turns the document model into Markdown: headings, bold, italic, links, lists, quotes and tables.</summary>
public static class MarkdownWriter
{
    public static string Write(Document document)
    {
        var blocks = new List<(string Text, bool IsList)>();
        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case Paragraph p when p.IsEmpty:
                    break;
                case Paragraph { Preformatted: true } p:
                    blocks.Add(("```\n" + p.PlainText.TrimEnd('\n') + "\n```", false));
                    break;
                case Paragraph p when p.Heading > 0:
                    blocks.Add((new string('#', Math.Clamp(p.Heading, 1, 6)) + " " + Escape(p.PlainText.Replace('\n', ' ').Trim()), false));
                    break;
                case Paragraph p when p.List != ListKind.None:
                    var indent = new string(' ', p.Level * 3);
                    var marker = p.List == ListKind.Ordered ? p.Number + ". " : "- ";
                    blocks.Add((indent + marker + Inlines(p.Inlines), true));
                    break;
                case Paragraph p when p.Quote:
                    blocks.Add(("> " + Inlines(p.Inlines).Replace("\n", "\n> "), false));
                    break;
                case Paragraph p:
                    blocks.Add((Inlines(p.Inlines), false));
                    break;
                case Table t when t.Rows.Count > 0:
                    var rows = t.Rows.Select(row => row.Select(cell =>
                        Inlines(cell).Replace("\n", " ").Replace("|", "\\|").Trim()).ToList()).ToList();
                    blocks.Add((Table(rows), false));
                    break;
                case PageBreak:
                    if (blocks.Count > 0 && blocks[^1].Text != "---") blocks.Add(("---", false));
                    break;
            }
        }
        while (blocks.Count > 0 && blocks[^1].Text == "---") blocks.RemoveAt(blocks.Count - 1);

        // Consecutive list items stay together; everything else is separated by a blank line.
        var output = new StringBuilder();
        for (int index = 0; index < blocks.Count; index++)
        {
            if (index > 0) output.Append(blocks[index - 1].IsList && blocks[index].IsList ? "\n" : "\n\n");
            output.Append(blocks[index].Text);
        }
        return output + "\n";
    }

    /// <summary>Markdown for plain paragraphs (PDF text, OCR output), one section per page.</summary>
    public static string FromPlainPages(IEnumerable<string> pages)
    {
        var parts = pages.Select(page =>
        {
            var paragraphs = new List<string>();
            var current = new List<string>();
            foreach (var raw in page.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
                    current.Clear();
                }
                else
                {
                    current.Add(Escape(line));
                }
            }
            if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
            return string.Join("\n\n", paragraphs);
        }).Where(p => p.Length > 0);
        return string.Join("\n\n---\n\n", parts) + "\n";
    }

    public static string Table(List<List<string>> rows)
    {
        int width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (width == 0) return "";
        var padded = rows.Select(r => r.Concat(Enumerable.Repeat("", width - r.Count)).ToList()).ToList();
        var lines = new List<string>
        {
            "| " + string.Join(" | ", padded[0]) + " |",
            "|" + string.Concat(Enumerable.Repeat(" --- |", width)),
        };
        foreach (var row in padded.Skip(1)) lines.Add("| " + string.Join(" | ", row) + " |");
        return string.Join("\n", lines);
    }

    public static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '*' or '_' or '`' or '\\') result.Append('\\');
            result.Append(character);
        }
        return result.ToString();
    }

    /// <summary>Runs → Markdown, merging neighbouring runs with the same formatting.</summary>
    public static string Inlines(IEnumerable<Inline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var piece in InlineList.Merge(inlines))
        {
            var text = piece.Text.Replace("\t", " ").Replace(((char)0x2028).ToString(), " ");
            if (piece.Code)
            {
                builder.Append('`').Append(text.Replace("`", "'")).Append('`');
                continue;
            }
            var escaped = Escape(text);
            var core = escaped.Trim();
            if (core.Length == 0)
            {
                builder.Append(escaped);
                continue;
            }
            var wrapped = core.Replace("\n", " ");
            if (piece.Italic) wrapped = "*" + wrapped + "*";
            if (piece.Bold) wrapped = "**" + wrapped + "**";
            if (piece.Link != null) wrapped = "[" + wrapped + "](" + piece.Link.Replace(" ", "%20") + ")";
            int start = escaped.IndexOf(core, StringComparison.Ordinal);
            builder.Append(escaped, 0, start).Append(wrapped).Append(escaped, start + core.Length, escaped.Length - start - core.Length);
        }
        return builder.ToString().Trim();
    }
}
