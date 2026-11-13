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
    /// <summary>The size of the slide (EMU), and whether the text of the shapes that are outside of it is written (FR-086).</summary>
    private sealed record SlideContext(long Width, long Height, bool IncludeOffSlide, PictureCollector? Pictures)
    {
        /// <summary>The drawn shapes of the slide that is being read, to be cut out as one picture where they are near one another (FR-136).</summary>
        public List<PendingCut>? Cuts { get; init; }

        /// <summary>The pictures of the slide, written when the cuts are placed: one that lies within a cut shape is in its picture already (FR-133, FR-136).</summary>
        public List<PendingPicture> Pictures2 { get; init; } = [];

        /// <summary>Inside a group that is cut out as one picture: its pictures are in that picture.</summary>
        public bool InCutGroup { get; init; }
    }

    /// <summary>A picture of the slide that is not yet written: where it is (EMU), how to collect it, its alternative text and the place of its mark in the body.</summary>
    private sealed record PendingPicture((double Left, double Top, double Right, double Bottom)? Box, Func<string?> Collect, string? Description, int BodyIndex);

    /// <summary>A drawn shape that may be cut out: where it is on the slide (in EMU, with its turn), the place of its mark in the body, and its alternative text.</summary>
    private sealed record PendingCut(double X, double Y, double Width, double Height, double Degrees, string? Description, int BodyIndex);

    /// <summary>The mark of a cut that has been merged into the one before it (it is taken out of the body).</summary>
    private const string MergedCut = "merged";

    /// <param name="includeHidden">Include hidden slides (FR-010a).</param>
    /// <param name="includeOffSlideText">Write the text of the shapes that lie outside the slide, as a quote of its own (FR-086); when false they are left out.</param>
    /// <param name="emphasis">Mark text in bold or italic (FR-101); a paragraph all in bold (italic), and a title, is not marked.</param>
    /// <param name="pictures">Collects the pictures, which are then written with the mark of their file (FR-133); null: a picture is written by its alternative text only.</param>
    public static IReadOnlyList<SlideContent> Read(string path, bool includeHidden, bool includeOffSlideText = true, bool emphasis = false, PictureCollector? pictures = null)
    {
        OoxmlPackage.EnsureOpens(path, DocumentKind.Pptx);
        _marking = emphasis;
        try
        {
            using var document = PresentationDocument.Open(path, isEditable: false);
            var presentationPart = document.PresentationPart ?? throw new InvalidDocumentException(Loc.T("The presentation has no main part."));
            var slideIds = presentationPart.Presentation?.SlideIdList?.Elements<SlideId>().ToList() ?? [];

            var size = presentationPart.Presentation?.SlideSize;
            var context = new SlideContext(size?.Cx?.Value ?? 9144000, size?.Cy?.Value ?? 6858000, includeOffSlideText, pictures);
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

                result.Add(ReadSlide(i + 1, hidden, slidePart, context));
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

    private static SlideContent ReadSlide(int number, bool hidden, SlidePart part, SlideContext context)
    {
        var tree = part.Slide?.CommonSlideData?.ShapeTree;
        var title = string.Empty;
        var body = new List<string>();
        context = context with { Cuts = context.Pictures is { CutShapes: true } ? [] : null, Pictures2 = [] };

        if (tree is not null)
        {
            Walk(tree, ref title, body, part, context, off: false, check: true);
            PlaceShapeCuts(body, context);
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

    // off: the container is outside the slide. check: the position of the shapes is the position on the slide (not inside a group, where it is the group's own).
    private static void Walk(OpenXmlElement container, ref string title, List<string> body, SlidePart part, SlideContext context, bool off, bool check)
    {
        foreach (var element in container.ChildElements)
        {
            switch (element)
            {
                case Shape shape:
                    if (!(off || (check && IsOffSlide(shape.ShapeProperties?.Transform2D?.Offset, shape.ShapeProperties?.Transform2D?.Extents, context))))
                    {
                        // A drawn shape on the slide is cut out of the image of the slide, before its text (FR-136).
                        if (check && IsDrawnShape(shape))
                        {
                            AddShapeCut(shape.ShapeProperties?.Transform2D, shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Description?.Value, body, context);
                        }

                        ReadShape(shape, ref title, body, part);
                    }
                    else if (context.IncludeOffSlide)
                    {
                        ReadOffSlideShape(shape, body, part);
                    }

                    break;
                case GraphicFrame frame:
                    if (context.IncludeOffSlide || !(off || (check && IsOffSlide(frame.Transform?.Offset, frame.Transform?.Extents, context))))
                    {
                        ReadTable(frame, body, part);
                    }

                    break;
                case ConnectionShape connector:
                    // A connector has no text; a long one that is not straight across (a diagonal) is a drawn shape too (FR-136).
                    if (check && !off && !IsOffSlide(connector.ShapeProperties?.Transform2D?.Offset, connector.ShapeProperties?.Transform2D?.Extents, context))
                    {
                        AddShapeCut(connector.ShapeProperties?.Transform2D, connector.NonVisualConnectionShapeProperties?.NonVisualDrawingProperties?.Description?.Value, body, context);
                    }

                    break;
                case GroupShape group:
                    var groupOff = off || (check && IsOffSlide(group.GroupShapeProperties?.TransformGroup?.Offset, group.GroupShapeProperties?.TransformGroup?.Extents, context));

                    // A group on the slide is cut out as one picture, before the texts of its shapes (FR-136); its shapes are not cut out one by one.
                    if (check && !groupOff)
                    {
                        var transform = group.GroupShapeProperties?.TransformGroup;
                        AddShapeCut(transform?.Offset, transform?.Extents, transform?.Rotation?.Value, group.NonVisualGroupShapeProperties?.NonVisualDrawingProperties?.Description?.Value, body, context);
                    }

                    if (!groupOff || context.IncludeOffSlide)
                    {
                        // The pictures in a group that is cut out are in the picture of the group.
                        var groupSize = group.GroupShapeProperties?.TransformGroup?.Extents;
                        var cutAsGroup = check && !groupOff && context.Cuts is not null
                            && groupSize?.Cx?.Value >= PictureCollector.MinShapeSidePoints * EmuPerPoint && groupSize.Cy?.Value >= PictureCollector.MinShapeSidePoints * EmuPerPoint;
                        Walk(group, ref title, body, part, cutAsGroup ? context with { InCutGroup = true } : context, groupOff, check: false);
                    }

                    break;
                case Picture picture:
                    if (!context.IncludeOffSlide && (off || (check && IsOffSlide(picture.ShapeProperties?.Transform2D?.Offset, picture.ShapeProperties?.Transform2D?.Extents, context))))
                    {
                        break;
                    }

                    // A picture is written by its alternative text (FR-072), and with the mark of its file when the pictures are saved (FR-133).
                    // A video or a sound is not a picture: the picture of it is only its cover.
                    var drawingProperties = picture.NonVisualPictureProperties?.NonVisualDrawingProperties;
                    if (context.InCutGroup)
                    {
                        break;
                    }

                    if (check && context.Cuts is not null)
                    {
                        // Written when the shapes of the slide are placed: a picture that lies within a cut shape is not written twice.
                        var offset = picture.ShapeProperties?.Transform2D?.Offset;
                        var extents = picture.ShapeProperties?.Transform2D?.Extents;
                        (double, double, double, double)? box = offset?.X?.Value is { } px && offset.Y?.Value is { } py && extents?.Cx?.Value is { } pw && extents.Cy?.Value is { } ph ? ((double)px, (double)py, (double)(px + pw), (double)(py + ph)) : null;
                        var embed = picture.BlipFill?.Blip?.Embed?.Value;
                        var name = drawingProperties?.Name?.Value;
                        var isMedia = IsMedia(picture);
                        context.Pictures2.Add(new PendingPicture(box, () => context.Pictures is not null && !isMedia ? context.Pictures.Add(part, embed, name) : null, drawingProperties?.Description?.Value, body.Count));
                        body.Add(string.Empty);
                        break;
                    }

                    string? mark = null;
                    if (context.Pictures is not null && !IsMedia(picture))
                    {
                        mark = context.Pictures.Add(part, picture.BlipFill?.Blip?.Embed?.Value, drawingProperties?.Name?.Value);
                    }

                    if (PictureCollector.Markdown(drawingProperties?.Description?.Value, mark) is { Length: > 0 } pictureMarkdown)
                    {
                        body.Add(pictureMarkdown);
                    }

                    break;
            }
        }
    }

    /// <summary>A shape that is drawn, not a place for text: not a placeholder of the layout, not a text box (FR-136).</summary>
    private static bool IsDrawnShape(Shape shape) =>
        shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape is null
        && shape.NonVisualShapeProperties?.NonVisualShapeDrawingProperties?.TextBox?.Value != true;

    private static void AddShapeCut(A.Transform2D? transform, string? description, List<string> body, SlideContext context) =>
        AddShapeCut(transform?.Offset, transform?.Extents, transform?.Rotation?.Value, description, body, context);

    /// <summary>
    /// Notes a drawn shape that may be cut out of the image of the slide (FR-136). Its mark is written by <see cref="PlaceShapeCuts"/> when the whole slide
    /// has been read, because shapes that are near one another are cut out as one picture.
    /// </summary>
    private static void AddShapeCut(A.Offset? offset, A.Extents? extents, int? rotation, string? description, List<string> body, SlideContext context)
    {
        if (context.Cuts is null || offset?.X is null || offset.Y is null || extents?.Cx is null || extents.Cy is null)
        {
            return;
        }

        context.Cuts.Add(new PendingCut(offset.X.Value, offset.Y.Value, extents.Cx.Value, extents.Cy.Value, (rotation ?? 0) / 60000.0, description, body.Count));
        body.Add(string.Empty); // the place of its mark
    }

    /// <summary>
    /// Writes the marks of the drawn shapes of a slide. Shapes that are within <see cref="ClusterGapPoints"/> of one another (a loose group: boxes and the arrows
    /// between them) are cut out as one picture, in the place of the first of them. A shape that is alone is cut out when it is half an inch or more on each side;
    /// a shape that covers most of the slide (a background) is always alone.
    /// </summary>
    private static void PlaceShapeCuts(List<string> body, SlideContext context)
    {
        if (context.Cuts is null || context.Pictures is not { CutShapes: true } pictures)
        {
            return;
        }

        var cuts = context.Cuts;
        var placed = new List<(double Left, double Top, double Right, double Bottom)>();

        var minimum = PictureCollector.MinShapeSidePoints * EmuPerPoint;
        var gap = ClusterGapPoints * EmuPerPoint;
        var boxes = cuts.Select(c =>
        {
            // The box that holds the turned shape.
            var radians = c.Degrees * Math.PI / 180;
            var width = Math.Abs(c.Width * Math.Cos(radians)) + Math.Abs(c.Height * Math.Sin(radians));
            var height = Math.Abs(c.Width * Math.Sin(radians)) + Math.Abs(c.Height * Math.Cos(radians));
            var centreX = c.X + c.Width / 2;
            var centreY = c.Y + c.Height / 2;
            return (Left: centreX - width / 2, Top: centreY - height / 2, Right: centreX + width / 2, Bottom: centreY + height / 2);
        }).ToList();

        bool Backdrop(int i) => (boxes[i].Right - boxes[i].Left) * (boxes[i].Bottom - boxes[i].Top) >= BackdropShare * context.Width * context.Height;

        // Shapes that are near one another (the gap between their boxes is small) are one cluster.
        var cluster = Enumerable.Range(0, cuts.Count).ToArray();
        int Root(int i)
        {
            while (cluster[i] != i)
            {
                i = cluster[i] = cluster[cluster[i]];
            }

            return i;
        }

        for (var i = 0; i < cuts.Count; i++)
        {
            for (var j = i + 1; j < cuts.Count; j++)
            {
                if (!Backdrop(i) && !Backdrop(j)
                    && boxes[i].Left - gap <= boxes[j].Right && boxes[j].Left - gap <= boxes[i].Right
                    && boxes[i].Top - gap <= boxes[j].Bottom && boxes[j].Top - gap <= boxes[i].Bottom)
                {
                    cluster[Root(j)] = Root(i);
                }
            }
        }

        foreach (var members in Enumerable.Range(0, cuts.Count).GroupBy(Root))
        {
            var list = members.ToList();
            var left = list.Min(i => boxes[i].Left);
            var top = list.Min(i => boxes[i].Top);
            var right = list.Max(i => boxes[i].Right);
            var bottom = list.Max(i => boxes[i].Bottom);

            // One shape is cut out under the old rule (its own turn, half an inch); several are cut out as the box that holds all of them.
            ShapeArea? area;
            if (list.Count == 1)
            {
                var only = cuts[list[0]];
                area = only.Width < minimum || only.Height < minimum
                    ? null
                    : ShapeAreas.Of(only.X, only.Y, only.Width, only.Height, only.Degrees, RoomPoints * EmuPerPoint, context.Width, context.Height);
            }
            else
            {
                area = right - left < minimum || bottom - top < minimum
                    ? null
                    : ShapeAreas.Of(left, top, right - left, bottom - top, 0, RoomPoints * EmuPerPoint, context.Width, context.Height);
            }

            foreach (var index in list)
            {
                body[cuts[index].BodyIndex] = MergedCut;
            }

            if (area is not null)
            {
                placed.Add(list.Count == 1 ? boxes[list[0]] : (left, top, right, bottom));
                var description = list.Select(i => cuts[i].Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
                body[cuts[list[0]].BodyIndex] = PictureCollector.Markdown(description, pictures.AddShape(area));
            }
        }

        // A picture is written unless it lies within the picture of a cut shape (a little room is allowed).
        foreach (var picture in context.Pictures2)
        {
            var inside = picture.Box is { } b && placed.Any(p => b.Left >= p.Left - gap / 4 && b.Top >= p.Top - gap / 4 && b.Right <= p.Right + gap / 4 && b.Bottom <= p.Bottom + gap / 4);
            var mark = inside ? null : picture.Collect();
            var markdown = inside ? string.Empty : PictureCollector.Markdown(picture.Description, mark);
            body[picture.BodyIndex] = markdown.Length > 0 ? markdown : MergedCut;
        }

        body.RemoveAll(b => b == MergedCut);
    }

    /// <summary>Drawn shapes that are this near one another, in points, are cut out as one picture (a quarter of an inch).</summary>
    private const double ClusterGapPoints = 18;

    /// <summary>A shape that covers this share of the slide is a background: it does not join the shapes on it.</summary>
    private const double BackdropShare = 0.4;

    private const double EmuPerPoint = 12700;

    /// <summary>The room around a cut shape, in points, for its line and its shadow.</summary>
    private const double RoomPoints = 4;

    private static bool IsMedia(Picture picture) =>
        picture.NonVisualPictureProperties?.ApplicationNonVisualDrawingProperties?.Descendants<OpenXmlElement>()
            .Any(e => e.LocalName is "videoFile" or "audioFile" or "quickTimeFile" or "media") == true;

    // A shape that lies wholly outside the slide: a memo on the pasteboard. A shape that is partly on the slide is seen on it, so it is not one.
    private static bool IsOffSlide(A.Offset? offset, A.Extents? extents, SlideContext context)
    {
        if (offset?.X is null || offset.Y is null || extents?.Cx is null || extents.Cy is null)
        {
            return false; // no place of its own (a placeholder takes it from the layout)
        }

        long x = offset.X.Value, y = offset.Y.Value, width = extents.Cx.Value, height = extents.Cy.Value;
        return width > 0 && height > 0 && (x + width <= 0 || y + height <= 0 || x >= context.Width || y >= context.Height);
    }

    // The text of a shape outside the slide, as a quote that says so (FR-086): it is not in the image of the slide.
    private static void ReadOffSlideShape(Shape shape, List<string> body, SlidePart part)
    {
        var ignoredTitle = string.Empty;
        var lines = new List<string>();
        ReadShape(shape, ref ignoredTitle, lines, part);
        var texts = lines.SelectMany(block => block.Split('\n')).Select(line => line.Trim().TrimStart('-', ' ')).Where(line => line.Length > 0).ToList();
        if (texts.Count == 0 && ignoredTitle.Length > 0)
        {
            texts.Add(MarkdownText.Escape(ignoredTitle));
        }

        if (texts.Count > 0)
        {
            body.Add(string.Join("\n>\n", texts.Select(text => "> **Off-slide**: " + text)));
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
    internal static string DiagramText(DiagramDataPart part)
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
        var listLine = new List<bool>();
        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            var text = ParagraphText(paragraph, part, allowEmphasis: !isTitle).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            // A text box has a bullet only when the paragraph says so (a bullet character or a number of its own).
            var bulleted = paragraph.ParagraphProperties?.GetFirstChild<A.CharacterBullet>() is not null
                || paragraph.ParagraphProperties?.GetFirstChild<A.AutoNumberedBullet>() is not null;
            if (isTitle)
            {
                lines.Add(text);
                listLine.Add(false);
            }
            else if (isBody || bulleted)
            {
                var level = paragraph.ParagraphProperties?.Level?.Value ?? 0;
                lines.Add($"{new string(' ', level * 2)}- {MarkdownText.Escape(text).Replace("\n", " ", StringComparison.Ordinal)}");
                listLine.Add(true);
            }
            else
            {
                lines.Add(MarkdownText.Escape(text).Replace("\n", "  \n", StringComparison.Ordinal));
                listLine.Add(false);
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
            // The paragraphs of a text box are paragraphs (a blank line between them: one newline joins them in Markdown); the items of a list go together.
            var text = new StringBuilder(lines[0]);
            for (var i = 1; i < lines.Count; i++)
            {
                text.Append(listLine[i] && listLine[i - 1] ? "\n" : "\n\n").Append(lines[i]);
            }

            body.Add(text.ToString());
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

    // Whether the slides that are being read are written with the marks of bold and italic (set by Read; the reading of a file is on one thread).
    [ThreadStatic]
    private static bool _marking;

    private static bool IsBold(A.Run run) => run.RunProperties?.Bold?.Value == true;

    private static bool IsItalic(A.Run run) => run.RunProperties?.Italic?.Value == true;

    // With the slide part, a run that has a link to the outside is written as a Markdown link (FR-072).
    private static string ParagraphText(A.Paragraph paragraph, SlidePart? part = null, bool allowEmphasis = true)
    {
        var builder = new StringBuilder();
        var runs = _marking && part is not null && allowEmphasis
            ? paragraph.Elements<A.Run>().Where(r => !string.IsNullOrWhiteSpace(r.Text?.Text)).ToList()
            : [];
        var boldAllowed = runs.Count > 0 && !runs.All(IsBold);
        var italicAllowed = runs.Count > 0 && !runs.All(IsItalic);
        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case A.Run run when part is not null && LinkOf(run, part) is { } address && run.Text?.Text is { Length: > 0 } label:
                    builder.Append('[').Append(Marked(run, part, label.Replace("]", "\\]", StringComparison.Ordinal), boldAllowed, italicAllowed)).Append("](").Append(address).Append(')');
                    break;
                case A.Run run:
                    builder.Append(Marked(run, part, run.Text?.Text ?? string.Empty, boldAllowed, italicAllowed));
                    break;
                case A.Break:
                    builder.Append('\n');
                    break;
                case A.Field field:
                    builder.Append(field.Text?.Text);
                    break;
            }
        }

        return part is null ? builder.ToString() : TextEmphasis.Merge(Strikethrough.Merge(builder.ToString()));
    }

    // A run with a strikethrough is written between ~~ (FR-092), bold between ** and italic between * (FR-101); in the plain text (no slide part) it is as it is.
    private static string Marked(A.Run run, SlidePart? part, string text, bool boldAllowed, bool italicAllowed)
    {
        if (part is null)
        {
            return text;
        }

        if (italicAllowed && IsItalic(run))
        {
            text = TextEmphasis.Italic(text);
        }

        if (boldAllowed && IsBold(run))
        {
            text = TextEmphasis.Bold(text);
        }

        return run.RunProperties?.Strike?.Value is { } strike && strike != A.TextStrikeValues.NoStrike ? Strikethrough.Wrap(text) : text;
    }
}
