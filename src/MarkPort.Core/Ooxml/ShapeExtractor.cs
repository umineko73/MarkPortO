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

using System.Globalization;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MarkPort.Core.Ooxml;

/// <summary>
/// Reads the drawn shapes (the <c>wps</c> shapes of a Word drawing) of a paragraph. The XML is read by name, so no
/// Office 2010 type library is needed. Pictures, charts, and the old VML shapes are not looked at.
/// </summary>
internal static class ShapeExtractor
{
    public static IReadOnlyList<ShapeBox> Extract(Paragraph paragraph, double pageWidth, double pageHeight)
    {
        var boxes = new List<ShapeBox>();
        foreach (var drawing in paragraph.Descendants<Drawing>())
        {
            // A drawing inside a text box is a drawing of that text box, not of the page.
            if (drawing.Ancestors<TextBoxContent>().Any())
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

            var extent = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "extent");
            var drawingWidth = Number(extent, "cx");
            var drawingHeight = Number(extent, "cy");
            if (drawingWidth <= 0 || drawingHeight <= 0)
            {
                continue;
            }

            var group = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "wgp");
            if (group is null)
            {
                foreach (var shape in root.Descendants().Where(e => e.Name.LocalName == "wsp"))
                {
                    boxes.Add(ToBox(shape, drawingWidth / pageWidth, drawingHeight / pageHeight));
                }

                continue;
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
        return new ShapeBox(geometry, widthFraction, heightFraction, horizontalFlip ^ verticalFlip);
    }

    private static double Number(XElement? element, string attribute) =>
        double.TryParse(element?.Attribute(attribute)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static bool Flag(XElement? element, string attribute) =>
        element?.Attribute(attribute)?.Value is "1" or "true";
}
