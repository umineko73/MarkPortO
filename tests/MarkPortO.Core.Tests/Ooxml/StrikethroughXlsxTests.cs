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

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>FR-092 for a workbook: a cell with a strikethrough font, or a part of its text, is written between <c>~~</c>.</summary>
public class StrikethroughXlsxTests
{
    private static string Workbook(TempDir dir)
    {
        var path = dir.Combine("strike.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var book = document.AddWorkbookPart();
        book.Workbook = new Workbook(new Sheets(new Sheet { Name = "Data", SheetId = 1, Id = "rId1" }));
        var stylesPart = book.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new Fonts(new Font(), new Font(new Strike())),
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(new CellFormat(), new CellFormat { FontId = 1, ApplyFont = true }));
        var strings = book.AddNewPart<SharedStringTablePart>();
        strings.SharedStringTable = new SharedStringTable(
            new SharedStringItem(new Text("plain")),
            new SharedStringItem(
                new Run(new Text("old ") { Space = SpaceProcessingModeValues.Preserve }),
                new Run(new RunProperties(new Strike()), new Text("crossed")),
                new Run(new Text(" new") { Space = SpaceProcessingModeValues.Preserve })),
            // As Excel writes a cell whose font has a strikethrough, when only a part of the text has it: the first part has no font of its own (it has the font
            // of the cell), the second has one without a strikethrough.
            new SharedStringItem(
                new Run(new Text("old")),
                new Run(new RunProperties(new FontSize { Val = 11 }), new Text(" new") { Space = SpaceProcessingModeValues.Preserve })));
        var sheet = book.AddNewPart<WorksheetPart>("rId1");
        sheet.Worksheet = new Worksheet(new SheetData(
            new Row(
                new Cell { CellReference = "A1", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
                new Cell { CellReference = "B1", DataType = CellValues.SharedString, CellValue = new CellValue("0"), StyleIndex = 1 },
                new Cell { CellReference = "C1", DataType = CellValues.SharedString, CellValue = new CellValue("1") },
                new Cell { CellReference = "D1", CellValue = new CellValue("42"), StyleIndex = 1 },
                new Cell { CellReference = "E1", DataType = CellValues.SharedString, CellValue = new CellValue("2"), StyleIndex = 1 }) { RowIndex = 1 }));
        book.Workbook.Save();
        return path;
    }

    [Fact]
    public void Xlsx_ACellWithAStrikethroughFont_AndAStruckPartOfTheText_AreWrittenBetweenTildes()
    {
        using var dir = new TempDir();
        var sheet = XlsxReader.Read(Workbook(dir), includeHidden: false).Single();

        Assert.Equal(["plain", "~~plain~~", "old ~~crossed~~ new", "~~42~~", "~~old~~ new"], sheet.Rows[0]);
    }

    [Fact]
    public void Xlsx_TheRowsAreFoundInThePdfWithoutTheTildes()
    {
        using var dir = new TempDir();
        var sheet = XlsxReader.Read(Workbook(dir), includeHidden: false).Single();

        // The PDF has "plain old crossed new 42" without tildes: the row is still found, on the second page.
        var pages = ExcelRowPages.Assign(sheet, 0, 3, ["first page text", "plain plain old crossed new 42"]);

        Assert.Equal([1], pages);
    }
}
