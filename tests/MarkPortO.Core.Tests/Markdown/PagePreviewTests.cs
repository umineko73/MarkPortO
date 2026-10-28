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
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-068: the pages of a converted document, read from the files of the output folder.</summary>
public class PagePreviewTests
{
    private static DocumentRecord Document(string folder, bool separate)
    {
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        File.WriteAllBytes(Path.Combine(folder, "images", "page-001.png"), [1]);
        return new DocumentRecord
        {
            MarkdownLanguage = "en",
            DocId = "d0001",
            SourcePath = @"D:\in\Report.docx",
            Kind = DocumentKind.Docx,
            OutputFolder = folder,
            Step1 = StepStatus.Done,
            Step2 = StepStatus.Done,
            Step3 = StepStatus.Done,
            VlmSeparate = separate,
            Pages =
            [
                new PageRecord { Index = 1, SourceLabel = "page 1", ImageFile = "images/page-001.png", Step3 = PageStep3Status.Done, VlmText = "READ-ONE" },
                new PageRecord { Index = 2, SourceLabel = "page 2", ImageFile = "images/page-002.png", Step3 = PageStep3Status.Pending },
            ],
            Blocks =
            [
                new Block { Kind = BlockKind.Paragraph, Markdown = "First page text.", PageIndex = 1 },
                new Block { Kind = BlockKind.Paragraph, Markdown = "Second page text.", PageIndex = 2 },
            ],
        };
    }

    [Fact]
    public void Load_GivesTheImageAndTheMarkdownOfEachPage()
    {
        using var dir = new TempDir();
        var document = Document(dir.Path, separate: false);
        MarkdownRenderer.Write(document);

        var pages = PagePreview.Load(document);

        Assert.Equal(2, pages.Count);
        Assert.Equal(Path.Combine(dir.Path, "images", "page-001.png"), pages[0].ImagePath);
        Assert.Null(pages[1].ImagePath); // the file is not there
        Assert.StartsWith("<!-- page: 1 -->", pages[0].Markdown, StringComparison.Ordinal);
        Assert.Contains("First page text.", pages[0].Markdown);
        Assert.DoesNotContain("Second page text.", pages[0].Markdown);
        Assert.Contains("Second page text.", pages[1].Markdown);
        Assert.Equal("READ-ONE", pages[0].Vlm);
        Assert.Null(pages[1].Vlm);
    }

    [Fact]
    public void Load_TakesTheResultsFromTheSeparateFile()
    {
        using var dir = new TempDir();
        var document = Document(dir.Path, separate: true);
        MarkdownRenderer.Write(document);

        var pages = PagePreview.Load(document);

        Assert.DoesNotContain("READ-ONE", pages[0].Markdown);
        Assert.Contains("READ-ONE", pages[0].Vlm);
    }

    [Fact]
    public void Load_WithoutTheFiles_GivesEmptyPages()
    {
        using var dir = new TempDir();
        var document = Document(dir.Path, separate: false);

        var pages = PagePreview.Load(document);

        Assert.All(pages, p => Assert.Equal(string.Empty, p.Markdown));
        Assert.All(pages, p => Assert.Null(p.Vlm));
    }

    [Fact]
    public void SplitPages_CutsAtThePageMarkers_AndLeavesTheWordsBeforeTheFirstPage()
    {
        var pages = PagePreview.SplitPages("# Title\n\n> note\n\n<!-- page: 1 -->\n\nA\n\n<!-- page: 2 -->\n\nB\n");

        Assert.Equal(["1", "2"], pages.Keys.Select(k => k.ToString()));
        Assert.Equal("<!-- page: 1 -->\n\nA", pages[1]);
        Assert.Equal("<!-- page: 2 -->\n\nB", pages[2]);
    }

    [Fact]
    public void WithoutVlm_TakesOutTheResultOfTheVlm_AndKeepsTheRest()
    {
        var text = PagePreview.WithoutVlm("<!-- page: 1 -->\n\nBody\n\n<!-- vlm:begin -->\nRead by the VLM\n<!-- vlm:end -->\n");

        Assert.Equal("<!-- page: 1 -->\n\nBody", text);
    }
}
