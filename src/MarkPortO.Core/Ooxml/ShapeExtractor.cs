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

using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Reads the drawn shapes (the <c>wps</c> shapes of a Word drawing) of a paragraph. The XML is read by name, so no
/// Office 2010 type library is needed. Pictures, charts, and the old VML shapes are not looked at.
/// </summary>
internal static class ShapeExtractor
{
    private const double EmuPerTwip = 635;

    /// <summary>
    /// The drawings of a paragraph that can be cut out of the page image (FR-136), with where each is on its page as far as the document says.
    /// An anchored drawing placed from the page or a margin is placed on the page; one placed from the paragraph or the line, and a drawing in the
    /// line (an inline drawing canvas), is placed only as far down as the paragraph, which only the PDF shows (<see cref="WordDrawing.FollowsParagraph"/>).
    /// Inline pictures are not taken: they stay pictures of the text.
    /// </summary>
    public static IReadOnlyList<WordDrawing> Drawings(Paragraph paragraph)
    {
        var drawings = new List<WordDrawing>();
        var page = PageOf(paragraph);
        foreach (var drawing in paragraph.Descendants<Drawing>())
        {
            if (IsLeftOut(drawing))
            {
                continue;
            }

            XElement root;
            try
            {
                root = XElement.Parse(drawing.OuterXml);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            var placement = root.Elements().FirstOrDefault(e => e.Name.LocalName is "anchor" or "inline");
            var extent = placement?.Elements().FirstOrDefault(e => e.Name.LocalName == "extent");
            var width = Number(extent, "cx");
            var height = Number(extent, "cy");
            if (placement is null || width <= 0 || height <= 0)
            {
                continue;
            }

            var hasShape = root.Descendants().Any(e => e.Name.LocalName == "wsp");
            var hasPicture = root.Descendants().Any(e => e.Name.LocalName == "pic");
            var boxes = Extract(root, page.Width, page.Height);

            // What the drawing draws outside its box (a shadow, a glow, a thick line).
            var effect = placement.Elements().FirstOrDefault(e => e.Name.LocalName == "effectExtent");
            double effectLeft = Math.Max(0, Number(effect, "l")), effectTop = Math.Max(0, Number(effect, "t"));
            width += effectLeft + Math.Max(0, Number(effect, "r"));
            height += effectTop + Math.Max(0, Number(effect, "b"));

            if (placement.Name.LocalName == "inline")
            {
                if (hasShape)
                {
                    drawings.Add(new WordDrawing(drawing, null, 0, width, height, FollowsParagraph: true, boxes, hasPicture, Anchored: false, page));
                }

                continue;
            }

            double? x;
            double y;
            bool follows;
            if (Flag(placement, "simplePos"))
            {
                var simple = placement.Elements().FirstOrDefault(e => e.Name.LocalName == "simplePos");
                x = Number(simple, "x");
                y = Number(simple, "y");
                follows = false;
            }
            else
            {
                var horizontal = placement.Elements().FirstOrDefault(e => e.Name.LocalName == "positionH");
                var vertical = placement.Elements().FirstOrDefault(e => e.Name.LocalName == "positionV");
                x = Horizontal(horizontal, width, page);
                (y, follows) = Vertical(vertical, height, page);
            }

            drawings.Add(new WordDrawing(drawing, x - effectLeft, y - effectTop, width, height, follows, boxes, hasPicture, Anchored: true, page));
        }

        return drawings;
    }

    // A drawing that is not on the page as the body shows it: in a text box (it is a drawing of the text box), hidden, or deleted with tracked changes.
    private static bool IsLeftOut(Drawing drawing) =>
        drawing.Ancestors<TextBoxContent>().Any() || drawing.Ancestors<DeletedRun>().Any() || drawing.ChildElements.Any(HiddenObjects.IsHidden);

    // Where a drawing is across the page (EMU from the left edge); null when it is not known: placed from a character, or from a column of a page
    // of several columns (where the character or the column is, only the PDF shows).
    private static double? Horizontal(XElement? position, double width, WordPage page)
    {
        var from = position?.Attribute("relativeFrom")?.Value ?? "column";
        if (from == "character" || from == "column" && page.Columns > 1)
        {
            return null;
        }

        var (start, size) = from switch
        {
            "page" => (0.0, page.Width),
            "leftMargin" or "insideMargin" => (0.0, page.Left),
            "rightMargin" or "outsideMargin" => (page.Width - page.Right, page.Right),
            _ => (page.Left, page.Width - page.Left - page.Right), // margin, and the one column: the text area
        };

        return Place(position, start, size, width, "left", "center", "right") is { } place ? place : null;
    }

    // Where a drawing is down the page (EMU from the top edge), or, when it follows the paragraph, down from the top of the paragraph.
    private static (double Y, bool Follows) Vertical(XElement? position, double height, WordPage page)
    {
        var from = position?.Attribute("relativeFrom")?.Value ?? "paragraph";
        if (from is "paragraph" or "line")
        {
            return (Offset(position) ?? 0, true);
        }

        var (start, size) = from switch
        {
            "page" => (0.0, page.Height),
            "topMargin" or "insideMargin" => (0.0, page.Top),
            "bottomMargin" or "outsideMargin" => (page.Height - page.Bottom, page.Bottom),
            _ => (page.Top, page.Height - page.Top - page.Bottom),
        };

        return (Place(position, start, size, height, "top", "center", "bottom") ?? start, false);
    }

    // The place from a posOffset, or from an alignment (left, centre, right: top, centre, bottom) in the part of the page it is placed in.
    private static double? Place(XElement? position, double start, double size, double length, string first, string middle, string last)
    {
        if (Offset(position) is { } offset)
        {
            return start + offset;
        }

        var align = position?.Elements().FirstOrDefault(e => e.Name.LocalName == "align")?.Value.Trim();
        return align == first || align == "inside" ? start
            : align == middle ? start + (size - length) / 2
            : align == last || align == "outside" ? start + size - length
            : null;
    }

    // The section of each paragraph of a body, read once per body: a section ends at the first section properties after (or in) the paragraph,
    // in the order of the document (also inside content controls and tables).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Body, Dictionary<Paragraph, SectionProperties>> Sections = new();

    // The page of the section of a paragraph.
    private static WordPage PageOf(Paragraph paragraph)
    {
        SectionProperties? section = null;
        if (paragraph.Ancestors<Body>().FirstOrDefault() is { } body)
        {
            var sections = Sections.GetValue(body, b =>
            {
                var map = new Dictionary<Paragraph, SectionProperties>();
                var waiting = new List<Paragraph>();
                foreach (var element in b.Descendants())
                {
                    if (element is Paragraph p)
                    {
                        waiting.Add(p);
                    }
                    else if (element is SectionProperties s)
                    {
                        waiting.ForEach(w => map[w] = s);
                        waiting.Clear();
                    }
                }

                return map;
            });
            section = sections.GetValueOrDefault(paragraph);
        }

        var size = section?.GetFirstChild<PageSize>();
        var margins = section?.GetFirstChild<PageMargin>();
        var columns = section?.GetFirstChild<Columns>();
        var count = columns?.ColumnCount?.Value is > 1 ? columns.ColumnCount.Value : columns?.Elements<Column>().Count() ?? 1;
        return new WordPage(
            (size?.Width?.Value ?? 11906) * EmuPerTwip,
            (size?.Height?.Value ?? 16838) * EmuPerTwip,
            (margins?.Left?.Value ?? 1440) * EmuPerTwip,
            Math.Abs(margins?.Top?.Value ?? 1440) * EmuPerTwip,
            (margins?.Right?.Value ?? 1440) * EmuPerTwip,
            Math.Abs(margins?.Bottom?.Value ?? 1440) * EmuPerTwip,
            Math.Max(1, count));
    }

    public static IReadOnlyList<ShapeBox> Extract(Paragraph paragraph, double pageWidth, double pageHeight)
    {
        var boxes = new List<ShapeBox>();
        foreach (var drawing in paragraph.Descendants<Drawing>())
        {
            // A drawing inside a text box is a drawing of that text box, not of the page; a hidden one, and one deleted with tracked changes, is not shown.
            if (IsLeftOut(drawing))
            {
                continue;
            }

            XElement root;
            try
            {
                root = XElement.Parse(drawing.OuterXml);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            boxes.AddRange(Extract(root, pageWidth, pageHeight));
        }

        return boxes;
    }

    // The drawn shapes of one drawing (its XML already read), as parts of the page.
    private static List<ShapeBox> Extract(XElement root, double pageWidth, double pageHeight)
    {
        var boxes = new List<ShapeBox>();
        var extent = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "extent");
        var drawingWidth = Number(extent, "cx");
        var drawingHeight = Number(extent, "cy");
        if (drawingWidth <= 0 || drawingHeight <= 0)
        {
            return boxes;
        }

        var group = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "wgp");
        if (group is null)
        {
            foreach (var shape in root.Descendants().Where(e => e.Name.LocalName == "wsp"))
            {
                boxes.Add(ToBox(shape, drawingWidth / pageWidth, drawingHeight / pageHeight));
            }

            return boxes;
        }

        // Children of a group are placed in the group's own coordinates (chExt); scale them to the drawing.
        var groupTransform = group.Elements().FirstOrDefault(e => e.Name.LocalName == "grpSpPr")?.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm");
        var childExtent = groupTransform?.Elements().FirstOrDefault(e => e.Name.LocalName == "chExt");
        var childWidth = Number(childExtent, "cx");
        var childHeight = Number(childExtent, "cy");
        foreach (var shape in group.Descendants().Where(e => e.Name.LocalName == "wsp"))
        {
            var ext = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr")?.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm")?.Elements().FirstOrDefault(e => e.Name.LocalName == "ext");
            var widthInGroup = childWidth > 0 ? Number(ext, "cx") / childWidth : 1;
            var heightInGroup = childHeight > 0 ? Number(ext, "cy") / childHeight : 1;
            boxes.Add(ToBox(shape, drawingWidth * widthInGroup / pageWidth, drawingHeight * heightInGroup / pageHeight));
        }

        return boxes;
    }

    private static ShapeBox ToBox(XElement shape, double widthFraction, double heightFraction)
    {
        var properties = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr");
        var transform = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm");
        var preset = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "prstGeom")?.Attribute("prst")?.Value;
        var geometry = properties?.Elements().Any(e => e.Name.LocalName == "custGeom") == true ? "custGeom" : preset ?? "rect";

        var horizontalFlip = Flag(transform, "flipH");
        var verticalFlip = Flag(transform, "flipV");
        var hasText = shape.Elements().Any(e => e.Name.LocalName == "txbx");
        return new ShapeBox(geometry, widthFraction, heightFraction, horizontalFlip ^ verticalFlip, hasText);
    }

    private static double Number(XElement? element, string attribute) =>
        double.TryParse(element?.Attribute(attribute)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double? Offset(XElement? element) =>
        double.TryParse(element?.Elements().FirstOrDefault(e => e.Name.LocalName == "posOffset")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool Flag(XElement? element, string attribute) =>
        element?.Attribute(attribute)?.Value is "1" or "true";
}
/// <summary>The size and the margins of the page of a section, in EMU, and the number of its columns of text.</summary>
internal sealed record WordPage(double Width, double Height, double Left, double Top, double Right, double Bottom, int Columns = 1);

/// <summary>
/// A drawing of a paragraph that can be cut out of the page image (FR-136). <see cref="Left"/> and <see cref="Top"/> are in EMU from the top left of
/// the page; <see cref="Left"/> is null when the place across is not known (a drawing in the line), and <see cref="Top"/> is from the top of the paragraph
/// when <see cref="FollowsParagraph"/>.
/// </summary>
internal sealed record WordDrawing(
    Drawing Element,
    double? Left,
    double Top,
    double Width,
    double Height,
    bool FollowsParagraph,
    IReadOnlyList<ShapeBox> Boxes,
    bool HasPicture,
    bool Anchored,
    WordPage Page);
