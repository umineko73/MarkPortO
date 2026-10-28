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

using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

public class XlsxReaderTests
{
    [Fact]
    public void Read_Numbers_AreWrittenAsTheCellsShowThem()
    {
        using var dir = new TempDir();
        var path = dir.Combine("f.xlsx");
        XlsxFactory.Create(path, new SheetSpec(
            "Data",
            [["0.256", "1234567", "3.14159", "-1234.5", "0.5"]],
            NumberFormats: new Dictionary<string, string>
            {
                ["A1"] = "0.0%",
                ["B1"] = "\"¥\"#,##0",
                ["C1"] = "0.00",
                ["D1"] = "#,##0.00;[Red]-#,##0.00",
                ["E1"] = "General",
            }));

        var sheet = XlsxReader.Read(path, false).Single();

        Assert.Equal(["25.6%", "¥1,234,567", "3.14", "-1,234.50", "0.5"], sheet.Rows[0]);
    }

    [Theory]
    [InlineData(1234.5, "#,##0", "1,235")]
    [InlineData(0.5, "0%", "50%")]
    [InlineData(1234567, "#,##0,", "1,235")]
    [InlineData(7, "000", "007")]
    [InlineData(-3.5, "0.0", "-3.5")]
    [InlineData(-3.5, "0.0;(0.0)", "(3.5)")]
    [InlineData(0, "0;-0;\"zero\"", "zero")]
    [InlineData(1234.5, "[$$-409]#,##0.00", "$1,234.50")]
    [InlineData(12, "0\" pcs\"", "12 pcs")]
    [InlineData(0.075, ".00", ".08")]
    public void NumberFormat_WritesTheNumberLikeExcel(double value, string code, string expected)
    {
        Assert.Equal(expected, XlsxNumberFormat.Format(value, code));
    }

    [Theory]
    [InlineData("0.00E+00")]
    [InlineData("# ?/?")]
    [InlineData("@")]
    [InlineData("General")]
    public void NumberFormat_KeepsTheStoredValue_WhenTheFormatIsNotAPlainNumber(string code)
    {
        Assert.Null(XlsxNumberFormat.Format(1.5, code));
    }

    [Fact]
    public void Read_HiddenRowsAndColumns_AreNotInTheMarkdown()
    {
        using var dir = new TempDir();
        var path = dir.Combine("h.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["a", "b", "c"], ["d", "e", "f"], ["g", "h", "i"]], HiddenRows: [2], HiddenColumns: [2]));

        var markdown = XlsxReader.Read(path, false).Single().ToMarkdown();

        Assert.Equal("|  |  |\n|---|---|\n| a | c |\n| g | i |", markdown);
    }

    [Fact]
    public void Read_ALineBreakInACell_IsWrittenAsBr()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["one\ntwo", "x"]]));

        Assert.Contains("| one<br>two | x |", XlsxReader.Read(path, false).Single().ToMarkdown());
    }

    [Fact]
    public void Read_StackedTables_AreWrittenOneByOne_WithTheirNames()
    {
        using var dir = new TempDir();
        var path = dir.Combine("t.xlsx");
        XlsxFactory.Create(path, new SheetSpec(
            "Data",
            [["h1", "h2"], ["a", "b"], ["", ""], ["g1", "g2"], ["c", "d"], ["", ""], ["note", ""]],
            ExcelTable: "A1:B2",
            MoreTables: ["A4:B5"]));

        var markdown = XlsxReader.Read(path, false).Single().ToMarkdown();

        Assert.Equal("### Table1\n\n| h1 | h2 |\n|---|---|\n| a | b |\n\n### Table2\n\n| g1 | g2 |\n|---|---|\n| c | d |\n\n|  |  |\n|---|---|\n| note |  |", markdown);
    }

    [Fact]
    public void Read_ATableWithContentBesideIt_FallsBackToOneTable()
    {
        using var dir = new TempDir();
        var path = dir.Combine("s.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["h1", "h2", "side"], ["a", "b", "x"]], ExcelTable: "A1:B2"));

        var markdown = XlsxReader.Read(path, false).Single().ToMarkdown();

        Assert.DoesNotContain("###", markdown);
        Assert.Contains("| h1 | h2 | side |", markdown);
    }

    [Fact]
    public void Read_HyperlinksAndComments_AreWritten()
    {
        using var dir = new TempDir();
        var path = dir.Combine("l.xlsx");
        XlsxFactory.Create(path, new SheetSpec(
            "Data",
            [["site", "x"], ["a", "b"]],
            Hyperlinks: new Dictionary<string, string> { ["A1"] = "https://example.com/a b" },
            Comments: new Dictionary<string, string> { ["B2"] = "check this" }));

        var markdown = XlsxReader.Read(path, false).Single().ToMarkdown();

        Assert.Contains("| [site](https://example.com/a%20b) | x |", markdown);
        Assert.EndsWith("> **B2**: check this", markdown);
    }

    // The XML of a bar chart as Excel saves it: the titles and the values are kept in caches.
    internal const string BarChartXml =
        "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">"
        + "<c:chart><c:title><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>Sales by year</a:t></a:r></a:p></c:rich></c:tx></c:title>"
        + "<c:plotArea><c:barChart><c:barDir val=\"col\"/>"
        + "<c:ser><c:idx val=\"0\"/><c:tx><c:strRef><c:f>S!$B$1</c:f><c:strCache><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>Tokyo</c:v></c:pt></c:strCache></c:strRef></c:tx>"
        + "<c:cat><c:strRef><c:f>S!$A$2:$A$3</c:f><c:strCache><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>2024</c:v></c:pt><c:pt idx=\"1\"><c:v>2025</c:v></c:pt></c:strCache></c:strRef></c:cat>"
        + "<c:val><c:numRef><c:f>S!$B$2:$B$3</c:f><c:numCache><c:formatCode>#,##0</c:formatCode><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>1200</c:v></c:pt><c:pt idx=\"1\"><c:v>1800</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser>"
        + "</c:barChart></c:plotArea></c:chart></c:chartSpace>";

    internal static void AddChart(string path, string chartXml)
    {
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, isEditable: true);
        var sheetPart = document.WorkbookPart!.WorksheetParts.First();
        var drawings = sheetPart.AddNewPart<DocumentFormat.OpenXml.Packaging.DrawingsPart>();
        drawings.WorksheetDrawing = new DocumentFormat.OpenXml.Drawing.Spreadsheet.WorksheetDrawing();
        var chart = drawings.AddNewPart<DocumentFormat.OpenXml.Packaging.ChartPart>();
        chart.ChartSpace = new DocumentFormat.OpenXml.Drawing.Charts.ChartSpace(chartXml);
    }

    [Fact]
    public void Read_AChart_IsWrittenWithItsTitleSeriesAndSavedValues()
    {
        using var dir = new TempDir();
        var path = dir.Combine("c.xlsx");
        XlsxFactory.Create(path, new SheetSpec("S", [["", "Tokyo"], ["2024", "1200"], ["2025", "1800"]]));
        AddChart(path, BarChartXml);

        var markdown = XlsxReader.Read(path, false).Single().ToMarkdown();

        Assert.EndsWith("#### Chart: Sales by year (bar)\n\n|  | Tokyo |\n|---|---|\n| 2024 | 1,200 |\n| 2025 | 1,800 |", markdown);
    }

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
    public void Read_Markdown_OfAnExcelTable_HasTheFirstRowAsHeader()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["Name", "Note"], ["Apple", "a|b"]], ExcelTable: "A1:B2"));

        var sheet = XlsxReader.Read(path, false).Single();

        Assert.Equal("| Name | Note |\n|---|---|\n| Apple | a\\|b |", sheet.ToMarkdown());
    }

    [Fact]
    public void Read_Markdown_CutsTheBlocksAtEmptyRowsAndColumns_IntoTablesOfTheirOwn()
    {
        using var dir = new TempDir();
        var path = dir.Combine("blocks.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [
            ["A1", "B1", "", "C1", "D1"],
            ["A2", "B2", "", "C2", "D2"],
            ["", "", "", "", ""],
            ["E1", "F1", "G1", "", ""],
            ["E2", "F2", "G2", "", ""]]));

        var markdown = XlsxReader.Read(path, false).Single().ToMarkdown();

        Assert.Equal(
            "|  |  |\n|---|---|\n| A1 | B1 |\n| A2 | B2 |\n\n" +
            "|  |  |\n|---|---|\n| C1 | D1 |\n| C2 | D2 |\n\n" +
            "|  |  |  |\n|---|---|---|\n| E1 | F1 | G1 |\n| E2 | F2 | G2 |",
            markdown);
    }

    [Fact]
    public void Read_Markdown_RowsOfOneRowEach_WithEmptyRowsBetween_AreOneTable_AndATableOfSeveralRowsStaysApart()
    {
        using var dir = new TempDir();
        var path = dir.Combine("thin.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [
            ["Mon", "Tue"],
            ["", ""],
            ["1", "2"],
            ["", ""],
            ["3", "4"],
            ["", ""],
            ["x", "y"],
            ["z", "w"]]));

        var markdown = XlsxReader.Read(path, false).Single().ToMarkdown();

        Assert.Equal(
            "|  |  |\n|---|---|\n| Mon | Tue |\n| 1 | 2 |\n| 3 | 4 |\n\n" +
            "|  |  |\n|---|---|\n| x | y |\n| z | w |",
            markdown);
    }

    [Fact]
    public void Read_Markdown_ALabelThatRunsOverEmptyCells_IsNotCutFromTheValueBesideIt()
    {
        using var dir = new TempDir();
        var path = dir.Combine("overflow.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [
            ["Opportunity cost (if any) of the project=", "", "", "", "$7,484"],
            ["Lifetime of the investment overall=", "", "", "", "10"]]));

        var markdown = XlsxReader.Read(path, false).Single().ToMarkdown();

        Assert.DoesNotContain("\n\n", markdown);
        Assert.Contains("$7,484", markdown);
    }

    [Fact]
    public void Read_Markdown_OfASheetWithoutAnExcelTable_HasAnEmptyHeaderAndTheFirstRowAsData()
    {
        using var dir = new TempDir();
        var path = dir.Combine("b.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["Name", "Note"], ["Apple", "a|b"]]));

        var sheet = XlsxReader.Read(path, false).Single();

        Assert.Equal("|  |  |\n|---|---|\n| Name | Note |\n| Apple | a\\|b |", sheet.ToMarkdown());
    }

    [Fact]
    public void Read_Markdown_MarksTheCellsHiddenByAMergedCell_AndLeavesAnEmptyCellEmpty()
    {
        using var dir = new TempDir();
        var path = dir.Combine("m.xlsx");
        // A1:B1 spans two columns, C1 is empty (not merged); A2:A3 spans two rows.
        XlsxFactory.Create(path, new SheetSpec("Data", [["Title", "", ""], ["A", "b", "c"], ["", "d", ""]], Merges: ["A1:B1", "A2:A3"]));

        var markdown = MarkdownText.ResolveMarks(XlsxReader.Read(path, false).Single().ToMarkdown());

        Assert.Equal("|  |  |  |\n|---|---|---|\n| Title | < |  |\n| A | b | c |\n| ^ | d |  |", markdown);
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
