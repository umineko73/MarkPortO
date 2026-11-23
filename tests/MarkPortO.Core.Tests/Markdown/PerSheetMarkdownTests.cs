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
using MarkPortO.Core.Convert;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-153: an xlsx written as one Markdown for each sheet.</summary>
public class PerSheetMarkdownTests
{
    /// <summary>A book of three sheets: 売上 on pages 1 and 2 (two parts of its columns), 費用 on page 3, and a sheet whose name gives the same file name as 売上.</summary>
    private static DocumentRecord Book(string folder)
    {
        var doc = new DocumentRecord
        {
            MarkdownLanguage = "ja",
            DocId = "d0001",
            SourcePath = @"D:\in\報告書.xlsx",
            Kind = DocumentKind.Xlsx,
            OutputFolder = folder,
            Step1 = StepStatus.Done,
            Step2 = StepStatus.Done,
            Step3 = StepStatus.NotApplicable,
            ExcelPerSheet = true,
            Chunks = true,
            PageImage = PageImageStyle.Reference,
        };
        doc.Pages =
        [
            new PageRecord { Index = 1, ImageFile = "images/page-001.png", Sheet = new SheetInfo("売上", "A:H", 1, 2) },
            new PageRecord { Index = 2, ImageFile = "images/page-002.png", Sheet = new SheetInfo("売上", "I:P", 2, 2) },
            new PageRecord { Index = 3, ImageFile = "images/page-003.png", Sheet = new SheetInfo("費用", "A:C", 1, 1) },
            new PageRecord { Index = 4, ImageFile = "images/page-004.png", Sheet = new SheetInfo("売上.", "A:B", 1, 1) },
        ];
        doc.Blocks =
        [
            new Block { Kind = BlockKind.SheetTable, Markdown = "| 月 | 売上 |\n|---|---|\n| 1 | 100 |", PageIndex = 1 },
            new Block { Kind = BlockKind.SheetTable, Markdown = "| 地域 |\n|---|\n| 東 |", PageIndex = 2 },
            new Block { Kind = BlockKind.SheetTable, Markdown = "| 費目 | 額 |\n|---|---|\n| 紙 | 5 |", PageIndex = 3 },
            new Block { Kind = BlockKind.SheetTable, Markdown = "| x |\n|---|\n| y |", PageIndex = 4 },
        ];
        return doc;
    }

    [Fact]
    public void EachSheetGetsAFile_NamedAfterTheBookAndTheSheet_AndTwoSheetsOfTheSameFileNameAreNumbered()
    {
        var sheets = MarkdownRenderer.SheetOutputs(Book(@"D:\out\報告書"));

        Assert.Equal(["報告書_売上", "報告書_費用", "報告書_売上 (2)"], sheets.Select(s => s.BaseName));
        Assert.Equal([1, 2], sheets[0].Pages.Order());
        Assert.Equal([3], sheets[1].Pages);
        Assert.Equal("売上.", sheets[2].Sheet); // a name with a dot at the end gives the same file name (Windows drops it)
    }

    [Fact]
    public void Write_WritesTheFilesOfTheSheets_WithTheirPagesOnly_AndNoMarkdownOfTheBook()
    {
        using var dir = new TempDir();
        var doc = Book(dir.Path);

        MarkdownRenderer.Write(doc);

        Assert.False(File.Exists(dir.Combine("報告書.md")));
        Assert.False(File.Exists(dir.Combine("報告書_chunks.jsonl")));
        var sales = File.ReadAllText(dir.Combine("報告書_売上.md"));
        Assert.StartsWith("# 報告書.xlsx - 売上\n", sales, StringComparison.Ordinal);
        Assert.Contains("<!-- page: 1 -->", sales, StringComparison.Ordinal);
        Assert.Contains("<!-- page: 2 -->", sales, StringComparison.Ordinal); // the page numbers of the book
        Assert.Contains("<!-- page-image: images/page-002.png -->", sales, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- page: 3 -->", sales, StringComparison.Ordinal);
        Assert.DoesNotContain("紙", sales, StringComparison.Ordinal);
        var costs = File.ReadAllText(dir.Combine("報告書_費用.md"));
        Assert.Contains("<!-- page: 3 -->", costs, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- page: 1 -->", costs, StringComparison.Ordinal);
        Assert.Equal(["報告書_売上", "報告書_費用", "報告書_売上 (2)"], doc.SheetFiles);

        // The chunks of a sheet are of its file and its pages.
        var line = Assert.Single(File.ReadAllLines(dir.Combine("報告書_費用_chunks.jsonl")));
        using var chunk = JsonDocument.Parse(line);
        Assert.Equal("報告書_費用.md", chunk.RootElement.GetProperty("markdown").GetString());
        Assert.Equal(3, chunk.RootElement.GetProperty("pages")[0].GetInt32());
    }

    [Fact]
    public void WrittenAsOneMarkdownAgain_TheFilesOfTheSheetsGo_ButNoOtherFile()
    {
        using var dir = new TempDir();
        var doc = Book(dir.Path);
        MarkdownRenderer.Write(doc);
        File.WriteAllText(dir.Combine("報告書_メモ.md"), "the user's own file");

        doc.ExcelPerSheet = false;
        MarkdownRenderer.Write(doc);

        Assert.True(File.Exists(dir.Combine("報告書.md")));
        Assert.True(File.Exists(dir.Combine("報告書_chunks.jsonl")));
        Assert.False(File.Exists(dir.Combine("報告書_売上.md")));
        Assert.False(File.Exists(dir.Combine("報告書_売上_chunks.jsonl")));
        Assert.True(File.Exists(dir.Combine("報告書_メモ.md")));
        Assert.Null(doc.SheetFiles);

        doc.ExcelPerSheet = true;
        MarkdownRenderer.Write(doc);
        Assert.False(File.Exists(dir.Combine("報告書.md")));
        Assert.False(File.Exists(dir.Combine("報告書_chunks.jsonl")));
        Assert.True(File.Exists(dir.Combine("報告書_売上.md")));
    }

    [Fact]
    public void TheVlmResultsInASeparateFile_AreSplitByTheSheetsToo()
    {
        using var dir = new TempDir();
        var doc = Book(dir.Path);
        doc.Step3 = StepStatus.Done;
        doc.VlmSeparate = true;
        foreach (var page in doc.Pages)
        {
            page.Step3 = PageStep3Status.Done;
            page.ReadAs = PageReading.Whole;
            page.VlmText = "VLM のページ " + page.Index;
        }

        MarkdownRenderer.Write(doc);

        var vlm = File.ReadAllText(dir.Combine("報告書_費用_vlm.md"));
        Assert.Contains("VLM のページ 3", vlm, StringComparison.Ordinal);
        Assert.DoesNotContain("VLM のページ 1", vlm, StringComparison.Ordinal);
        Assert.Contains("報告書_費用.md", vlm, StringComparison.Ordinal); // it says where the text is
        Assert.False(File.Exists(dir.Combine("報告書_vlm.md")));
    }

    [Fact]
    public void ThePreview_ShowsEachPageFromTheFileOfItsSheet()
    {
        using var dir = new TempDir();
        var doc = Book(dir.Path);
        MarkdownRenderer.Write(doc);

        var pages = PagePreview.Load(doc);

        Assert.Contains("| 1 | 100 |", pages.Single(p => p.Index == 1).Markdown, StringComparison.Ordinal);
        Assert.Contains("| 紙 | 5 |", pages.Single(p => p.Index == 3).Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpacesAroundTheNameOfASheet_AreNotInTheTitleNorInTheHeadings_ButTheSheetKeepsItsName()
    {
        using var dir = new TempDir();
        var doc = Book(dir.Path);
        doc.Pages[2].Sheet = new SheetInfo("  費用  ", "A:C", 1, 1);

        MarkdownRenderer.Write(doc);

        Assert.StartsWith("# 報告書.xlsx - 費用\n", File.ReadAllText(dir.Combine("報告書_費用.md")), StringComparison.Ordinal);
        using var chunk = JsonDocument.Parse(Assert.Single(File.ReadAllLines(dir.Combine("報告書_費用_chunks.jsonl"))));
        Assert.Equal(["費用"], chunk.RootElement.GetProperty("headings").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("  費用  ", chunk.RootElement.GetProperty("sheet").GetString());
    }

    [Fact]
    public void AnXlsxOfAnExtractor_OrAnotherKind_IsOneMarkdown()
    {
        using var dir = new TempDir();
        var extracted = Book(dir.Path);
        extracted.Extractor = "markitdown";
        var docx = Book(dir.Path);
        docx.Kind = DocumentKind.Docx;

        Assert.Empty(MarkdownRenderer.SheetOutputs(extracted));
        Assert.Empty(MarkdownRenderer.SheetOutputs(docx));
    }

    [Fact]
    public void TheResultFile_NamesTheFilesOfTheSheets()
    {
        using var dir = new TempDir();
        var doc = Book(dir.Path);

        var json = ConvertResultJson.Serialize(new ConvertResult(new JobState(), [doc]));

        using var result = JsonDocument.Parse(json);
        var entry = result.RootElement.GetProperty("documents")[0];
        Assert.Equal("報告書_売上.md", entry.GetProperty("markdown").GetString());
        Assert.Equal(["報告書_売上.md", "報告書_費用.md", "報告書_売上 (2).md"], entry.GetProperty("markdownFiles").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("報告書_売上_chunks.jsonl", entry.GetProperty("chunks").GetString());
    }
}
