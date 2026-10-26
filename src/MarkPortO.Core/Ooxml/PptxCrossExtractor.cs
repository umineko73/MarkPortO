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
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MarkPortO.Core.Jobs.Models;
using A = DocumentFormat.OpenXml.Drawing;

namespace MarkPortO.Core.Ooxml;

/// <summary>Finds the shapes of every slide of a .pptx and decides which slides are deleted pages (FR-012).</summary>
public static class PptxCrossExtractor
{
    /// <summary>The verdict of each slide, by its 1-based position in the presentation (hidden slides count).</summary>
    public static IReadOnlyDictionary<int, CrossVerdict> Evaluate(string path)
    {
        OoxmlPackage.EnsureOpens(path, DocumentKind.Pptx);
        using var document = PresentationDocument.Open(path, isEditable: false);
        var presentationPart = document.PresentationPart ?? throw new InvalidDocumentException(Loc.T("The presentation has no main part."));
        var size = presentationPart.Presentation?.SlideSize;
        double slideWidth = size?.Cx?.Value ?? 9144000;
        double slideHeight = size?.Cy?.Value ?? 6858000;

        var result = new Dictionary<int, CrossVerdict>();
        var slideIds = presentationPart.Presentation?.SlideIdList?.Elements<SlideId>().ToList() ?? [];
        for (var i = 0; i < slideIds.Count; i++)
        {
            if (presentationPart.GetPartById(slideIds[i].RelationshipId!.Value!) is not SlidePart part)
            {
                continue;
            }

            var shapes = new List<ShapeBox>();
            if (part.Slide?.CommonSlideData?.ShapeTree is { } tree)
            {
                Walk(tree, slideWidth, slideHeight, 1, 1, shapes);
            }

            result[i + 1] = CrossShapeDetector.Evaluate(shapes);
        }

        return result;
    }

    /// <param name="scaleX">How much larger the coordinates of this level are than the slide's (for shapes inside groups).</param>
    private static void Walk(OpenXmlElement container, double pageWidth, double pageHeight, double scaleX, double scaleY, List<ShapeBox> shapes)
    {
        foreach (var element in container.ChildElements)
        {
            switch (element)
            {
                case Shape shape:
                    Add(shape.ShapeProperties, pageWidth, pageHeight, scaleX, scaleY, shapes);
                    break;
                case ConnectionShape connector:
                    Add(connector.ShapeProperties, pageWidth, pageHeight, scaleX, scaleY, shapes);
                    break;
                case GroupShape group:
                    var transform = group.GroupShapeProperties?.TransformGroup;
                    var ext = transform?.Extents;
                    var childExt = transform?.ChildExtents;
                    var groupScaleX = scaleX;
                    var groupScaleY = scaleY;
                    if (ext?.Cx?.Value is { } cx && childExt?.Cx?.Value is { } chCx && chCx > 0)
                    {
                        groupScaleX = scaleX * cx / chCx;
                    }

                    if (ext?.Cy?.Value is { } cy && childExt?.Cy?.Value is { } chCy && chCy > 0)
                    {
                        groupScaleY = scaleY * cy / chCy;
                    }

                    Walk(group, pageWidth, pageHeight, groupScaleX, groupScaleY, shapes);
                    break;
            }
        }
    }

    private static void Add(ShapeProperties? properties, double pageWidth, double pageHeight, double scaleX, double scaleY, List<ShapeBox> shapes)
    {
        if (properties?.Transform2D?.Extents is not { Cx.Value: { } cx, Cy.Value: { } cy })
        {
            return;
        }

        var geometry = properties.GetFirstChild<A.CustomGeometry>() is not null
            ? "custGeom"
            : properties.GetFirstChild<A.PresetGeometry>()?.Preset?.InnerText ?? "rect";

        var horizontalFlip = properties.Transform2D.HorizontalFlip?.Value == true;
        var verticalFlip = properties.Transform2D.VerticalFlip?.Value == true;
        shapes.Add(new ShapeBox(
            geometry,
            cx * scaleX / pageWidth,
            cy * scaleY / pageHeight,
            AntiDiagonal: horizontalFlip ^ verticalFlip));
    }
}
