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

using System.Text.Json;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pipeline;

namespace MarkPortO.Core.Tests.Pipeline;

/// <summary>FR-117 (a page of the rows of a joined table only is not sent to the VLM), FR-116 (the comment of the converter) and FR-073 (the whole sheet on one page).</summary>
public class JoinedTableOnlyTests
{
    private static Block Table(int page, int? joinedTo = null) =>
        new() { Kind = BlockKind.Table, Markdown = "| a | b |\n|---|---|\n| 1 | 2 |", PageIndex = page, JoinedToPage = joinedTo };

    private static DocumentRecord Document(Step1TablesMode mode, params Block[] blocks) => new()
    {
        MarkdownLanguage = "ja",
        DocId = "d0001",
        SourcePath = @"D:\in\a.docx",
        Kind = DocumentKind.Docx,
        OutputFolder = @"D:\out\a",
        Step1 = StepStatus.Done,
        Step2 = StepStatus.Done,
        Step3 = StepStatus.Running,
        Step1Tables = mode,
        Pages =
        [
            new PageRecord { Index = 1, SourceLabel = "page:1", ImageFile = "images/page-001.png" },
            new PageRecord { Index = 2, SourceLabel = "page:2", ImageFile = "images/page-002.png" },
            new PageRecord { Index = 3, SourceLabel = "page:3", ImageFile = "images/page-003.png" },
        ],
        Blocks = [.. blocks],
    };

    [Theory]
    [InlineData(Step1TablesMode.Replace)]
    [InlineData(Step1TablesMode.Omit)]
    public void WhenTheTablesOfStep1AreUsed_ThePagesOfJoinedRowsOnly_AreNotRead(Step1TablesMode mode)
    {
        var document = Document(mode, Table(1), Table(2, joinedTo: 1), Table(3, joinedTo: 1));

        Step3Worker.MarkJoinedTableOnly(document);

        Assert.Equal([false, true, true], document.Pages.Select(p => p.IsJoinedTableOnly));
        Assert.Equal([true, false, false], document.Pages.Select(p => p.ReadsInStep3));
    }

    [Fact]
    public void WithTheTablesOfTheVlm_EveryPageIsRead()
    {
        var document = Document(Step1TablesMode.Off, Table(1), Table(2, joinedTo: 1));

        Step3Worker.MarkJoinedTableOnly(document);

        Assert.All(document.Pages, p => Assert.True(p.ReadsInStep3));
    }

    [Fact]
    public void APageWithAParagraphBesideTheJoinedRows_IsRead()
    {
        var document = Document(
            Step1TablesMode.Replace,
            Table(1),
            Table(2, joinedTo: 1),
            new Block { Kind = BlockKind.Paragraph, Markdown = "A paragraph.", PageIndex = 2 });

        Step3Worker.MarkJoinedTableOnly(document);

        Assert.False(document.Pages[1].IsJoinedTableOnly);
    }

    [Fact]
    public void TheChoiceIsMadeAgainWhenTheModeChanges()
    {
        var document = Document(Step1TablesMode.Replace, Table(1), Table(2, joinedTo: 1));
        Step3Worker.MarkJoinedTableOnly(document);
        Assert.True(document.Pages[1].IsJoinedTableOnly);

        document.Step1Tables = Step1TablesMode.Off;
        Step3Worker.MarkJoinedTableOnly(document);

        Assert.False(document.Pages[1].IsJoinedTableOnly);
    }

    [Fact]
    public void TheMarkdown_SaysWhyTheVlmDidNotReadThePage()
    {
        var document = Document(Step1TablesMode.Replace, Table(1), Table(2, joinedTo: 1));
        document.Step3 = StepStatus.Done;
        document.Pages[0].Step3 = PageStep3Status.Done;
        document.Pages[0].VlmText = "text";
        document.Pages[1].IsJoinedTableOnly = true;
        document.Pages[1].Step3 = PageStep3Status.Skipped;
        document.Pages[2].IsBlank = true;
        document.Pages[2].Step3 = PageStep3Status.Skipped;

        var markdown = MarkdownRenderer.Render(document);

        Assert.Contains("<!-- vlm:skipped page=2 reason=\"joined table\" -->", markdown, StringComparison.Ordinal);
        Assert.Contains("<!-- vlm:skipped page=3 reason=\"blank page\" -->", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConverterComment_NamesTheToolsTheModelAndThePrompt_InTheMainFileAndTheVlmFile()
    {
        var document = Document(Step1TablesMode.Off, Table(1));
        document.Converter = "MarkPortO 0.2.2; engine: Microsoft Word 16.0.1";
        document.VlmModel = "claude haiku";
        document.VlmPrompt = "cloud -- test"; // a double hyphen would end the comment

        var main = MarkdownRenderer.Render(document);
        var vlm = MarkdownRenderer.RenderVlm(document);

        foreach (var markdown in new[] { main, vlm })
        {
            Assert.Contains("<!-- converter: MarkPortO 0.2.2; engine: Microsoft Word 16.0.1; vlm: claude haiku; prompt: cloud - - test -->", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithoutTheConverter_NoCommentIsWritten_AndWithoutStep3_NoModel()
    {
        var old = Document(Step1TablesMode.Off, Table(1));
        Assert.DoesNotContain("converter:", MarkdownRenderer.Render(old), StringComparison.Ordinal);

        var plain = Document(Step1TablesMode.Off, Table(1));
        plain.Converter = "MarkPortO 0.2.2";
        var markdown = MarkdownRenderer.Render(plain);
        Assert.Contains("<!-- converter: MarkPortO 0.2.2 -->", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("vlm:", markdown.Split('\n').Single(l => l.StartsWith("<!-- converter", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionOfTheProgram_IsReadFromTheAssembly()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Current);
        Assert.DoesNotContain('+', AppVersion.Current);
    }

    private static SheetContent Sheet(double width, int rows) => new()
    {
        Name = "Data",
        FirstColumn = 1,
        LastColumn = 1,
        FirstRow = 1,
        LastRow = rows,
        Rows = [new string[] { "x" }],
        ColumnWidthsPoints = [width],
    };

    [Fact]
    public void TheWholeSheetOnOnePage_GetsTheDpiThatItsHeightNeeds()
    {
        // 2000 rows of 15 pt on a portrait page: the page is 1190.55 - 36 = 1154.55 pt high, so the scale is 1154.55 / 30000.
        var sheet = Sheet(width: 100, rows: 2000);
        var unit = Assert.Single(ExcelUnitPlanner.Plan(sheet, ExcelRange.Sheet));

        Assert.False(ExcelUnitPlanner.IsLandscape(sheet));
        Assert.Equal(0.6 / (1154.55 / 30000), ExcelUnitPlanner.DpiFactor(sheet, unit, ExcelRange.Sheet), 3);
    }

    [Fact]
    public void ASmallSheetOnOnePage_KeepsTheDpi_AndAWideOneIsLandscape()
    {
        var small = Sheet(width: 100, rows: 10);
        Assert.Equal(1.0, ExcelUnitPlanner.DpiFactor(small, Assert.Single(ExcelUnitPlanner.Plan(small, ExcelRange.Sheet)), ExcelRange.Sheet), 3);

        var wide = Sheet(width: 4000, rows: 20);
        Assert.True(ExcelUnitPlanner.IsLandscape(wide));
        Assert.True(ExcelUnitPlanner.DpiFactor(wide, Assert.Single(ExcelUnitPlanner.Plan(wide, ExcelRange.Sheet)), ExcelRange.Sheet) > 1.0);
    }

    [Fact]
    public void TheWholeSheet_IsNeverSplit_EvenWhenVeryWide()
    {
        var sheet = Sheet(width: ExcelUnitPlanner.MaxGroupWidthPoints * 3, rows: 5);

        Assert.Single(ExcelUnitPlanner.Plan(sheet, ExcelRange.Sheet));
        Assert.True(ExcelUnitPlanner.Plan(sheet, ExcelRange.Columns).Count >= 1);
    }

    [Theory]
    [InlineData("\"rows\"", ExcelRange.Columns)]
    [InlineData("\"Rows\"", ExcelRange.Columns)]
    [InlineData("\"sheet\"", ExcelRange.Sheet)]
    [InlineData("\"printarea\"", ExcelRange.PrintArea)]
    public void AnOldSettingOfRows_IsReadAsColumns(string json, ExcelRange expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<ExcelRange>(json, JsonDefaults.Options));
    }

    [Fact]
    public void TheRangeIsWrittenInLowerCase()
    {
        Assert.Equal("\"sheet\"", JsonSerializer.Serialize(ExcelRange.Sheet, JsonDefaults.Options));
    }
}
