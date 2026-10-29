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

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-089: a table that runs over several pages is one table in the results of the VLM, and the marks of the pages come after it.</summary>
public class VlmTableJoinTests
{
    private const string Note = "> この表の行は、ページ1の表に結合しました。";

    // pages: the result of the VLM of each page. continuesOn: the pages that have the later part of a table in the text that Step1 took.
    private static DocumentRecord Document(string[] pages, params int[] continuesOn)
    {
        var document = new DocumentRecord
        {
            MarkdownLanguage = "ja",
            DocId = "d0001",
            SourcePath = @"D:\in\a.docx",
            Kind = DocumentKind.Docx,
            OutputFolder = @"D:\out\a",
            Step1 = StepStatus.Done,
            Step2 = StepStatus.NotApplicable,
            Step3 = StepStatus.Done,
        };
        document.Pages =
        [
            .. pages.Select((text, i) => new PageRecord
            {
                Index = i + 1,
                SourceLabel = $"page:{i + 1}",
                ImageFile = $"images/page-{i + 1:000}.png",
                Step3 = PageStep3Status.Done,
                VlmText = text,
            }),
        ];
        document.Blocks =
        [
            new Block { Kind = BlockKind.Table, Markdown = "| a |\n|---|\n| b |", PageIndex = 1 },
            .. continuesOn.Select(page => new Block { Kind = BlockKind.Table, Markdown = "| a |\n|---|\n| c |", PageIndex = page, JoinedToPage = 1 }),
        ];
        return document;
    }

    // The results of the VLM, as they are written (the text that Step1 took has the same kind of note: it is not looked at here).
    private static string Vlm(DocumentRecord document) => MarkdownRenderer.RenderVlm(document);

    [Fact]
    public void TheRowsOfALaterPage_AreUnderTheTableOfTheFirstPage_AndTheHeaderWrittenAgainIsDropped()
    {
        var document = Document(
            ["Intro\n\n| No | Item |\n|---|---|\n| 1 | a |\n| 2 | b |", "| No | Item |\n|---|---|\n| 3 | c |\n| 4 | d |", "| No | Item |\n|---|---|\n| 5 | e |\n\nThe end."],
            continuesOn: [2, 3]);

        var text = Vlm(document);

        Assert.Contains("| No | Item |\n|---|---|\n| 1 | a |\n| 2 | b |\n| 3 | c |\n| 4 | d |\n| 5 | e |\n<!-- vlm:end -->", text);
        Assert.Equal(1, text.Split("| No | Item |").Length - 1);
        Assert.Equal(2, text.Split(Note).Length - 1);
        Assert.Contains("<!-- vlm:begin page=3 -->\n" + Note + "\n\nThe end.\n<!-- vlm:end -->", text);
        Assert.Contains("<!-- vlm:begin page=2 -->\n" + Note + "\n<!-- vlm:end -->", text);
    }

    [Fact]
    public void ThePageMarks_StandAfterTheWholeTable_NotBetweenItsRows()
    {
        var document = Document(["| No | Item |\n|---|---|\n| 1 | a |", "| No | Item |\n|---|---|\n| 2 | b |"], continuesOn: [2]);

        var text = Vlm(document);

        var firstRow = text.IndexOf("| 1 | a |", StringComparison.Ordinal);
        var lastRow = text.IndexOf("| 2 | b |", StringComparison.Ordinal);
        var endOfTheBlock = text.IndexOf("<!-- vlm:end -->", text.IndexOf("<!-- vlm:begin page=1", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.InRange(lastRow, firstRow + 1, endOfTheBlock);
        Assert.True(text.IndexOf("<!-- page: 2 -->", StringComparison.Ordinal) > lastRow);
    }

    [Fact]
    public void ARowsOnlyResultWithoutAHeader_IsJoinedToo()
    {
        var document = Document(["| No | Item |\n|---|---|\n| 1 | a |", "| 2 | b |\n| 3 | c |"], continuesOn: [2]);

        var text = Vlm(document);

        Assert.Contains("| 1 | a |\n| 2 | b |\n| 3 | c |\n<!-- vlm:end -->", text);
    }

    [Fact]
    public void WhenTheVlmTookTheFirstRowForTheHeader_ThatRowIsKept()
    {
        var document = Document(["| No | Item |\n|---|---|\n| 1 | a |", "| 2 | b |\n|---|---|\n| 3 | c |"], continuesOn: [2]);

        var text = Vlm(document);

        Assert.Contains("| 1 | a |\n| 2 | b |\n| 3 | c |\n<!-- vlm:end -->", text);
        Assert.DoesNotContain("|---|---|\n| 2 | b |", text);
    }

    [Fact]
    public void ATableWithAnotherNumberOfColumns_IsNotJoined()
    {
        var document = Document(["| No | Item |\n|---|---|\n| 1 | a |", "| x | y | z |\n|---|---|---|\n| 1 | 2 | 3 |"], continuesOn: [2]);

        var text = Vlm(document);

        Assert.DoesNotContain("結合しました。\n<!-- vlm:end -->\n<!-- usage", text.Replace("\r", string.Empty, StringComparison.Ordinal).Split("<!-- page: 2 -->")[0]);
        Assert.DoesNotContain(Note, text);
        Assert.Contains("| x | y | z |", text);
    }

    [Fact]
    public void WithoutALaterPartOfATableInTheTextOfStep1_TwoTablesStayTwoTables()
    {
        var document = Document(["| No | Item |\n|---|---|\n| 1 | a |", "| No | Item |\n|---|---|\n| 2 | b |"]);

        var text = Vlm(document);

        Assert.DoesNotContain(Note, text);
        Assert.Equal(2, text.Split("| No | Item |").Length - 1);
    }

    [Fact]
    public void AResultThatDoesNotStartWithATable_IsLeftAsItIs()
    {
        var document = Document(["| No | Item |\n|---|---|\n| 1 | a |", "A paragraph first.\n\n| 2 | b |"], continuesOn: [2]);

        var text = Vlm(document);

        Assert.DoesNotContain(Note, text);
        Assert.Contains("A paragraph first.", text);
    }

    [Fact]
    public void AFailedPageInTheMiddle_BreaksTheChain()
    {
        var document = Document(["| No | Item |\n|---|---|\n| 1 | a |", "ignored", "| 3 | c |"], continuesOn: [2, 3]);
        document.Pages[1].Step3 = PageStep3Status.Failed;
        document.Pages[1].VlmText = null;

        var text = Vlm(document);

        Assert.DoesNotContain(Note, text.Split("<!-- page: 3 -->")[1]);
    }
}
