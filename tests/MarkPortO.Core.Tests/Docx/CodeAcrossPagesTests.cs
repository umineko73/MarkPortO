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
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Docx;

/// <summary>A code block that runs over a page break: cut for the text of each page, joined again in the Markdown, and read as one by the VLM.</summary>
public class CodeAcrossPagesTests
{
    private static readonly string[] Lines =
    [
        "specs/001-office-to-markdown/",
        "├── plan.md              # この文書",
        "├── research.md          # Part 0: 技術選定",
        "├── contracts/           # Part 1: CLI",
        "│   ├── cli.md",
        "│   └── vlm-openai-compat.md",
        "└── tasks.md             # Part 2: /speckit-tasks の出力",
    ];

    private static DocxBlock Code() =>
        new(new Block { Kind = BlockKind.Paragraph, Markdown = CodeText.Block(Lines) }, string.Join(' ', Lines.Select(l => l.Trim()))) { CodeLines = Lines };

    private static DocxBlock Paragraph(string text) =>
        new(new Block { Kind = BlockKind.Paragraph, Markdown = text }, text) { Splittable = true };

    private static List<DocxBlock> Placed()
    {
        DocxBlock[] blocks = [Paragraph("Documentation of this feature"), Code(), Paragraph("Source Code (repository root)")];
        DocxPageAssigner.Assign(blocks,
        [
            "Documentation of this feature " + string.Join(" ", Lines[..4]),
            string.Join(" ", Lines[4..]) + " Source Code (repository root)",
        ]);
        return DocxPageAssigner.ExpandTables(blocks);
    }

    [Fact]
    public void ACodeBlockOverAPageBreak_IsCutIntoOneCodeBlockForEachPage()
    {
        var placed = Placed();

        Assert.Equal(4, placed.Count);
        Assert.Equal([1, 1, 2, 2], placed.Select(b => b.Block.PageIndex).ToArray());
        Assert.Equal([null, null, 1, null], placed.Select(b => b.Block.JoinedToPage).ToArray());
        Assert.Equal(CodeText.Block(Lines[..4]), placed[1].Block.Markdown);
        Assert.Equal(CodeText.Block(Lines[4..]), placed[2].Block.Markdown);
    }

    [Fact]
    public void TheMarkdownOfTheDocument_HasOneCodeBlock_WhereItStarts()
    {
        var document = Document(Placed());

        var markdown = MarkdownRenderer.Render(document);

        Assert.Contains(CodeText.Block(Lines), markdown);
        Assert.Contains("このコードの続きは、ページ1のコードブロックに結合しました。", markdown);
    }

    [Fact]
    public void ThePageWhereItStarts_GetsTheWholeCodeBlock_AndTheNextPageIsToldItIsWritten()
    {
        var document = Document(Placed());

        var first = Step3Worker.PageTextOf(document, document.Pages[0], maxLines: 100);
        var second = Step3Worker.PageTextOf(document, document.Pages[1], maxLines: 100);

        Assert.Contains(CodeText.Block(Lines), first.Text);
        Assert.True(first.Table!.CarriesCode);
        Assert.DoesNotContain("cli.md", second.Text);
        Assert.Contains("Source Code (repository root)", second.Text);
        Assert.Equal(1, second.Table!.CodeWrittenBeforeFromPage);

        var prompt = PromptSupplement.Build("p\n\n抽出テキスト:\n{pagetext}", "ja", pageText: second.Text, table: second.Table);
        Assert.Contains("ページ1のコードブロックの続き", prompt);
    }

    [Fact]
    public void TheLineThatTheVlmCopiedFromTheText_IsTakenOutOfItsAnswer()
    {
        var document = Document(Placed());
        var second = Step3Worker.PageTextOf(document, document.Pages[1], maxLines: 100);

        // A model copied the line of the text into its answer, as it was or without the quote mark.
        var answer = "> このコードの続きは、ページ1のコードブロックに含めて書きました。\n\nこのコードの続きは、ページ1のコードブロックに含めて書きました。\n\n#### Source Code (repository root)";

        Assert.Equal("#### Source Code (repository root)", Step3Worker.WithoutNotes(answer, second.Notes));
    }

    [Fact]
    public void ACodeBlockThatTheVlmWroteAgainOnTheNextPage_IsTakenOff()
    {
        var document = Document(Placed());
        document.Pages[0].Step3 = PageStep3Status.Done;
        document.Pages[0].ContinuedCodeSent = true;
        document.Pages[0].VlmText = "Documentation of this feature\n\n" + CodeText.Block(Lines);
        document.Pages[1].Step3 = PageStep3Status.Done;
        document.Pages[1].CodeWrittenBefore = true;
        document.Pages[1].VlmText = CodeText.Block(Lines[4..]) + "\n\nSource Code (repository root)";

        var changed = VlmTableJoiner.Join(document, DocxPageAssigner.EffectivePages(document.Blocks, 2), "ja");

        Assert.StartsWith("> このコードの続きは", changed[2]);
        Assert.EndsWith("Source Code (repository root)", changed[2]);
        Assert.DoesNotContain("cli.md", changed[2]);
    }

    private static DocumentRecord Document(List<DocxBlock> placed) => new()
    {
        MarkdownLanguage = "ja",
        DocId = "d0001",
        SourcePath = @"D:\in\a.docx",
        Kind = DocumentKind.Docx,
        OutputFolder = @"D:\out\a",
        Step1 = StepStatus.Done,
        Step2 = StepStatus.NotApplicable,
        Step3 = StepStatus.NotApplicable,
        Pages =
        [
            new PageRecord { Index = 1, SourceLabel = "page:1" },
            new PageRecord { Index = 2, SourceLabel = "page:2" },
        ],
        Blocks = [.. placed.Select(b => b.Block)],
    };
}
