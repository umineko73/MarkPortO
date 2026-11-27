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
/// Office 2010 type library is needed. Pictures and charts are not looked at; of the old VML shapes, only the straight lines, for the "×" check (ExtractVmlLines).
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
                // In a table cell, Word places a drawing from the cell (layoutInCell), except from the page: where the cell is, only the PDF shows.
                var inCell = drawing.Ancestors<DocumentFormat.OpenXml.Wordprocessing.TableCell>().Any();
                x = inCell && horizontal?.Attribute("relativeFrom")?.Value != "page" ? null : Horizontal(horizontal, width, page);
                (y, follows) = inCell && vertical?.Attribute("relativeFrom")?.Value != "page" ? (Offset(vertical) ?? 0, true) : Vertical(vertical, height, page);
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

    /// <summary>
    /// The straight lines of the old VML drawings of a paragraph (<c>w:pict</c>: <c>v:line</c>, and <c>v:shape</c> of a straight line or
    /// connector, also in <c>v:group</c>), as parts of the page, only for the "×" check (FR-012): Word keeps the shapes drawn in a document in
    /// compatibility mode (Word 2007) in this form only. They are not cut out. A copy of a newer drawing for old readers
    /// (<c>mc:Fallback</c>) is not counted again; nor a drawing in a text box, or one deleted with tracked changes.
    /// </summary>
    public static IReadOnlyList<ShapeBox> ExtractVmlLines(Paragraph paragraph, double pageWidth, double pageHeight)
    {
        var boxes = new List<ShapeBox>();
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return boxes;
        }

        foreach (var pict in paragraph.Descendants().Where(e => e.LocalName == "pict"))
        {
            if (pict.Ancestors().Any(a => a.LocalName is "Fallback" or "del" or "txbxContent" or "moveFrom"))
            {
                continue;
            }

            XElement root;
            try
            {
                root = XElement.Parse(pict.OuterXml);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            // The shape types of straight lines and connectors (o:spt 20 and 32), by id, as this drawing defines them; Word also uses their usual ids.
            var lineTypes = new HashSet<string>(StringComparer.Ordinal) { "_x0000_t20", "_x0000_t32" };
            foreach (var type in root.Descendants().Where(e => e.Name.LocalName == "shapetype"))
            {
                if (type.Attributes().FirstOrDefault(a => a.Name.LocalName == "spt")?.Value is "20" or "32" && type.Attribute("id")?.Value is { } id)
                {
                    lineTypes.Add(id);
                }
            }

            foreach (var element in root.Elements())
            {
                Walk(element, VmlPixel, VmlPixel, flipped: false);
            }

            // scaleX / scaleY: points for a value without a unit (a pixel at the top, a unit of coordsize in a group).
            void Walk(XElement element, double scaleX, double scaleY, bool flipped)
            {
                var style = VmlStyle(element.Attribute("style")?.Value);
                if (string.Equals(style.GetValueOrDefault("visibility"), "hidden", StringComparison.OrdinalIgnoreCase))
                {
                    return; // not shown (nor what is in it)
                }

                var flip = style.GetValueOrDefault("flip", string.Empty);
                var turned = flipped ^ (flip.Contains('x', StringComparison.Ordinal) ^ flip.Contains('y', StringComparison.Ordinal));
                switch (element.Name.LocalName)
                {
                    case "group":
                    {
                        var width = VmlLength(style.GetValueOrDefault("width"), scaleX);
                        var height = VmlLength(style.GetValueOrDefault("height"), scaleY);
                        var size = VmlPair(element.Attribute("coordsize")?.Value ?? "1000,1000");
                        var cw = size is { } s1 && double.TryParse(s1.X, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a > 0 ? a : 1000;
                        var ch = size is { } s2 && double.TryParse(s2.Y, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0 ? b : 1000;
                        foreach (var child in element.Elements())
                        {
                            Walk(child, width / cw, height / ch, turned);
                        }

                        break;
                    }

                    case "line" when IsStroked(element):
                    {
                        if (VmlPoint(element.Attribute("from")?.Value, scaleX, scaleY) is not { } start
                            || VmlPoint(element.Attribute("to")?.Value, scaleX, scaleY) is not { } end)
                        {
                            break; // its ends cannot be read: not taken
                        }

                        var (x1, y1) = start;
                        var (x2, y2) = end;

                        var anti = ((x2 - x1) * (y2 - y1) < 0) ^ flipped;
                        boxes.Add(new ShapeBox("line", Math.Abs(x2 - x1) * EmuPerPoint / pageWidth, Math.Abs(y2 - y1) * EmuPerPoint / pageHeight, anti));
                        break;
                    }

                    case "shape" when IsVmlLine(element) && IsStroked(element):
                    {
                        if (!IsVmlLength(style.GetValueOrDefault("width") ?? string.Empty) || !IsVmlLength(style.GetValueOrDefault("height") ?? string.Empty))
                        {
                            break; // its size cannot be read: not taken
                        }

                        var width = VmlLength(style.GetValueOrDefault("width"), scaleX);
                        var height = VmlLength(style.GetValueOrDefault("height"), scaleY);
                        boxes.Add(new ShapeBox("line", width * EmuPerPoint / pageWidth, height * EmuPerPoint / pageHeight, turned));
                        break;
                    }
                }
            }

            // A line is drawn unless its stroke is off (stroked="f", or a v:stroke with on="f").
            static bool IsStroked(XElement shape) =>
                shape.Attribute("stroked")?.Value.Trim().ToLowerInvariant() is not ("f" or "false" or "0")
                && !shape.Elements().Any(e => e.Name.LocalName == "stroke" && e.Attribute("on")?.Value.Trim().ToLowerInvariant() is "f" or "false" or "0");

            bool IsVmlLine(XElement shape) =>
                (shape.Attribute("type")?.Value is { } type && lineTypes.Contains(type.TrimStart('#')))
                || shape.Attributes().Any(a => a.Name.LocalName == "connectortype" && a.Value == "straight")
                || shape.Attribute("path")?.Value.Replace(" ", string.Empty, StringComparison.Ordinal) is "m,l21600,21600e" or "m0,0l21600,21600e";
        }

        return boxes;
    }

    private const double EmuPerPoint = 12700;

    private const double VmlPixel = 0.75; // a length of VML without a unit is in pixels (96 to the inch)

    private static Dictionary<string, string> VmlStyle(string? style)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (style ?? string.Empty).Split(';'))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                values[part[..colon].Trim()] = part[(colon + 1)..].Trim();
            }
        }

        return values;
    }

    // A VML length in points: with its unit, or without one in the units of scale (points each).
    private static double VmlLength(string? value, double scale)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        var unit = new string([.. text.Reverse().TakeWhile(char.IsLetter).Reverse()]);
        if (!double.TryParse(text[..(text.Length - unit.Length)], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number) || !VmlUnits.Contains(unit))
        {
            return 0; // not a length (a line of no size is not taken)
        }

        return unit switch
        {
            "pt" => number,
            "in" => number * 72,
            "cm" => number * 72 / 2.54,
            "mm" => number * 72 / 25.4,
            "pc" => number * 12,
            "px" => number * VmlPixel,
            "emu" => number / EmuPerPoint,
            _ => number * scale,
        };
    }

    // Two values of VML, separated by a comma or by spaces ("10pt,20pt", "21600 21600"); null when there are not two.
    private static (string X, string Y)? VmlPair(string? value)
    {
        var parts = (value ?? string.Empty).Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    private static (double X, double Y)? VmlPoint(string? value, double scaleX, double scaleY)
    {
        if (VmlPair(value) is not { } pair || !IsVmlLength(pair.X) || !IsVmlLength(pair.Y))
        {
            return null;
        }

        return (VmlLength(pair.X, scaleX), VmlLength(pair.Y, scaleY));
    }

    private static readonly HashSet<string> VmlUnits = new(StringComparer.Ordinal) { string.Empty, "pt", "in", "cm", "mm", "pc", "px", "emu" };

    private static bool IsVmlLength(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        var unit = new string([.. text.Reverse().TakeWhile(char.IsLetter).Reverse()]);
        return VmlUnits.Contains(unit)
            && double.TryParse(text[..(text.Length - unit.Length)], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) && Math.Abs(number) < 1e9;
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
