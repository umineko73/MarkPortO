// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace MarkPort.Core.Tests.Fixtures;

/// <summary>One worksheet to build. A cell written as <c>=A1+1::2</c> is the formula <c>A1+1</c> whose saved result is <c>2</c>.</summary>
/// <param name="Rows">Cell texts, row by row. A numeric-looking text becomes a number.</param>
/// <param name="Hidden">The sheet is hidden.</param>
/// <param name="PrintArea">An A1-style range such as <c>A1:C5</c>; set as the sheet's print area.</param>
/// <param name="ColumnWidthChars">Width of every column in characters (Excel's default is about 8.43).</param>
/// <param name="Merges">Merged ranges such as <c>A1:B1</c>.</param>
public sealed record SheetSpec(
    string Name,
    string[][] Rows,
    bool Hidden = false,
    string? PrintArea = null,
    double? ColumnWidthChars = null,
    string[]? Merges = null,
    CellShapeSpec[]? Shapes = null);

/// <summary>A drawn shape anchored to cells (0-based, as in the file): from the top left of (FromColumn, FromRow) to the top left of (ToColumn, ToRow).</summary>
public sealed record CellShapeSpec(string Geometry, int FromColumn, int FromRow, int ToColumn, int ToRow, bool FlipH = false, bool FlipV = false);

/// <summary>Builds small .xlsx files for tests.</summary>
public static class XlsxFactory
{
    public static void Create(string path, params SheetSpec[] sheets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheetsElement = workbookPart.Workbook.AppendChild(new Sheets());
        var definedNames = new DefinedNames();

        for (var i = 0; i < sheets.Length; i++)
        {
            var spec = sheets[i];
            var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
            sheetPart.Worksheet = BuildWorksheet(spec);
            if (spec.Shapes is { Length: > 0 })
            {
                AddShapes(sheetPart, spec.Shapes);
            }

            var sheet = new Sheet
            {
                Id = workbookPart.GetIdOfPart(sheetPart),
                SheetId = (uint)(i + 1),
                Name = spec.Name,
            };
            if (spec.Hidden)
            {
                sheet.State = SheetStateValues.Hidden;
            }

            sheetsElement.AppendChild(sheet);

            if (spec.PrintArea is not null)
            {
                definedNames.AppendChild(new DefinedName($"'{spec.Name}'!{Absolute(spec.PrintArea)}")
                {
                    Name = "_xlnm.Print_Area",
                    LocalSheetId = (uint)i,
                });
            }
        }

        if (definedNames.HasChildren)
        {
            workbookPart.Workbook.AppendChild(definedNames);
        }

        workbookPart.Workbook.Save();
    }

    /// <summary>A sheet with many wide columns: header "H1".."Hn" and <paramref name="rows"/> rows of text.</summary>
    public static SheetSpec WideSheet(string name, int columns, int rows, double columnWidthChars = 14, bool hidden = false)
    {
        var data = new List<string[]>
        {
            Enumerable.Range(1, columns).Select(c => $"H{c}").ToArray(),
        };
        for (var r = 1; r <= rows; r++)
        {
            data.Add(Enumerable.Range(1, columns).Select(c => $"r{r}c{c}").ToArray());
        }

        return new SheetSpec(name, [.. data], hidden, ColumnWidthChars: columnWidthChars);
    }

    public static string ColumnName(int oneBasedIndex)
    {
        var name = string.Empty;
        for (var n = oneBasedIndex; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }

        return name;
    }

    private static void AddShapes(WorksheetPart sheetPart, CellShapeSpec[] shapes)
    {
        var drawingsPart = sheetPart.AddNewPart<DrawingsPart>();
        var xml = new System.Text.StringBuilder(
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">");
        for (var i = 0; i < shapes.Length; i++)
        {
            var shape = shapes[i];
            var flips = (shape.FlipH ? " flipH=\"1\"" : string.Empty) + (shape.FlipV ? " flipV=\"1\"" : string.Empty);
            var geometry = shape.Geometry == "custGeom"
                ? "<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"r\" b=\"b\"/><a:pathLst><a:path w=\"100\" h=\"100\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"100\" y=\"100\"/></a:lnTo></a:path></a:pathLst></a:custGeom>"
                : $"<a:prstGeom prst=\"{shape.Geometry}\"><a:avLst/></a:prstGeom>";
            var line = "<a:ln w=\"38100\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:ln>";
            var properties = $"<xdr:spPr><a:xfrm{flips}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"100\"/></a:xfrm>{geometry}{line}</xdr:spPr>";
            var content = shape.Geometry is "line" or "straightConnector1"
                ? $"<xdr:cxnSp macro=\"\"><xdr:nvCxnSpPr><xdr:cNvPr id=\"{i + 2}\" name=\"Line {i}\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr>{properties}</xdr:cxnSp>"
                : $"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"{i + 2}\" name=\"Shape {i}\"/><xdr:cNvSpPr/></xdr:nvSpPr>{properties}</xdr:sp>";
            xml.Append($"<xdr:twoCellAnchor><xdr:from><xdr:col>{shape.FromColumn}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{shape.FromRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>")
                .Append($"<xdr:to><xdr:col>{shape.ToColumn}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{shape.ToRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>")
                .Append(content).Append("<xdr:clientData/></xdr:twoCellAnchor>");
        }

        xml.Append("</xdr:wsDr>");
        drawingsPart.WorksheetDrawing = new Xdr.WorksheetDrawing(xml.ToString());
        sheetPart.Worksheet!.AddChild(new Drawing { Id = sheetPart.GetIdOfPart(drawingsPart) });
    }

    private static Worksheet BuildWorksheet(SheetSpec spec)
    {
        var worksheet = new Worksheet();
        var columnCount = spec.Rows.Length == 0 ? 0 : spec.Rows.Max(r => r.Length);

        if (spec.ColumnWidthChars is { } width && columnCount > 0)
        {
            worksheet.AppendChild(new Columns(new Column
            {
                Min = 1,
                Max = (uint)columnCount,
                Width = width,
                CustomWidth = true,
            }));
        }

        var data = worksheet.AppendChild(new SheetData());
        for (var r = 0; r < spec.Rows.Length; r++)
        {
            var row = new Row { RowIndex = (uint)(r + 1) };
            for (var c = 0; c < spec.Rows[r].Length; c++)
            {
                var text = spec.Rows[r][c];
                if (text.Length == 0)
                {
                    continue;
                }

                row.AppendChild(BuildCell($"{ColumnName(c + 1)}{r + 1}", text));
            }

            data.AppendChild(row);
        }

        if (spec.Merges is { Length: > 0 })
        {
            var merges = new MergeCells();
            foreach (var range in spec.Merges)
            {
                merges.AppendChild(new MergeCell { Reference = range });
            }

            worksheet.AppendChild(merges);
        }

        return worksheet;
    }

    private static Cell BuildCell(string reference, string text)
    {
        if (text.StartsWith('='))
        {
            var parts = text[1..].Split("::", 2);
            var cell = new Cell { CellReference = reference };
            cell.AppendChild(new CellFormula(parts[0]));
            if (parts.Length == 2)
            {
                cell.AppendChild(new CellValue(parts[1]));
            }

            return cell;
        }

        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return new Cell(new CellValue(text)) { CellReference = reference, DataType = CellValues.Number };
        }

        return new Cell(new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }))
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
        };
    }

    /// <summary>"A1:C5" becomes "$A$1:$C$5".</summary>
    private static string Absolute(string range) =>
        string.Join(':', range.Split(':').Select(part =>
        {
            var letters = new string(part.TakeWhile(char.IsLetter).ToArray());
            var digits = part[letters.Length..];
            return $"${letters}${digits}";
        }));
}
