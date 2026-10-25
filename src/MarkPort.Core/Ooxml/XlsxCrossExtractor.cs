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
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MarkPort.Core.Jobs.Models;
using A = DocumentFormat.OpenXml.Drawing;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace MarkPort.Core.Ooxml;

/// <summary>
/// Finds the shapes drawn on each worksheet and decides which sheets carry a "×" over (nearly) the whole sheet (FR-012).
/// "The whole sheet" means the used range: the same range that is printed and converted (FR-010b).
/// </summary>
public static class XlsxCrossExtractor
{
    private const double EmuPerPoint = 12700;
    private const double DefaultRowHeightPoints = 15;

    /// <summary>The verdict of each sheet, by name. A sheet that is a deleted page is deleted in all its parts.</summary>
    public static IReadOnlyDictionary<string, CrossVerdict> Evaluate(string path, IReadOnlyList<SheetContent> sheets)
    {
        OoxmlPackage.EnsureOpens(path, DocumentKind.Xlsx);
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDocumentException(Loc.T("The workbook has no main part."));
        var byName = sheets.ToDictionary(s => s.Name, StringComparer.Ordinal);

        var result = new Dictionary<string, CrossVerdict>(StringComparer.Ordinal);
        foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            var name = sheet.Name?.Value;
            if (name is null
                || !byName.TryGetValue(name, out var content)
                || content.IsEmpty
                || workbookPart.GetPartById(sheet.Id!.Value!) is not WorksheetPart worksheetPart
                || worksheetPart.DrawingsPart?.WorksheetDrawing is not { } drawing)
            {
                continue;
            }

            var shapes = new List<ShapeBox>();
            foreach (var anchor in drawing.ChildElements)
            {
                var (widthFraction, heightFraction) = Coverage(anchor, content);
                if (widthFraction <= 0 || heightFraction <= 0)
                {
                    continue;
                }

                foreach (var child in anchor.ChildElements)
                {
                    AddShape(child, widthFraction, heightFraction, shapes);
                }
            }

            result[name] = CrossShapeDetector.Evaluate(shapes);
        }

        return result;
    }

    private static void AddShape(OpenXmlElement element, double widthFraction, double heightFraction, List<ShapeBox> shapes)
    {
        switch (element)
        {
            case Xdr.Shape shape:
                Add(shape.ShapeProperties, widthFraction, heightFraction, shapes);
                break;
            case Xdr.ConnectionShape connector:
                Add(connector.ShapeProperties, widthFraction, heightFraction, shapes);
                break;
            case Xdr.GroupShape group:
                var childExtents = group.GroupShapeProperties?.TransformGroup?.ChildExtents;
                foreach (var child in group.ChildElements)
                {
                    // The children are placed in the group's own coordinates; they cover their share of the group's area.
                    var share = childExtents is { Cx.Value: > 0, Cy.Value: > 0 } ? ChildShare(child, childExtents) : (1.0, 1.0);
                    AddShape(child, widthFraction * share.Item1, heightFraction * share.Item2, shapes);
                }

                break;
        }
    }

    private static (double, double) ChildShare(OpenXmlElement child, A.ChildExtents groupExtents)
    {
        var properties = child switch
        {
            Xdr.Shape s => s.ShapeProperties,
            Xdr.ConnectionShape c => c.ShapeProperties,
            _ => null,
        };

        if (properties?.Transform2D?.Extents is { Cx.Value: { } cx, Cy.Value: { } cy })
        {
            return (Math.Min(1, cx / (double)groupExtents.Cx!.Value), Math.Min(1, cy / (double)groupExtents.Cy!.Value));
        }

        return (1.0, 1.0);
    }

    private static void Add(Xdr.ShapeProperties? properties, double widthFraction, double heightFraction, List<ShapeBox> shapes)
    {
        if (properties is null)
        {
            return;
        }

        var geometry = properties.GetFirstChild<A.CustomGeometry>() is not null
            ? "custGeom"
            : properties.GetFirstChild<A.PresetGeometry>()?.Preset?.InnerText ?? "rect";
        var horizontalFlip = properties.Transform2D?.HorizontalFlip?.Value == true;
        var verticalFlip = properties.Transform2D?.VerticalFlip?.Value == true;
        shapes.Add(new ShapeBox(geometry, widthFraction, heightFraction, horizontalFlip ^ verticalFlip));
    }

    /// <summary>How much of the used range an anchor covers, as fractions of its width and height.</summary>
    private static (double Width, double Height) Coverage(OpenXmlElement anchor, SheetContent sheet)
    {
        switch (anchor)
        {
            case Xdr.TwoCellAnchor two when two.FromMarker is not null && two.ToMarker is not null:
                return CoverageOfCells(
                    Index(two.FromMarker.ColumnId?.Text), Index(two.FromMarker.RowId?.Text),
                    Index(two.ToMarker.ColumnId?.Text), Index(two.ToMarker.RowId?.Text),
                    sheet);
            case Xdr.OneCellAnchor one when one.Extent is { Cx.Value: { } cx, Cy.Value: { } cy }:
                return CoverageOfSize(cx, cy, sheet);
            case Xdr.AbsoluteAnchor absolute when absolute.Extent is { Cx.Value: { } cx, Cy.Value: { } cy }:
                return CoverageOfSize(cx, cy, sheet);
            default:
                return (0, 0);
        }
    }

    private static (double, double) CoverageOfCells(int fromColumn, int fromRow, int toColumn, int toRow, SheetContent sheet)
    {
        // The anchors count from 0; the used range counts from 1.
        int firstColumn = fromColumn + 1, lastColumn = toColumn + 1, firstRow = fromRow + 1, lastRow = toRow + 1;

        var width = 0.0;
        var covered = 0.0;
        for (var i = 0; i < sheet.ColumnWidthsPoints.Length; i++)
        {
            var column = sheet.FirstColumn + i;
            width += sheet.ColumnWidthsPoints[i];
            if (column >= firstColumn && column <= lastColumn)
            {
                covered += sheet.ColumnWidthsPoints[i];
            }
        }

        var widthFraction = width > 0
            ? covered / width
            : OverlapCount(firstColumn, lastColumn, sheet.FirstColumn, sheet.LastColumn) / (double)sheet.ColumnCount;

        var rows = sheet.LastRow - sheet.FirstRow + 1;
        var heightFraction = OverlapCount(firstRow, lastRow, sheet.FirstRow, sheet.LastRow) / (double)rows;
        return (widthFraction, heightFraction);
    }

    private static (double, double) CoverageOfSize(long widthEmu, long heightEmu, SheetContent sheet)
    {
        var sheetWidth = sheet.TotalWidthPoints * EmuPerPoint;
        var sheetHeight = (sheet.LastRow - sheet.FirstRow + 1) * DefaultRowHeightPoints * EmuPerPoint;
        return (sheetWidth > 0 ? widthEmu / sheetWidth : 0, sheetHeight > 0 ? heightEmu / sheetHeight : 0);
    }

    private static int OverlapCount(int from, int to, int rangeFrom, int rangeTo) =>
        Math.Max(0, Math.Min(to, rangeTo) - Math.Max(from, rangeFrom) + 1);

    private static int Index(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
