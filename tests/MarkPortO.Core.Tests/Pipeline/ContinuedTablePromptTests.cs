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
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Pipeline;

/// <summary>A table that runs over pages, with the tables of the VLM: the first page is given the rows of the later pages, and a later page is told they are written.</summary>
public class ContinuedTablePromptTests
{
    private const string First = "| 番号 | 名前 |\n|---|---|\n| 1 | A |\n| 2 | B |";
    private const string Second = "| | |\n|---|---|\n| 3 | C |\n| 4 | D |";

    private static DocumentRecord Document(Step1TablesMode mode = Step1TablesMode.Off) => new()
    {
        MarkdownLanguage = "ja",
        DocId = "d0001",
        SourcePath = @"D:\in\a.docx",
        Kind = DocumentKind.Docx,
        OutputFolder = @"D:\out\a",
        Step1Tables = mode,
        Pages =
        [
            new PageRecord { Index = 1, SourceLabel = "page:1" },
            new PageRecord { Index = 2, SourceLabel = "page:2" },
        ],
        Blocks =
        [
            new Block { Kind = BlockKind.Paragraph, Markdown = "Title", PageIndex = 1 },
            new Block { Kind = BlockKind.Table, Markdown = First, PageIndex = 1 },
            new Block { Kind = BlockKind.Table, Markdown = Second, PageIndex = 2, JoinedToPage = 1 },
        ],
    };

    [Fact]
    public void TheFirstPage_GetsTheRowsOfTheNextPage_AfterItsOwn()
    {
        var result = Step3Worker.PageTextOf(Document(), Document().Pages[0], maxLines: 100);

        Assert.Contains(First + "\n| 3 | C |\n| 4 | D |", result.Text);
        Assert.DoesNotContain("| | |", result.Text);
        Assert.Equal(new PromptSupplement.TableContinuation(2, 2, null), result.Table);
    }

    [Fact]
    public void TheNextPage_IsToldThatItsRowsAreWritten_AndHasNoRowsInItsText()
    {
        var document = Document();

        var result = Step3Worker.PageTextOf(document, document.Pages[1], maxLines: 100);

        Assert.DoesNotContain("| 3 | C |", result.Text);
        Assert.Contains("ページ1", result.Text);
        Assert.Equal(new PromptSupplement.TableContinuation(0, 0, 1), result.Table);
    }

    [Fact]
    public void WithTheTablesOfStep1_NothingChanges()
    {
        var document = Document(Step1TablesMode.Replace);

        var first = Step3Worker.PageTextOf(document, document.Pages[0], maxLines: 100);
        var second = Step3Worker.PageTextOf(document, document.Pages[1], maxLines: 100);

        Assert.Null(first.Table);
        Assert.Null(second.Table);
        Assert.Contains("| 3 | C |", second.Text);
    }

    [Fact]
    public void WhenTheFirstPageIsNotRead_TheNextPageIsNotToldThatItsRowsAreWritten()
    {
        var document = Document();
        document.Pages[0].IsOutOfRange = true;

        var second = Step3Worker.PageTextOf(document, document.Pages[1], maxLines: 100);

        Assert.Null(second.Table);
        Assert.Contains("| 3 | C |", second.Text);
    }

    [Fact]
    public void TheSupplement_SaysHowManyRowsAreInTheImage_AndDropsTheHeaderRule()
    {
        var text = PromptSupplement.Build("p", "ja", table: new PromptSupplement.TableContinuation(2, 5, null));

        Assert.Contains("最初の2行", text);
        Assert.Contains("5行", text);
        Assert.DoesNotContain("見出し行を作らず", text);
    }

    [Fact]
    public void RowsThatTheVlmWroteAgainOnTheNextPage_AreTakenOff()
    {
        var document = Document();
        document.Pages[0].Step3 = PageStep3Status.Done;
        document.Pages[0].ContinuedRowsSent = 2;
        document.Pages[0].VlmText = First + "\n| 3 | C |\n| 4 | D |";
        document.Pages[1].Step3 = PageStep3Status.Done;
        document.Pages[1].RowsWrittenBefore = true;
        document.Pages[1].VlmText = "| 3 | C |\n| 4 | D |\n\nAfter the table.";

        var changed = VlmTableJoiner.Join(document, [1, 1, 2], "ja");

        Assert.Equal(First + "\n| 3 | C |\n| 4 | D |", changed[1]);
        Assert.Contains("After the table.", changed[2]);
        Assert.DoesNotContain("| 3 | C |", changed[2]);
    }

    [Fact]
    public void WhenTheVlmLeftTheRowsOut_TheRowsOfTheNextPageAreJoinedAsUsual()
    {
        var document = Document();
        document.Pages[0].Step3 = PageStep3Status.Done;
        document.Pages[0].ContinuedRowsSent = 2;
        document.Pages[0].VlmText = First;
        document.Pages[1].Step3 = PageStep3Status.Done;
        document.Pages[1].RowsWrittenBefore = true;
        document.Pages[1].VlmText = "| 3 | C |\n| 4 | D |";

        var changed = VlmTableJoiner.Join(document, [1, 1, 2], "ja");

        Assert.Equal(First + "\n| 3 | C |\n| 4 | D |", changed[1]);
    }
}
