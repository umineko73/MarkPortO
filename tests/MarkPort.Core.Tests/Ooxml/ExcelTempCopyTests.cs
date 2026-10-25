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

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using MarkPort.Core.Engines;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Ooxml;
using MarkPort.Core.Pdf;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.Ooxml;

public class ExcelTempCopyTests
{
    private static (SheetContent Sheet, ExcelUnit Unit) Plan(string path, string sheetName, int part = 1)
    {
        var sheet = XlsxReader.Read(path, includeHidden: true).Single(s => s.Name == sheetName);
        return (sheet, ExcelUnitPlanner.Plan(sheet).Single(u => u.Part == part));
    }

    [Fact]
    public void Create_PrintsOnlyTheTargetSheet_AndReplacesTheOldPrintArea()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(
            source,
            new SheetSpec("Other", [["o"]]),
            // The author's print area covers only A1:B1; the data is larger (FR-010b: ignore the print area).
            new SheetSpec("Data", [["a", "b", "c"], ["d", "e", "f"], ["g", "h", "i"]], PrintArea: "A1:B1"),
            new SheetSpec("Secret", [["s"]], Hidden: true));
        var (sheet, unit) = Plan(source, "Data");
        var copy = dir.Combine("work", "copy.xlsx");

        ExcelTempCopy.Create(source, copy, sheet, unit);

        using var document = SpreadsheetDocument.Open(copy, false);
        var workbook = document.WorkbookPart!.Workbook!;
        var states = workbook.Sheets!.Elements<Sheet>().ToDictionary(s => s.Name!.Value!, s => s.State?.Value);
        Assert.Null(states["Data"]); // visible
        Assert.Equal(SheetStateValues.Hidden, states["Other"]);
        Assert.Equal(SheetStateValues.Hidden, states["Secret"]);

        // Only the target's print area remains; the author's print area (A1:B1) and the other sheets' are gone.
        var printArea = Assert.Single(workbook.DefinedNames!.Elements<DefinedName>(), n => n.Name!.Value == "_xlnm.Print_Area");
        Assert.Equal("'Data'!$A$1:$C$3", printArea.Text);
        Assert.Equal(0u, printArea.LocalSheetId!.Value); // the target is the first sheet in the copy
    }

    [Fact]
    public void Create_MovesTheTargetSheetToTheFront_AndKeepsLocalNamesOnTheirSheets()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(
            source,
            new SheetSpec("First", [["f"]], PrintArea: "A1:A1"),
            new SheetSpec("Target", [["t1", "t2"]]),
            new SheetSpec("Last", [["l"]], PrintArea: "A1:A1"));
        var (sheet, unit) = Plan(source, "Target");
        var copy = dir.Combine("copy.xlsx");

        ExcelTempCopy.Create(source, copy, sheet, unit);

        using var document = SpreadsheetDocument.Open(copy, false);
        var workbook = document.WorkbookPart!.Workbook!;
        Assert.Equal(["Target", "First", "Last"], workbook.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value));
        Assert.Equal(["t1"], [XlsxReader.Read(copy, includeHidden: true).Single(s => s.Name == "Target").Rows[0][0]]);
    }

    [Fact]
    public void Create_SetsA3LandscapeAndFitToOnePageWide()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Data", [["a", "b"], ["c", "d"]]));
        var (sheet, unit) = Plan(source, "Data");
        var copy = dir.Combine("copy.xlsx");

        ExcelTempCopy.Create(source, copy, sheet, unit);

        using var document = SpreadsheetDocument.Open(copy, false);
        var worksheet = ((WorksheetPart)document.WorkbookPart!.GetPartById(
            document.WorkbookPart.Workbook!.Sheets!.Elements<Sheet>().Single().Id!.Value!)).Worksheet!;
        Assert.True(worksheet.SheetProperties!.PageSetupProperties!.FitToPage!.Value);
        var setup = worksheet.GetFirstChild<PageSetup>()!;
        Assert.Equal(8u, setup.PaperSize!.Value);
        Assert.Equal(OrientationValues.Landscape, setup.Orientation!.Value);
        Assert.Equal(1u, setup.FitToWidth!.Value);
        Assert.Equal(0u, setup.FitToHeight!.Value);
    }

    [Fact]
    public void Create_RestrictsThePrintAreaToTheColumnsOfThePart()
    {
        using var dir = new TempDir();
        var source = dir.Combine("wide.xlsx");
        XlsxFactory.Create(source, XlsxFactory.WideSheet("Wide", columns: 60, rows: 3, columnWidthChars: 14));
        var (sheet, second) = Plan(source, "Wide", part: 2);
        var copy = dir.Combine("copy.xlsx");

        ExcelTempCopy.Create(source, copy, sheet, second);

        using var document = SpreadsheetDocument.Open(copy, false);
        var area = document.WorkbookPart!.Workbook!.DefinedNames!.Elements<DefinedName>().Single(n => n.Name!.Value == "_xlnm.Print_Area");
        Assert.Equal("'Wide'!$AA$1:$AZ$4", area.Text);
    }

    [Fact]
    public void Create_UnhidesAHiddenTargetSheet()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Visible", [["v"]]), new SheetSpec("Secret", [["s"]], Hidden: true));
        var (sheet, unit) = Plan(source, "Secret");
        var copy = dir.Combine("copy.xlsx");

        ExcelTempCopy.Create(source, copy, sheet, unit);

        using var document = SpreadsheetDocument.Open(copy, false);
        var states = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>().ToDictionary(s => s.Name!.Value!, s => s.State?.Value);
        Assert.Null(states["Secret"]);
        Assert.Equal(SheetStateValues.Hidden, states["Visible"]);
    }

    [Fact]
    public void Create_NeverChangesTheOriginal_AndTheCopyIsAValidWorkbook()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Data", [["a", "b"]], PrintArea: "A1:A1"), new SheetSpec("Other", [["o"]]));
        var before = File.ReadAllBytes(source);
        var beforeTime = File.GetLastWriteTimeUtc(source);
        var (sheet, unit) = Plan(source, "Data");
        var copy = dir.Combine("copy.xlsx");

        ExcelTempCopy.Create(source, copy, sheet, unit);

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(source));
        using var document = SpreadsheetDocument.Open(copy, false);
        var errors = new OpenXmlValidator().Validate(document).Select(e => $"{e.Description} @ {e.Path?.XPath}").ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Create_UnknownSheet_Fails()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Data", [["a"]]));
        var (sheet, unit) = Plan(source, "Data");
        var other = new SheetContent { Name = "Nope", FirstColumn = 1, LastColumn = 1, FirstRow = 1, LastRow = 1, Rows = sheet.Rows, ColumnWidthsPoints = sheet.ColumnWidthsPoints };

        Assert.Throws<InvalidDocumentException>(() => ExcelTempCopy.Create(source, dir.Combine("c.xlsx"), other, unit));
    }

    // The point of the copy: the engine prints every column of the part on one page width (FR-011).
    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task LibreOffice_PrintsAllColumnsOfAnOrdinaryWideSheetOnOnePage()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        // 20 columns x 73.5 pt = 1470 pt: wider than one A3 page, but it fits at a scale of about 0.78.
        XlsxFactory.Create(source, XlsxFactory.WideSheet("Wide", columns: 20, rows: 5, columnWidthChars: 14));
        var (sheet, unit) = Plan(source, "Wide");
        Assert.Equal(1, unit.PartCount);
        var copy = dir.Combine("work", "copy.xlsx");
        ExcelTempCopy.Create(source, copy, sheet, unit);

        var pages = await ToPdfPages(dir, copy);

        var page = Assert.Single(pages);
        Assert.Contains("H1", page);
        Assert.Contains("H20", page);
        Assert.Contains("r5c20", page);
    }

    // LibreOffice prints the FIRST sheet of a workbook even when it is hidden. The copy puts the target first,
    // so no page of another sheet ends up in the PDF (found by the xlsx end-to-end test).
    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task LibreOffice_PrintsOnlyTheTargetSheet_EvenWhenAnotherSheetComesFirstAndHasAPrintArea()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(
            source,
            new SheetSpec("Other", [["other-sheet-text"]], PrintArea: "A1:A1"),
            XlsxFactory.WideSheet("Wide", columns: 4, rows: 2));
        var sheet = XlsxReader.Read(source, false).Single(s => s.Name == "Wide");
        var unit = ExcelUnitPlanner.Plan(sheet).Single();
        var copy = dir.Combine("work", "copy.xlsx");
        ExcelTempCopy.Create(source, copy, sheet, unit);

        var pages = await ToPdfPages(dir, copy);

        var page = Assert.Single(pages);
        Assert.Contains("H1", page);
        Assert.DoesNotContain("other-sheet-text", page);
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task LibreOffice_PrintsAnExtremelyWideSheetAsSeparatePartsThatTogetherHaveEveryColumn()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(source, XlsxFactory.WideSheet("Wide", columns: 60, rows: 3, columnWidthChars: 14));
        var sheet = XlsxReader.Read(source, false).Single();
        var units = ExcelUnitPlanner.Plan(sheet);
        Assert.Equal(3, units.Count);

        var seen = new HashSet<int>();
        foreach (var unit in units)
        {
            var copy = dir.Combine("work", $"part{unit.Part}.xlsx");
            ExcelTempCopy.Create(source, copy, sheet, unit);
            var text = string.Concat(await ToPdfPages(dir, copy));

            foreach (var column in Enumerable.Range(unit.FromColumnIndex + 1, unit.ToColumnIndex - unit.FromColumnIndex + 1))
            {
                Assert.True(ContainsToken(text, $"r3c{column}"), $"part {unit.Part} should contain column {column}");
                seen.Add(column);
            }

            // A column of another part must not appear in this part.
            var outside = unit.ToColumnIndex + 2 <= 60 ? unit.ToColumnIndex + 2 : unit.FromColumnIndex;
            if (unit.Part < units.Count)
            {
                Assert.False(ContainsToken(text, $"r3c{outside}"), $"part {unit.Part} should not contain column {outside}");
            }
        }

        Assert.Equal(Enumerable.Range(1, 60), seen.Order());
    }

    // "r3c1" must not match inside "r3c12": the PDF text puts cells next to each other without a separator.
    private static bool ContainsToken(string text, string token)
    {
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            var next = index + token.Length;
            if (next >= text.Length || !char.IsDigit(text[next]))
            {
                return true;
            }

            index = next;
        }

        return false;
    }

    private static async Task<IReadOnlyList<string>> ToPdfPages(TempDir dir, string workbook)
    {
        using var engine = new LibreOfficeEngine(profileRoot: dir.Combine("profiles"));
        var pdf = Path.ChangeExtension(workbook, ".pdf");
        await engine.ExportPdfAsync(new PdfExportRequest(workbook, DocumentKind.Xlsx, pdf, PdfExportRequest.DefaultTimeout), CancellationToken.None);
        return PdfTextExtractor.GetPageTexts(pdf);
    }
}
