// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPortO.
//
// MarkPortO is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPortO.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using MarkPortO.Core.Localization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;

namespace MarkPortO.Core.Ooxml;

/// <summary>A paragraph or table of a docx body, with the plain text that is searched for in the PDF (see DocxPageAssigner).</summary>
/// <param name="Block">The block as Markdown. <see cref="Block.PageIndex"/> is not set yet.</param>
/// <param name="SearchText">Text without Markdown marks, used to find the block in the PDF text.</param>
public sealed record DocxBlock(Block Block, string SearchText);

/// <summary>The shapes of one paragraph of the body, with the block they belong to.</summary>
/// <param name="HostBlockIndex">
/// Index (in <see cref="DocxContent.Blocks"/>) of the block of the paragraph the shapes are anchored in. A paragraph
/// without text has no block of its own: it counts as part of the block before it (-1 before the first block).
/// </param>
public sealed record DocxShapes(int HostBlockIndex, IReadOnlyList<ShapeBox> Boxes);

/// <summary>What is read from the body of a .docx file.</summary>
public sealed record DocxContent(IReadOnlyList<DocxBlock> Blocks, IReadOnlyList<DocxShapes> Shapes);

/// <summary>Reads the body of a .docx file as Markdown blocks (research R2). The header and footer are not read.</summary>
public static partial class DocxReader
{
    private const double EmuPerTwip = 635;

    public static IReadOnlyList<DocxBlock> Read(string path) => ReadContent(path).Blocks;

    /// <summary>The blocks of the body, and the shapes drawn in it (for the deleted-page check, FR-012).</summary>
    public static DocxContent ReadContent(string path)
    {
        OoxmlPackage.EnsureOpens(path, DocumentKind.Docx);
        try
        {
            using var document = WordprocessingDocument.Open(path, isEditable: false);
            var main = document.MainDocumentPart ?? throw new InvalidDocumentException(Loc.T("The document has no main part."));
            var body = main.Document?.Body ?? throw new InvalidDocumentException(Loc.T("The document has no body."));

            var context = new Context(main);
            var blocks = new List<DocxBlock>();
            var shapes = new List<DocxShapes>();
            var (pageWidth, pageHeight) = PageSizeEmu(body);
            foreach (var element in body.ChildElements)
            {
                switch (element)
                {
                    case Paragraph paragraph:
                        AddParagraph(paragraph, context, blocks, shapes, pageWidth, pageHeight);
                        break;
                    case Table table:
                        AddTable(table, context, blocks);
                        AddNotes(context, blocks);
                        break;
                }
            }

            return new DocxContent(blocks, shapes);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException)
        {
            throw new InvalidDocumentException(Loc.T("The document could not be read."), ex);
        }
    }

    private sealed class Context(MainDocumentPart main)
    {
        private readonly Dictionary<string, (int? HeadingLevel, bool IsTitle)> _styles = BuildStyleMap(main);
        private int _footnoteNumber;
        private int _endnoteNumber;

        /// <summary>The notes that the paragraph being read refers to: written after it as <c>[^1]: text</c> (FR-071).</summary>
        public List<(string Label, string Text)> PendingNotes { get; } = [];

        /// <summary>The address of an outside link of the body, or null (a link inside the document has no address to write).</summary>
        public string? LinkTarget(string relationshipId)
        {
            var uri = main.HyperlinkRelationships.FirstOrDefault(r => r.Id == relationshipId)?.Uri;
            return uri?.OriginalString.Replace(" ", "%20", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
        }

        /// <summary>The mark <c>[^1]</c> for a reference to a footnote or an endnote; its text is kept for <see cref="PendingNotes"/>.</summary>
        public string NoteReference(long? id, bool endnote)
        {
            if (id is null)
            {
                return string.Empty;
            }

            IEnumerable<OpenXmlElement>? paragraphs = endnote
                ? main.EndnotesPart?.Endnotes?.Elements<Endnote>().FirstOrDefault(n => n.Id?.Value == id)?.Elements<Paragraph>()
                : main.FootnotesPart?.Footnotes?.Elements<Footnote>().FirstOrDefault(n => n.Id?.Value == id)?.Elements<Paragraph>();
            if (paragraphs is null)
            {
                return string.Empty;
            }

            var text = string.Join(" ", paragraphs.Select(p => ParagraphText(p, this).Trim()).Where(t => t.Length > 0));
            var label = endnote ? "e" + (++_endnoteNumber).ToString(System.Globalization.CultureInfo.InvariantCulture) : (++_footnoteNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
            PendingNotes.Add((label, text));
            return "[^" + label + "]";
        }

        public (int? HeadingLevel, bool IsTitle) StyleOf(string? styleId) =>
            styleId is not null && _styles.TryGetValue(styleId, out var style) ? style : (null, false);

        public bool IsBullet(NumberingProperties numbering)
        {
            var numberId = numbering.NumberingId?.Val?.Value;
            var level = numbering.NumberingLevelReference?.Val?.Value ?? 0;
            var part = main.NumberingDefinitionsPart?.Numbering;
            if (numberId is null || part is null)
            {
                return true;
            }

            var instance = part.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numberId);
            var abstractId = instance?.AbstractNumId?.Val?.Value;
            var abstractNum = part.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
            var levelDefinition = abstractNum?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
            var format = levelDefinition?.NumberingFormat?.Val?.Value;
            return format is null || format == NumberFormatValues.Bullet;
        }

        private static Dictionary<string, (int? HeadingLevel, bool IsTitle)> BuildStyleMap(MainDocumentPart main)
        {
            var map = new Dictionary<string, (int?, bool)>(StringComparer.OrdinalIgnoreCase);
            var styles = main.StyleDefinitionsPart?.Styles;
            if (styles is null)
            {
                return map;
            }

            foreach (var style in styles.Elements<Style>())
            {
                var id = style.StyleId?.Value;
                if (id is null)
                {
                    continue;
                }

                var name = style.StyleName?.Val?.Value ?? string.Empty;
                int? level = null;
                var match = HeadingName().Match(name);
                if (match.Success)
                {
                    level = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } outline && outline < 9)
                {
                    level = outline + 1;
                }

                map[id] = (level, name.Equals("title", StringComparison.OrdinalIgnoreCase));
            }

            return map;
        }
    }

    private static (double Width, double Height) PageSizeEmu(Body body)
    {
        var size = body.Elements<SectionProperties>().LastOrDefault()?.GetFirstChild<PageSize>();
        double width = size?.Width?.Value ?? 11906;
        double height = size?.Height?.Value ?? 16838;
        return (width * EmuPerTwip, height * EmuPerTwip);
    }

    private static void AddParagraph(
        Paragraph paragraph,
        Context context,
        List<DocxBlock> blocks,
        List<DocxShapes> shapes,
        double pageWidth,
        double pageHeight)
    {
        // The plain text finds the page of the block; the Markdown text also has links, note marks and the alternative text of pictures.
        var text = ParagraphText(paragraph).Trim();
        var markdown = ParagraphText(paragraph, context).Trim();
        if (text.Length > 0 || markdown.Length > 0)
        {
            var properties = paragraph.ParagraphProperties;
            var (headingLevel, isTitle) = context.StyleOf(properties?.ParagraphStyleId?.Val?.Value);
            var escaped = MarkdownText.Escape(markdown).Replace("\n", "  \n", StringComparison.Ordinal);

            if (headingLevel is not null || isTitle)
            {
                var level = Math.Clamp(headingLevel ?? 1, 1, 6);
                blocks.Add(New(BlockKind.Heading, $"{new string('#', level)} {escaped.Replace("  \n", " ", StringComparison.Ordinal)}", text));
            }
            else if (properties?.NumberingProperties is { } numbering)
            {
                var depth = numbering.NumberingLevelReference?.Val?.Value ?? 0;
                var marker = context.IsBullet(numbering) ? "-" : "1.";
                blocks.Add(New(BlockKind.ListItem, $"{new string(' ', depth * 2)}{marker} {escaped}", text));
            }
            else
            {
                blocks.Add(New(BlockKind.Paragraph, escaped, text));
            }
        }

        AddNotes(context, blocks);

        // Shapes (for the "×" check) belong to the block of this paragraph, or to the one before it if it has no text.
        var boxes = ShapeExtractor.Extract(paragraph, pageWidth, pageHeight);
        if (boxes.Count > 0)
        {
            shapes.Add(new DocxShapes(blocks.Count - 1, boxes));
        }

        // Text boxes anchored in this paragraph follow it as paragraphs of their own.
        foreach (var box in TextBoxes(paragraph))
        {
            foreach (var inner in box.Elements<Paragraph>())
            {
                var innerText = ParagraphText(inner).Trim();
                if (innerText.Length > 0)
                {
                    blocks.Add(New(BlockKind.Paragraph, MarkdownText.Escape(innerText).Replace("\n", "  \n", StringComparison.Ordinal), innerText));
                }
            }
        }
    }

    // The notes that the last paragraph or table referred to, as blocks of their own: [^1]: text. Their page is the page before them.
    private static void AddNotes(Context context, List<DocxBlock> blocks)
    {
        foreach (var (label, text) in context.PendingNotes)
        {
            if (text.Length > 0)
            {
                blocks.Add(New(BlockKind.Paragraph, "[^" + label + "]: " + MarkdownText.Escape(text).Replace("\n", " ", StringComparison.Ordinal), string.Empty));
            }
        }

        context.PendingNotes.Clear();
    }

    // The text of a table cell: its paragraphs, and a table inside it written as lines of cells (FR-071).
    private static string CellText(TableCell cell, Context context)
    {
        var parts = new List<string>();
        foreach (var child in cell.ChildElements)
        {
            switch (child)
            {
                case Paragraph paragraph:
                    parts.Add(ParagraphText(paragraph, context).Trim());
                    break;
                case Table inner:
                    parts.AddRange(inner.Elements<TableRow>().Select(row =>
                        string.Join(" / ", row.Elements<TableCell>().Select(c => CellText(c, context).Replace("\n", " ", StringComparison.Ordinal)))));
                    break;
            }
        }

        return string.Join('\n', parts.Where(t => t.Length > 0));
    }

    private static void AddTable(Table table, Context context, List<DocxBlock> blocks)
    {
        var rows = new List<IReadOnlyList<string>>();
        string? firstCell = null;
        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var text = CellText(cell, context);
                if (firstCell is null && text.Length > 0)
                {
                    // The page of the table is found by plain text, not by the Markdown (links, note marks).
                    firstCell = string.Join('\n', cell.Elements<Paragraph>().Select(p => ParagraphText(p).Trim()).Where(t => t.Length > 0));
                    if (firstCell.Length == 0)
                    {
                        firstCell = null;
                    }
                }

                // The rest of a vertical merge has no text of its own; a cell that spans columns is followed by the columns it covers (FR-059).
                var properties = cell.TableCellProperties;
                var continues = properties?.VerticalMerge is { } vertical && (vertical.Val is null || vertical.Val.Value == MergedCellValues.Continue);
                // The rest of a vertical merge is in the second row or below of its range (^); the columns that a cell spans are "<" in the first row.
                cells.Add(continues ? MarkdownText.MergedUp : text);
                var span = Math.Clamp(properties?.GridSpan?.Val?.Value ?? 1, 1, 1000);
                for (var i = 1; i < span; i++)
                {
                    cells.Add(continues ? MarkdownText.MergedUp : MarkdownText.MergedLeft);
                }
            }

            rows.Add(cells);
        }

        var markdown = MarkdownText.Table(rows, HasHeaderRow(table));
        if (markdown.Length > 0)
        {
            // The page of a table is the page of its first cell: the order in which a PDF lists the other cells varies.
            blocks.Add(New(BlockKind.Table, markdown, firstCell ?? string.Empty));
        }
    }

    // FR-060: the first row is a header when the table says so (a repeated header row, or the "header row" of its style).
    private static bool HasHeaderRow(Table table)
    {
        if (table.Elements<TableRow>().FirstOrDefault()?.TableRowProperties?.Elements<TableHeader>().FirstOrDefault() is { } repeated
            && (repeated.Val is null || repeated.Val.Value != OnOffOnlyValues.Off))
        {
            return true;
        }

        var look = table.GetFirstChild<TableProperties>()?.TableLook;
        if (look?.FirstRow is { } firstRow)
        {
            return firstRow.Value;
        }

        // The old form: a bit mask in w:val (0x0020 is the first row).
        return look?.Val?.Value is { } mask
            && int.TryParse(mask, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var bits)
            && (bits & 0x0020) != 0;
    }

    private static DocxBlock New(BlockKind kind, string markdown, string searchText) =>
        new(new Block { Kind = kind, Markdown = markdown }, searchText);

    /// <summary>The text of a paragraph. A line break is a newline; the text of a text box is not included.</summary>
    private static string ParagraphText(OpenXmlElement paragraph, Context? markdown = null)
    {
        var builder = new StringBuilder();
        Append(paragraph, builder, markdown);
        return builder.ToString();
    }

    // With a context the text is Markdown (links, note marks, the alternative text of pictures); without it, plain text.
    private static void Append(OpenXmlElement element, StringBuilder builder, Context? markdown = null)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
                case Hyperlink link when markdown is not null && link.Id?.Value is { } relationshipId && markdown.LinkTarget(relationshipId) is { } address:
                    var label = new StringBuilder();
                    Append(link, label, markdown);
                    if (label.ToString().Trim().Length > 0)
                    {
                        builder.Append('[').Append(label.ToString().Trim().Replace("]", "\\]", StringComparison.Ordinal)).Append("](").Append(address).Append(')');
                    }

                    break;
                case FootnoteReference note when markdown is not null:
                    builder.Append(markdown.NoteReference(note.Id?.Value, endnote: false));
                    break;
                case EndnoteReference note when markdown is not null:
                    builder.Append(markdown.NoteReference(note.Id?.Value, endnote: true));
                    break;
                case Drawing drawing when markdown is not null:
                    // A picture is written by its alternative text, without the image file (FR-071).
                    var description = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().FirstOrDefault()?.Description?.Value?.Trim();
                    if (!string.IsNullOrEmpty(description) && drawing.Descendants<OpenXmlElement>().Any(e => e.LocalName == "blip"))
                    {
                        builder.Append("![").Append(description.Replace("]", "\\]", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)).Append(']');
                    }

                    Append(child, builder, markdown);
                    break;
                case Text text:
                    builder.Append(text.Text);
                    break;
                case TabChar:
                    builder.Append(' ');
                    break;
                case Break lineBreak when !IsPageOrColumnBreak(lineBreak):
                    builder.Append('\n');
                    break;
                case TextBoxContent:
                    break; // written as its own blocks
                case DocumentFormat.OpenXml.AlternateContent alternate:
                    // Choice and Fallback hold the same text twice: use the first.
                    if (alternate.Elements<AlternateContentChoice>().FirstOrDefault() is { } choice)
                    {
                        Append(choice, builder, markdown);
                    }

                    break;
                case DeletedRun:
                case DeletedText:
                    break; // text removed by a tracked change
                default:
                    Append(child, builder, markdown);
                    break;
            }
        }
    }

    private static bool IsPageOrColumnBreak(Break b) =>
        b.Type?.Value == BreakValues.Page || b.Type?.Value == BreakValues.Column;

    private static IEnumerable<TextBoxContent> TextBoxes(Paragraph paragraph)
    {
        foreach (var box in paragraph.Descendants<TextBoxContent>())
        {
            // Skip the Fallback copy of an AlternateContent: the Choice holds the same text.
            if (box.Ancestors<AlternateContentFallback>().Any())
            {
                continue;
            }

            if (box.Ancestors<TextBoxContent>().Any())
            {
                continue; // nested boxes are not unfolded
            }

            yield return box;
        }
    }

    [GeneratedRegex(@"^\s*heading\s*([1-9])\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingName();
}
