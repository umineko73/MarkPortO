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

/// <summary>FR-105: where the text of a page has a table, the tables of the VLM result are left out and the table of Step1 is the one to use.</summary>
public class Step1TablesTests
{
    private const string Note = "> このページの表は、ここには書きません。文書の抽出テキストの表を使ってください。";

    // pages: the result of the VLM of each page. withTable: the pages whose text (Step1) has a table.
    private static DocumentRecord Document(bool step1Tables, string[] pages, params int[] withTable)
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
            Step1Tables = step1Tables,
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
        document.Blocks = [.. withTable.Select(page => new Block { Kind = BlockKind.Table, Markdown = "| a |\n|---|\n| b |", PageIndex = page })];
        return document;
    }

    [Fact]
    public void ATableOfTheVlm_IsLeftOut_OnAPageWhoseTextHasATable()
    {
        var text = MarkdownRenderer.RenderVlm(Document(true, ["Intro\n\n| No | Item |\n|---|---|\n| 1 | a |\n\nThe end."], 1));

        Assert.Contains("<!-- vlm:begin page=1 -->\nIntro\n\n" + Note + "\n\nThe end.\n<!-- vlm:end -->", text);
        Assert.DoesNotContain("| 1 | a |", text);
    }

    [Fact]
    public void ATableOfTheVlm_IsKept_OnAPageWhoseTextHasNoTable()
    {
        var text = MarkdownRenderer.RenderVlm(Document(true, ["| No | Item |\n|---|---|\n| 1 | a |", "| No | Item |\n|---|---|\n| 2 | b |"], 1));

        Assert.Contains("| 2 | b |", text);
        Assert.DoesNotContain("| 1 | a |", text);
    }

    [Fact]
    public void NothingChanges_WhenTheOptionIsOff()
    {
        var text = MarkdownRenderer.RenderVlm(Document(false, ["| No | Item |\n|---|---|\n| 1 | a |"], 1));

        Assert.Contains("| 1 | a |", text);
        Assert.DoesNotContain(Note, text);
    }

    [Fact]
    public void ATableThatRunsOverPages_IsLeftOutOnEveryPage_AndNotJoined()
    {
        var text = MarkdownRenderer.RenderVlm(Document(true, ["| No | Item |\n|---|---|\n| 1 | a |", "| No | Item |\n|---|---|\n| 2 | b |\n\nThe end."], 1, 2));

        Assert.Equal(2, text.Split(Note).Length - 1);
        Assert.Contains("The end.", text);
        Assert.DoesNotContain("joined to the table", text);
        Assert.DoesNotContain("結合しました", text);
    }

    [Fact]
    public void ARowWithoutTheFirstPipe_IsAlsoATable_WhenTheSeparatorRowIsThere()
    {
        Assert.Equal("Intro\n\nN\n\nEnd", Step1Tables.Replace("Intro\n\nNo | Item\n---|---\n1 | a\n\nEnd", "N"));
    }

    [Fact]
    public void ASingleLineWithAPipe_IsNotATable()
    {
        const string text = "Use a | b to choose.";

        Assert.Equal(text, Step1Tables.Replace(text, "N"));
    }
}
