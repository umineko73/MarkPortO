// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using MarkPort.Core.Localization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Markdown;

namespace MarkPort.Core.Ooxml;

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
                        AddTable(table, blocks);
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
        var text = ParagraphText(paragraph).Trim();
        if (text.Length > 0)
        {
            var properties = paragraph.ParagraphProperties;
            var (headingLevel, isTitle) = context.StyleOf(properties?.ParagraphStyleId?.Val?.Value);
            var escaped = MarkdownText.Escape(text).Replace("\n", "  \n", StringComparison.Ordinal);

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

    private static void AddTable(Table table, List<DocxBlock> blocks)
    {
        var rows = new List<IReadOnlyList<string>>();
        string? firstCell = null;
        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var text = string.Join('\n', cell.Elements<Paragraph>().Select(ParagraphText).Select(t => t.Trim()).Where(t => t.Length > 0));
                cells.Add(text);
                if (firstCell is null && text.Length > 0)
                {
                    firstCell = text;
                }
            }

            rows.Add(cells);
        }

        var markdown = MarkdownText.Table(rows);
        if (markdown.Length > 0)
        {
            // The page of a table is the page of its first cell: the order in which a PDF lists the other cells varies.
            blocks.Add(New(BlockKind.Table, markdown, firstCell ?? string.Empty));
        }
    }

    private static DocxBlock New(BlockKind kind, string markdown, string searchText) =>
        new(new Block { Kind = kind, Markdown = markdown }, searchText);

    /// <summary>The text of a paragraph. A line break is a newline; the text of a text box is not included.</summary>
    private static string ParagraphText(OpenXmlElement paragraph)
    {
        var builder = new StringBuilder();
        Append(paragraph, builder);
        return builder.ToString();
    }

    private static void Append(OpenXmlElement element, StringBuilder builder)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
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
                        Append(choice, builder);
                    }

                    break;
                case DeletedRun:
                case DeletedText:
                    break; // text removed by a tracked change
                default:
                    Append(child, builder);
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
