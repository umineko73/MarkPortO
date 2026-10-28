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
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using A = DocumentFormat.OpenXml.Drawing;

namespace MarkPortO.Core.Ooxml;

/// <summary>The text of one slide as Markdown.</summary>
/// <param name="Number">1-based position of the slide in the presentation (hidden slides count).</param>
/// <param name="Hidden">The slide is hidden in the slide show.</param>
/// <param name="Title">The title text, or an empty string.</param>
/// <param name="Markdown">Title as a heading, then the body, as Markdown.</param>
public sealed record SlideContent(int Number, bool Hidden, string Title, string Markdown);

/// <summary>Reads the slides of a .pptx file (research R2). Speaker notes are not read.</summary>
public static class PptxReader
{
    /// <param name="includeHidden">Include hidden slides (FR-010a).</param>
    public static IReadOnlyList<SlideContent> Read(string path, bool includeHidden)
    {
        OoxmlPackage.EnsureOpens(path, DocumentKind.Pptx);
        try
        {
            using var document = PresentationDocument.Open(path, isEditable: false);
            var presentationPart = document.PresentationPart ?? throw new InvalidDocumentException(Loc.T("The presentation has no main part."));
            var slideIds = presentationPart.Presentation?.SlideIdList?.Elements<SlideId>().ToList() ?? [];

            var result = new List<SlideContent>();
            for (var i = 0; i < slideIds.Count; i++)
            {
                if (presentationPart.GetPartById(slideIds[i].RelationshipId!.Value!) is not SlidePart slidePart)
                {
                    continue;
                }

                var hidden = slidePart.Slide?.Show?.Value == false;
                if (hidden && !includeHidden)
                {
                    continue;
                }

                result.Add(ReadSlide(i + 1, hidden, slidePart));
            }

            return result;
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException)
        {
            throw new InvalidDocumentException(Loc.T("The presentation could not be read."), ex);
        }
    }

    /// <summary>The number of slides and which of them are hidden, in presentation order.</summary>
    public static IReadOnlyList<bool> ReadHiddenFlags(string path)
    {
        OoxmlPackage.EnsureOpens(path, DocumentKind.Pptx);
        using var document = PresentationDocument.Open(path, isEditable: false);
        var presentationPart = document.PresentationPart ?? throw new InvalidDocumentException(Loc.T("The presentation has no main part."));
        return
        [
            .. (presentationPart.Presentation?.SlideIdList?.Elements<SlideId>() ?? [])
                .Select(id => presentationPart.GetPartById(id.RelationshipId!.Value!) is SlidePart part && part.Slide?.Show?.Value == false),
        ];
    }

    private static SlideContent ReadSlide(int number, bool hidden, SlidePart part)
    {
        var tree = part.Slide?.CommonSlideData?.ShapeTree;
        var title = string.Empty;
        var body = new List<string>();

        if (tree is not null)
        {
            Walk(tree, ref title, body, part);
        }

        // Charts and SmartArt of the slide, then the speaker notes (FR-072).
        foreach (var chart in part.ChartParts.Select(ChartReader.Read).OfType<ChartInfo>())
        {
            body.Add(chart.ToMarkdown());
        }

        foreach (var diagram in part.DiagramDataParts)
        {
            if (DiagramText(diagram) is { Length: > 0 } diagramText)
            {
                body.Add(diagramText);
            }
        }

        if (NotesText(part) is { Length: > 0 } notes)
        {
            body.Add(notes);
        }

        var markdown = new StringBuilder();
        if (title.Length > 0)
        {
            markdown.Append("## ").Append(MarkdownText.Escape(title).Replace("\n", " ", StringComparison.Ordinal));
        }

        foreach (var part2 in body)
        {
            if (markdown.Length > 0)
            {
                markdown.Append("\n\n");
            }

            markdown.Append(part2);
        }

        return new SlideContent(number, hidden, title, markdown.ToString());
    }

    private static void Walk(OpenXmlElement container, ref string title, List<string> body, SlidePart part)
    {
        foreach (var element in container.ChildElements)
        {
            switch (element)
            {
                case Shape shape:
                    ReadShape(shape, ref title, body, part);
                    break;
                case GraphicFrame frame:
                    ReadTable(frame, body, part);
                    break;
                case GroupShape group:
                    Walk(group, ref title, body, part);
                    break;
                case Picture picture:
                    // A picture is written by its alternative text, without the image file (FR-072).
                    if (picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Description?.Value?.Trim() is { Length: > 0 } description)
                    {
                        body.Add("![" + description.Replace("]", "\\]", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "]");
                    }

                    break;
            }
        }
    }

    // The speaker notes of a slide, as a quote. The slide number and other placeholders of the notes page are left out.
    private static string NotesText(SlidePart part)
    {
        var tree = part.NotesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree;
        if (tree is null)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var shape in tree.Elements<Shape>())
        {
            var type = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;
            if (type != PlaceholderValues.Body || shape.TextBody is null)
            {
                continue;
            }

            lines.AddRange(shape.TextBody.Elements<A.Paragraph>().Select(p => ParagraphText(p).Trim().Replace("\n", " ", StringComparison.Ordinal)).Where(t => t.Length > 0));
        }

        return lines.Count == 0 ? string.Empty : string.Join("\n>\n", lines.Select(l => "> **Notes**: " + MarkdownText.Escape(l)));
    }

    // The text of the nodes of a SmartArt diagram, one line each (the shapes of a diagram are drawn from this data).
    private static string DiagramText(DiagramDataPart part)
    {
        var lines = (part.DataModelRoot?.Descendants<OpenXmlElement>() ?? [])
            .Where(e => e.LocalName == "pt" && e.GetAttributes().FirstOrDefault(a => a.LocalName == "type").Value is null or "node")
            .Select(point => string.Concat(point.Descendants<OpenXmlElement>().Where(e => e.LocalName == "t" && e.NamespaceUri.EndsWith("drawingml/2006/main", StringComparison.Ordinal)).Select(e => e.InnerText)).Trim())
            .Where(t => t.Length > 0)
            .ToList();
        return lines.Count == 0 ? string.Empty : string.Join("\n", lines.Select(l => "- " + MarkdownText.Escape(l).Replace("\n", " ", StringComparison.Ordinal)));
    }

    private static void ReadShape(Shape shape, ref string title, List<string> body, SlidePart part)
    {
        var textBody = shape.TextBody;
        if (textBody is null)
        {
            return;
        }

        var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
        var placeholderType = placeholder?.Type?.Value;
        var isTitle = placeholderType == PlaceholderValues.Title || placeholderType == PlaceholderValues.CenteredTitle;
        var isBody = placeholder is not null && !isTitle && placeholderType != PlaceholderValues.SlideNumber
            && placeholderType != PlaceholderValues.Footer && placeholderType != PlaceholderValues.DateAndTime;

        var lines = new List<string>();
        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            var text = ParagraphText(paragraph, part).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (isTitle)
            {
                lines.Add(text);
            }
            else if (isBody)
            {
                var level = paragraph.ParagraphProperties?.Level?.Value ?? 0;
                lines.Add($"{new string(' ', level * 2)}- {MarkdownText.Escape(text).Replace("\n", " ", StringComparison.Ordinal)}");
            }
            else
            {
                lines.Add(MarkdownText.Escape(text).Replace("\n", "  \n", StringComparison.Ordinal));
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        if (isTitle)
        {
            title = title.Length == 0 ? string.Join(' ', lines) : title + " " + string.Join(' ', lines);
        }
        else
        {
            body.Add(string.Join('\n', lines));
        }
    }

    private static void ReadTable(GraphicFrame frame, List<string> body, SlidePart part)
    {
        var table = frame.Descendants<A.Table>().FirstOrDefault();
        if (table is null)
        {
            return;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in table.Elements<A.TableRow>())
        {
            // The cells hidden by a merged cell are in the file; they are marked (FR-059).
            rows.Add([.. row.Elements<A.TableCell>().Select(cell =>
                cell.VerticalMerge?.Value == true ? MarkdownText.MergedUp
                : cell.HorizontalMerge?.Value == true ? MarkdownText.MergedLeft
                    : string.Join('\n', cell.TextBody?.Elements<A.Paragraph>().Select(p => ParagraphText(p, part).Trim()).Where(t => t.Length > 0) ?? []))]);
        }

        // FR-060: the first row is a header when the table has the "header row" option.
        var markdown = MarkdownText.Table(rows, table.TableProperties?.FirstRow?.Value == true);
        if (markdown.Length > 0)
        {
            body.Add(markdown);
        }
    }

    private static string? LinkOf(A.Run run, SlidePart part)
    {
        var id = run.RunProperties?.GetFirstChild<A.HyperlinkOnClick>()?.Id?.Value;
        var uri = id is null ? null : part.HyperlinkRelationships.FirstOrDefault(r => r.Id == id)?.Uri;
        return uri?.OriginalString.Replace(" ", "%20", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
    }

    // With the slide part, a run that has a link to the outside is written as a Markdown link (FR-072).
    private static string ParagraphText(A.Paragraph paragraph, SlidePart? part = null)
    {
        var builder = new StringBuilder();
        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case A.Run run when part is not null && LinkOf(run, part) is { } address && run.Text?.Text is { Length: > 0 } label:
                    builder.Append('[').Append(label.Replace("]", "\\]", StringComparison.Ordinal)).Append("](").Append(address).Append(')');
                    break;
                case A.Run run:
                    builder.Append(run.Text?.Text);
                    break;
                case A.Break:
                    builder.Append('\n');
                    break;
                case A.Field field:
                    builder.Append(field.Text?.Text);
                    break;
            }
        }

        return builder.ToString();
    }
}
