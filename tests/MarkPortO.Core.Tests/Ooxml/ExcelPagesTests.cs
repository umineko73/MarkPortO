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

using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>FR-090: a sheet that is printed on several pages, one below the other, is one table in the Markdown of the document.</summary>
public class ExcelPagesTests
{
    private static SheetContent Sheet(TempDir dir, string[][] rows)
    {
        var path = dir.Combine("sheet.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", rows));
        return XlsxReader.Read(path, includeHidden: false).Single();
    }

    private static readonly string[][] Rows =
    [
        ["No", "Item", "Amount"],
        ["1", "Pencil", "100"],
        ["2", "Eraser", "200"],
        ["3", "Ruler", "300"],
        ["4", "Compass", "400"],
    ];

    [Fact]
    public void ToMarkdownParts_CutsATableAtThePageBreaks_OneTableForEachPage()
    {
        using var dir = new TempDir();
        var sheet = Sheet(dir, Rows);

        var parts = sheet.ToMarkdownParts(0, 2, [0, 0, 0, 1, 1]);

        Assert.Equal([0, 1], parts.Select(p => p.Page));
        Assert.All(parts, p => Assert.Equal(0, p.HeadPage));
        Assert.Equal("|  |  |  |\n|---|---|---|\n| No | Item | Amount |\n| 1 | Pencil | 100 |\n| 2 | Eraser | 200 |", parts[0].Markdown);
        Assert.Equal("|  |  |  |\n|---|---|---|\n| 3 | Ruler | 300 |\n| 4 | Compass | 400 |", parts[1].Markdown);
    }

    [Fact]
    public void ToMarkdown_WithoutPages_IsTheSameTableAsBefore()
    {
        using var dir = new TempDir();
        var sheet = Sheet(dir, Rows);

        Assert.Equal(
            "|  |  |  |\n|---|---|---|\n| No | Item | Amount |\n| 1 | Pencil | 100 |\n| 2 | Eraser | 200 |\n| 3 | Ruler | 300 |\n| 4 | Compass | 400 |",
            sheet.ToMarkdown(0, 2));
    }

    [Fact]
    public void Assign_PutsEachRowOnThePageWhereItsTextIs()
    {
        using var dir = new TempDir();
        var sheet = Sheet(dir, Rows);

        var pages = ExcelRowPages.Assign(sheet, 0, 2, ["No Item Amount 1 Pencil 100 2 Eraser 200", "3 Ruler 300 4 Compass 400"]);

        Assert.Equal([0, 0, 0, 1, 1], pages);
    }

    [Fact]
    public void Assign_ARowThatIsNotFound_StaysOnThePageOfTheRowBeforeIt()
    {
        using var dir = new TempDir();
        var sheet = Sheet(dir, Rows);

        var pages = ExcelRowPages.Assign(sheet, 0, 2, ["No Item Amount 1 Pencil 100", "3 Ruler 300 4 Compass 400"]);

        Assert.Equal([0, 0, 0, 1, 1], pages); // "2 Eraser 200" is not in the text: it stays with the row before it
    }

    [Fact]
    public void Assign_ASheetOnOnePage_IsAllOnPageZero()
    {
        using var dir = new TempDir();
        var sheet = Sheet(dir, Rows);

        Assert.All(ExcelRowPages.Assign(sheet, 0, 2, ["No Item Amount 1 Pencil 100 2 Eraser 200 3 Ruler 300 4 Compass 400"]), page => Assert.Equal(0, page));
    }

    [Fact]
    public void TheParts_OfASheet_AreJoinedInTheMarkdown_AndInTheResultsOfTheVlm()
    {
        var document = new DocumentRecord
        {
            MarkdownLanguage = "ja",
            DocId = "d0001",
            SourcePath = @"D:\in\a.xlsx",
            Kind = DocumentKind.Xlsx,
            OutputFolder = @"D:\out\a",
            Step1 = StepStatus.Done,
            Step2 = StepStatus.NotApplicable,
            Step3 = StepStatus.Done,
            Pages =
            [
                new PageRecord { Index = 1, SourceLabel = "sheet:Data", ImageFile = "images/page-001.png", Step3 = PageStep3Status.Done, VlmText = "| No | Item |\n|---|---|\n| 1 | a |" },
                new PageRecord { Index = 2, SourceLabel = "sheet:Data", ImageFile = "images/page-002.png", Step3 = PageStep3Status.Done, VlmText = "| 2 | b |\n|---|---|\n| 3 | c |" },
            ],
            Blocks =
            [
                new Block { Kind = BlockKind.SheetTable, Markdown = "|  |  |\n|---|---|\n| No | Item |\n| 1 | a |", PageIndex = 1 },
                new Block { Kind = BlockKind.SheetTable, Markdown = "|  |  |\n|---|---|\n| 2 | b |\n| 3 | c |", PageIndex = 2, JoinedToPage = 1 },
            ],
        };

        var markdown = MarkdownRenderer.Render(document);
        var vlm = MarkdownRenderer.RenderVlm(document);

        Assert.Contains("|  |  |\n|---|---|\n| No | Item |\n| 1 | a |\n| 2 | b |\n| 3 | c |", markdown);
        Assert.Contains("| No | Item |\n|---|---|\n| 1 | a |\n| 2 | b |\n| 3 | c |\n<!-- vlm:end -->", vlm);
        Assert.Contains("> この表の行は、ページ1の表に結合しました。", vlm);
    }
}
