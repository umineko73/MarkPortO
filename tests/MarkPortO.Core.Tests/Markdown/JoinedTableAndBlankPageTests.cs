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

using System.Drawing;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Pdf;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-078 (a table over several pages is one table in the Markdown) and FR-082 (a blank page is not sent to the VLM).</summary>
public class JoinedTableAndBlankPageTests
{
    private static DocumentRecord Document(params Block[] blocks) => new()
    {
        MarkdownLanguage = "ja",
        DocId = "d0001",
        SourcePath = @"D:\in\a.docx",
        Kind = DocumentKind.Docx,
        OutputFolder = @"D:\out\a",
        Step1 = StepStatus.Done,
        Step2 = StepStatus.Done,
        Step3 = StepStatus.NotApplicable,
        Pages =
        [
            new PageRecord { Index = 1, SourceLabel = "page:1", ImageFile = "images/page-001.png" },
            new PageRecord { Index = 2, SourceLabel = "page:2", ImageFile = "images/page-002.png" },
            new PageRecord { Index = 3, SourceLabel = "page:3", ImageFile = "images/page-003.png" },
        ],
        Blocks = [.. blocks],
    };

    private static Block Table(int page, string markdown, int? joinedTo = null) =>
        new() { Kind = BlockKind.Table, Markdown = markdown, PageIndex = page, JoinedToPage = joinedTo };

    [Fact]
    public void Render_ATableOverSeveralPages_IsOneTable_AndTheLaterPagesSayWhereTheRowsWent()
    {
        var document = Document(
            Table(1, "| No | Item |\n|---|---|\n| 1 | Pencil |"),
            Table(2, "| No | Item |\n|---|---|\n| 2 | Eraser |", joinedTo: 1),
            Table(3, "| No | Item |\n|---|---|\n| 3 | Ruler |", joinedTo: 1));

        var markdown = MarkdownRenderer.Render(document);

        Assert.Contains("| No | Item |\n|---|---|\n| 1 | Pencil |\n| 2 | Eraser |\n| 3 | Ruler |", markdown);
        Assert.Equal(1, markdown.Split("| No | Item |").Length - 1); // one header row
        Assert.Equal(2, markdown.Split("> この表の行は、ページ1の表に結合しました。").Length - 1);
        Assert.Contains("複数のページにまたがる表は、1つの表として", markdown); // the note on the structure
    }

    [Fact]
    public void Render_APartWhoseFirstPartIsNotThere_StaysATableOfItsOwn()
    {
        var document = Document(Table(2, "| No | Item |\n|---|---|\n| 2 | Eraser |", joinedTo: 1));

        var markdown = MarkdownRenderer.Render(document);

        Assert.Contains("| 2 | Eraser |", markdown);
        Assert.DoesNotContain("結合しました", markdown);
    }

    [Fact]
    public void Render_ABlankPage_SaysSo()
    {
        var document = Document();
        document.Pages[1].IsBlank = true;

        var markdown = MarkdownRenderer.Render(document);

        Assert.Contains("> このページは空白です。", markdown);
    }

    [Fact]
    public void PageBlank_APageWithNothingOnIt_IsBlank_AndAHeaderOrAFooterDoesNotCount()
    {
        using var page = new Bitmap(400, 600);
        using (var g = Graphics.FromImage(page))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 150, 5, 100, 12);    // a header
            g.FillRectangle(Brushes.Black, 180, 580, 40, 12);   // a page number
        }

        Assert.True(PageBlank.IsBlank(page));
    }

    [Fact]
    public void PageBlank_APageWithAFewWordsOrADrawing_IsNotBlank()
    {
        using var page = new Bitmap(400, 600);
        using var g = Graphics.FromImage(page);
        g.Clear(Color.White);
        g.FillRectangle(Brushes.Black, 100, 300, 12, 12);

        Assert.False(PageBlank.IsBlank(page));
    }

    [Fact]
    public void PageBlank_ATransparentPage_IsBlank()
    {
        using var page = new Bitmap(400, 600, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(page))
        {
            g.Clear(Color.Transparent);
        }

        Assert.True(PageBlank.IsBlank(page));
    }
}
