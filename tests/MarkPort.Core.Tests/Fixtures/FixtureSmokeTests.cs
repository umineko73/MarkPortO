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

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace MarkPort.Core.Tests.Fixtures;

/// <summary>The generated documents must be valid Office files; the later tests rely on them.</summary>
public class FixtureSmokeTests
{
    [Fact]
    public void DocxFactory_ProducesValidDocuments()
    {
        using var dir = new TempDir();
        var paged = dir.Combine("paged.docx");
        var structured = dir.Combine("structured.docx");
        var flow = dir.Combine("flow.docx");
        DocxFactory.CreatePaged(paged, ["P1-a", "P1-b"], ["P2-a"], ["P3-a"]);
        DocxFactory.CreateStructured(structured);
        DocxFactory.CreateFlow(flow, 40);

        foreach (var path in new[] { paged, structured, flow })
        {
            using var document = WordprocessingDocument.Open(path, false);
            AssertValid(document);
        }
    }

    [Fact]
    public void XlsxFactory_ProducesValidWorkbooks()
    {
        using var dir = new TempDir();
        var path = dir.Combine("book.xlsx");
        XlsxFactory.Create(
            path,
            new SheetSpec("Data", [["Name", "Qty"], ["Apple", "3"], ["Sum", "=B2+1::4"]], PrintArea: "A1:B3", Merges: ["A1:B1"]),
            new SheetSpec("Hidden", [["secret"]], Hidden: true),
            XlsxFactory.WideSheet("Wide", columns: 60, rows: 5));

        using var document = SpreadsheetDocument.Open(path, false);
        AssertValid(document);
        Assert.Equal(3, document.WorkbookPart!.Workbook!.Sheets!.Count());
    }

    [Fact]
    public void PptxFactory_ProducesValidPresentations()
    {
        using var dir = new TempDir();
        var path = dir.Combine("deck.pptx");
        PptxFactory.Create(
            path,
            new SlideSpec("First", ["body one", "body two"]),
            new SlideSpec("Hidden", ["secret"], Hidden: true),
            new SlideSpec("Table", Table: [["A", "B"], ["1", "2"]]));

        using var document = PresentationDocument.Open(path, false);
        AssertValid(document);
        Assert.Equal(3, document.PresentationPart!.SlideParts.Count());
    }

    private static void AssertValid(OpenXmlPackage document)
    {
        var errors = new OpenXmlValidator().Validate(document)
            .Select(e => $"{e.Description} @ {e.Path?.XPath}")
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void XlsxFactory_ColumnName_FollowsExcelNaming()
    {
        Assert.Equal("A", XlsxFactory.ColumnName(1));
        Assert.Equal("Z", XlsxFactory.ColumnName(26));
        Assert.Equal("AA", XlsxFactory.ColumnName(27));
        Assert.Equal("AZ", XlsxFactory.ColumnName(52));
        Assert.Equal("BA", XlsxFactory.ColumnName(53));
    }
}
