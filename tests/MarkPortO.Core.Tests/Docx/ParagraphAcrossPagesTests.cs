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

namespace MarkPortO.Core.Tests.Docx;

/// <summary>FR-091: a paragraph that runs over a page break is cut there for the text of each page, and written as one paragraph again.</summary>
public class ParagraphAcrossPagesTests
{
    private const string Long = "The long paragraph starts near the bottom of page one and carries on over the page break to the top of page two where it ends.";

    private static DocxBlock Paragraph(string text, bool splittable = true) =>
        new(new Block { Kind = BlockKind.Paragraph, Markdown = text }, text) { Splittable = splittable };

    [Fact]
    public void Assign_AParagraphOverAPageBreak_IsCutIntoOnePartForEachPage()
    {
        DocxBlock[] blocks = [Paragraph("An opening line of the page one."), Paragraph(Long), Paragraph("A closing line of page two.")];

        // The second page begins with the text of a header.
        DocxPageAssigner.Assign(blocks, ["An opening line of the page one. The long paragraph starts near the bottom of page one and carries on over the page break", "HEADER to the top of page two where it ends. A closing line of page two."]);
        var expanded = DocxPageAssigner.ExpandTables(blocks);

        Assert.Equal(4, expanded.Count);
        Assert.Equal([1, 1, 2, 2], expanded.Select(b => b.Block.PageIndex).ToArray());
        Assert.Equal([null, null, 1, null], expanded.Select(b => b.Block.JoinedToPage).ToArray());
        Assert.Equal("The long paragraph starts near the bottom of page one and carries on over the page break", expanded[1].Block.Markdown);
        Assert.Equal("to the top of page two where it ends.", expanded[2].Block.Markdown);
        Assert.Equal(Long, expanded[1].Block.Markdown + " " + expanded[2].Block.Markdown); // nothing is lost, nothing is repeated
    }

    [Fact]
    public void Assign_AParagraphThatIsAllOnOnePage_IsNotCut()
    {
        DocxBlock[] blocks = [Paragraph(Long), Paragraph("Next paragraph of the second page.")];

        DocxPageAssigner.Assign(blocks, [Long + " ", "Next paragraph of the second page."]);

        Assert.Equal([1, 2], blocks.Select(b => b.Block.PageIndex).ToArray());
        Assert.Equal(2, DocxPageAssigner.ExpandTables(blocks).Count);
    }

    [Fact]
    public void Assign_AParagraphWithMarkup_IsNotCut()
    {
        DocxBlock[] blocks = [Paragraph(Long, splittable: false)];

        DocxPageAssigner.Assign(blocks, ["The long paragraph starts near the bottom of page one and carries on over the page break", "to the top of page two where it ends."]);

        Assert.Single(DocxPageAssigner.ExpandTables(blocks));
    }

    [Fact]
    public void Assign_AParagraphOverThreePages_IsCutIntoThreeParts()
    {
        var text = "Alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega.";
        DocxBlock[] blocks = [Paragraph(text)];

        DocxPageAssigner.Assign(blocks, ["Alpha beta gamma delta epsilon zeta eta theta", "iota kappa lambda mu nu xi omicron", "pi rho sigma tau upsilon phi chi psi omega."]);
        var expanded = DocxPageAssigner.ExpandTables(blocks);

        Assert.Equal([1, 2, 3], expanded.Select(b => b.Block.PageIndex).ToArray());
        Assert.Equal([null, 1, 1], expanded.Select(b => b.Block.JoinedToPage).ToArray());
        Assert.Equal(text, string.Join(" ", expanded.Select(b => b.Block.Markdown)));
    }

    [Fact]
    public void TheMarkdown_JoinsTheParts_AndTheLaterPageSaysWhereTheRestWent()
    {
        var document = Document(
            new Block { Kind = BlockKind.Paragraph, Markdown = "The long paragraph starts near the bottom", PageIndex = 1 },
            new Block { Kind = BlockKind.Paragraph, Markdown = "and carries on over the page break.", PageIndex = 2, JoinedToPage = 1 });

        var markdown = MarkdownRenderer.Render(document);

        Assert.Contains("The long paragraph starts near the bottom and carries on over the page break.", markdown);
        Assert.Contains("> この段落の続きは、ページ1の段落に結合しました。", markdown);
    }

    [Fact]
    public void TheMarkdown_OfJapaneseText_IsJoinedWithoutASpace()
    {
        var document = Document(
            new Block { Kind = BlockKind.Paragraph, Markdown = "日本語の文章は、ページの終わり", PageIndex = 1 },
            new Block { Kind = BlockKind.Paragraph, Markdown = "をまたいで続きます。", PageIndex = 2, JoinedToPage = 1 });

        Assert.Contains("日本語の文章は、ページの終わりをまたいで続きます。", MarkdownRenderer.Render(document));
    }

    [Fact]
    public void TheResultsOfTheVlm_AreJoined_TheRestIsKept_AndTheNoteStandsInItsPlace()
    {
        var document = Document(
            new Block { Kind = BlockKind.Paragraph, Markdown = "The long paragraph starts", PageIndex = 1 },
            new Block { Kind = BlockKind.Paragraph, Markdown = "and carries on.", PageIndex = 2, JoinedToPage = 1 });
        document.Pages[0].VlmText = "Heading\n\nThe long paragraph starts near the bottom of page one and";
        document.Pages[1].VlmText = "carries on over the page break to the top of page two.\n\nNext paragraph.";

        var vlm = MarkdownRenderer.RenderVlm(document);

        Assert.Contains("The long paragraph starts near the bottom of page one and carries on over the page break to the top of page two.\n<!-- vlm:end -->", vlm);
        Assert.Contains("<!-- vlm:begin page=2 -->\n> この段落の続きは、ページ1の段落に結合しました。\n\nNext paragraph.\n<!-- vlm:end -->", vlm);
    }

    [Theory]
    [InlineData("# A heading", "A paragraph.")]       // the page before ends with a heading
    [InlineData("A paragraph.", "- a list item")]       // the next page starts with a list
    [InlineData("A paragraph.", "| a | b |")]           // ... or a table
    public void TheResultsOfTheVlm_AreNotJoined_WhenOneSideIsNotAPlainParagraph(string before, string after)
    {
        Assert.False(ParagraphJoin.TryJoin(before, after, out _, out _));
    }

    [Fact]
    public void TheResultsOfTheVlm_AreNotJoined_WithoutAPartOfAParagraphInTheTextOfStep1()
    {
        var document = Document(new Block { Kind = BlockKind.Paragraph, Markdown = "text", PageIndex = 1 }, new Block { Kind = BlockKind.Paragraph, Markdown = "more", PageIndex = 2 });
        document.Pages[0].VlmText = "The paragraph of the page one";
        document.Pages[1].VlmText = "starts the page two";

        var vlm = MarkdownRenderer.RenderVlm(document);

        Assert.DoesNotContain("結合しました", vlm);
    }

    private static DocumentRecord Document(params Block[] blocks) => new()
    {
        MarkdownLanguage = "ja",
        DocId = "d0001",
        SourcePath = @"D:\in\a.docx",
        Kind = DocumentKind.Docx,
        OutputFolder = @"D:\out\a",
        Step1 = StepStatus.Done,
        Step2 = StepStatus.NotApplicable,
        Step3 = StepStatus.Done,
        Pages =
        [
            new PageRecord { Index = 1, SourceLabel = "page:1", ImageFile = "images/page-001.png", Step3 = PageStep3Status.Done, VlmText = "x" },
            new PageRecord { Index = 2, SourceLabel = "page:2", ImageFile = "images/page-002.png", Step3 = PageStep3Status.Done, VlmText = "y" },
        ],
        Blocks = [.. blocks],
    };
}
