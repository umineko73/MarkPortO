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

using MarkPort.Core.Ooxml;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.Ooxml;

public class XlsxReaderTests
{
    [Fact]
    public void Read_UsedRange_StartsAtTheFirstCellWithContent()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        // Rows 1-2 and column A are empty: the data starts at B3.
        XlsxFactory.Create(path, new SheetSpec("Data", [[], [], ["", "Name", "Qty"], ["", "Apple", "3"]]));

        var sheet = Assert.Single(XlsxReader.Read(path, includeHidden: false));

        Assert.Equal((2, 3, 3, 4), (sheet.FirstColumn, sheet.LastColumn, sheet.FirstRow, sheet.LastRow));
        Assert.Equal([["Name", "Qty"], ["Apple", "3"]], sheet.Rows);
        Assert.Equal("B:C", sheet.ColumnRange(0, 1));
    }

    [Fact]
    public void Read_Markdown_IsAPipeTableWithTheFirstRowAsHeader()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["Name", "Note"], ["Apple", "a|b"]]));

        var sheet = XlsxReader.Read(path, false).Single();

        Assert.Equal("| Name | Note |\n|---|---|\n| Apple | a\\|b |", sheet.ToMarkdown());
    }

    [Fact]
    public void Read_FormulaCells_UseTheSavedResult()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["1", "2"], ["=A1+B1::3", "x"]]));

        var sheet = XlsxReader.Read(path, false).Single();

        Assert.Equal("3", sheet.Rows[1][0]);
    }

    [Fact]
    public void Read_MergedCells_KeepTheValueOnlyInTheTopLeftCell()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["Title", ""], ["a", "b"]], Merges: ["A1:B1"]));

        var sheet = XlsxReader.Read(path, false).Single();

        Assert.Equal(["Title", ""], sheet.Rows[0]);
        Assert.Equal(["a", "b"], sheet.Rows[1]);
    }

    [Fact]
    public void Read_HiddenSheets_AreSkippedUnlessRequested()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(
            path,
            new SheetSpec("Visible", [["v"]]),
            new SheetSpec("Secret", [["s"]], Hidden: true));

        Assert.Equal(["Visible"], XlsxReader.Read(path, includeHidden: false).Select(s => s.Name));
        var all = XlsxReader.Read(path, includeHidden: true);
        Assert.Equal(["Visible", "Secret"], all.Select(s => s.Name));
        Assert.True(all[1].Hidden);
        Assert.Equal([("Visible", false), ("Secret", true)], XlsxReader.ReadSheetNames(path));
    }

    [Fact]
    public void Read_ColumnWidths_AreInPoints()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, XlsxFactory.WideSheet("Wide", columns: 3, rows: 2, columnWidthChars: 14));

        var sheet = XlsxReader.Read(path, false).Single();

        // OOXML: pixels = trunc((256 * 14 + trunc(128 / 7)) / 256 * 7) = 98; 98 pixels = 73.5 points.
        Assert.All(sheet.ColumnWidthsPoints, w => Assert.Equal(73.5, w));
        Assert.Equal(3, sheet.ColumnWidthsPoints.Length);
        Assert.Equal(220.5, sheet.TotalWidthPoints);
    }

    [Fact]
    public void Read_DefaultColumnWidth_IsAbout48Points()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["a", "b"]]));

        var sheet = XlsxReader.Read(path, false).Single();

        Assert.All(sheet.ColumnWidthsPoints, w => Assert.Equal(48, w));
    }

    [Fact]
    public void Read_EmptySheet_IsEmpty()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Empty", []));

        var sheet = Assert.Single(XlsxReader.Read(path, false));

        Assert.True(sheet.IsEmpty);
        Assert.Equal(string.Empty, sheet.ToMarkdown());
    }

    [Fact]
    public void Read_CorruptFile_ThrowsInvalidDocumentException()
    {
        using var dir = new TempDir();
        var path = dir.Combine("broken.xlsx");
        File.WriteAllText(path, "not a workbook");

        Assert.Throws<InvalidDocumentException>(() => XlsxReader.Read(path, false));
    }

    [Theory]
    [InlineData("A", 1)]
    [InlineData("Z", 26)]
    [InlineData("AA", 27)]
    [InlineData("XFD", 16384)]
    public void ExcelColumn_NamesAndIndexesAgree(string name, int index)
    {
        Assert.Equal(index, ExcelColumn.Index(name));
        Assert.Equal(name, ExcelColumn.Name(index));
    }
}
