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

using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pdf;
using MarkPortO.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace MarkPortO.Core.Tests.Docx;

public class PageAssignmentTests(ITestOutputHelper output)
{
    private static DocxBlock Para(string text) =>
        new(new Block { Kind = BlockKind.Paragraph, Markdown = text }, text);

    private static int?[] Pages(IEnumerable<DocxBlock> blocks) => [.. blocks.Select(b => b.Block.PageIndex)];

    // ---- Unit tests on synthetic PDF text (no engine needed) ----

    [Fact]
    public void Assign_PutsEachBlockOnThePageWhereItsTextStarts()
    {
        DocxBlock[] blocks = [Para("alpha beta gamma"), Para("delta epsilon"), Para("zeta eta theta")];

        var warnings = DocxPageAssigner.Assign(blocks, ["alpha beta gamma delta epsilon", "zeta eta theta"]);

        Assert.Equal([1, 1, 2], Pages(blocks));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Assign_AParagraphThatRunsOverToTheNextPage_BelongsToThePageItStartsOn()
    {
        var long1 = "The quick brown fox jumps over the lazy dog and keeps running far away";
        DocxBlock[] blocks = [Para(long1), Para("Next paragraph here")];

        // The page break falls in the middle of the first paragraph.
        DocxPageAssigner.Assign(blocks, ["The quick brown fox jumps over the lazy", "dog and keeps running far away Next paragraph here"]);

        Assert.Equal([1, 2], Pages(blocks));
    }

    [Fact]
    public void Assign_IgnoresFootersAndPageNumbersBetweenPages()
    {
        DocxBlock[] blocks = [Para("First paragraph of the report"), Para("Second paragraph of the report")];

        DocxPageAssigner.Assign(blocks, ["First paragraph of the report Page 1 of 2", "Page header Second paragraph of the report Page 2 of 2"]);

        Assert.Equal([1, 2], Pages(blocks));
    }

    [Fact]
    public void Assign_ABlockThatIsNotInThePdf_GetsNoPageAndAWarning_AndTheOthersStillResolve()
    {
        DocxBlock[] blocks = [Para("alpha beta gamma"), Para("text that is not in the pdf at all"), Para("zeta eta theta")];

        var warnings = DocxPageAssigner.Assign(blocks, ["alpha beta gamma", "zeta eta theta"]);

        Assert.Equal([1, null, 2], Pages(blocks));
        var warning = Assert.Single(warnings);
        Assert.Equal(DocxPageAssigner.UnresolvedCode, warning.Code);
        Assert.Contains("text that is not in the pdf", warning.Message);
    }

    [Fact]
    public void Assign_RepeatedTextOnDifferentPages_IsMatchedInOrder()
    {
        DocxBlock[] blocks = [Para("Total: 100 yen"), Para("middle text one"), Para("Total: 100 yen"), Para("final text two"), Para("Total: 100 yen")];

        DocxPageAssigner.Assign(blocks, ["Total: 100 yen middle text one", "Total: 100 yen", "final text two Total: 100 yen"]);

        Assert.Equal([1, 1, 2, 3, 3], Pages(blocks));
    }

    [Fact]
    public void Assign_ComparesWithoutWhiteSpaceAndWithFullWidthFolded()
    {
        DocxBlock[] blocks = [Para("ＡＢＣ　１２３ 日本語")];

        DocxPageAssigner.Assign(blocks, ["x", "ABC123\n日本語 more"]);

        Assert.Equal([2], Pages(blocks));
    }

    [Fact]
    public void Assign_ASearchStartingInsideALongPreviousBlock_DoesNotMatchItsTail()
    {
        // The table's first cell "Name" also occurs in the tail of the long paragraph before it on page 1.
        var paragraph = "Please write your Name and address in the form that is attached to this letter";
        DocxBlock[] blocks = [Para(paragraph), new(new Block { Kind = BlockKind.Table }, "Name"), Para("Closing words on page two")];

        DocxPageAssigner.Assign(blocks, [paragraph + " ", "Name Value Alpha 1 Closing words on page two"]);

        Assert.Equal([1, 2, 2], Pages(blocks));
    }

    [Fact]
    public void Assign_VeryShortBlocks_AreLeftWithoutAPageAndWithoutNoise()
    {
        DocxBlock[] blocks = [Para("1"), Para("a long enough paragraph")];

        var warnings = DocxPageAssigner.Assign(blocks, ["1 a long enough paragraph"]);

        Assert.Equal([null, 1], Pages(blocks));
        Assert.Empty(warnings);
    }

    [Fact]
    public void EffectivePages_BlocksWithoutAPageStayWithTheBlockBeforeThem()
    {
        List<Block> blocks =
        [
            new() { PageIndex = null },
            new() { PageIndex = 1 },
            new() { PageIndex = null },
            new() { PageIndex = 3 },
            new() { PageIndex = null },
        ];

        Assert.Equal([1, 1, 1, 3, 3], DocxPageAssigner.EffectivePages(blocks, 3));
    }

    [Fact]
    public void EffectivePages_NeverGoesBeyondThePageCount()
    {
        Assert.Equal([2], DocxPageAssigner.EffectivePages([new Block { PageIndex = 9 }], 2));
    }

    // ---- Spike S1: accuracy on documents laid out by a real engine (LibreOffice) ----

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task S1_ExplicitPageBreaks_AreAssignedWithoutAnyError()
    {
        using var dir = new TempDir();
        string[][] pages =
        [
            ["Chapter one begins here", "First chapter second paragraph"],
            ["Chapter two begins here", "Second chapter second paragraph", "Second chapter third paragraph"],
            ["Chapter three begins here"],
            ["Chapter four begins here", "Fourth chapter second paragraph"],
        ];
        var source = dir.Combine("paged.docx");
        DocxFactory.CreatePaged(source, pages);
        var blocks = DocxReader.Read(source);
        var pdfTexts = await ExportAsync(dir, source);

        var warnings = DocxPageAssigner.Assign(blocks, pdfTexts);

        var expected = pages.SelectMany((paragraphs, page) => paragraphs.Select(_ => (int?)(page + 1))).ToArray();
        Assert.Equal(pages.Length, pdfTexts.Count);
        Assert.Equal(expected, Pages(blocks));
        Assert.Empty(warnings);
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task S1_NaturalFlow_MatchesThePageWhereEachParagraphTokenReallyIs()
    {
        using var dir = new TempDir();
        var source = dir.Combine("flow.docx");
        DocxFactory.CreateFlow(source, paragraphs: 120, wordsPerParagraph: 60);
        var blocks = DocxReader.Read(source);
        var pdfTexts = await ExportAsync(dir, source);

        var warnings = DocxPageAssigner.Assign(blocks, pdfTexts);

        // Ground truth: paragraph n starts with the token Pnnnn; the page that holds that token is its page.
        var correct = 0;
        for (var n = 1; n <= blocks.Count; n++)
        {
            var token = $"P{n:0000}";
            var truePage = Enumerable.Range(0, pdfTexts.Count).First(p => pdfTexts[p].Contains(token, StringComparison.Ordinal)) + 1;
            if (blocks[n - 1].Block.PageIndex == truePage)
            {
                correct++;
            }
        }

        var rate = 100.0 * correct / blocks.Count;
        output.WriteLine($"S1: {blocks.Count} paragraphs over {pdfTexts.Count} pages: {correct} correct ({rate:0.0}%), {warnings.Count} warnings");
        Assert.True(pdfTexts.Count >= 4, "the document should span several pages");
        Assert.True(rate >= 99.0, $"page assignment accuracy {rate:0.0}% is below 99% (SC-017)");
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task S1_TableAndHeadings_AreAssignedToThePageOfTheirFirstText()
    {
        using var dir = new TempDir();
        var source = dir.Combine("structured.docx");
        DocxFactory.CreateStructured(source);
        var blocks = DocxReader.Read(source);
        var pdfTexts = await ExportAsync(dir, source);

        var warnings = DocxPageAssigner.Assign(blocks, pdfTexts);

        Assert.Single(pdfTexts);
        Assert.All(blocks, b => Assert.Equal(1, b.Block.PageIndex));
        Assert.Empty(warnings);
    }

    private static async Task<IReadOnlyList<string>> ExportAsync(TempDir dir, string source)
    {
        using var engine = new LibreOfficeEngine(profileRoot: dir.Combine("profiles"));
        var pdf = Path.ChangeExtension(source, ".pdf");
        await engine.ExportPdfAsync(new PdfExportRequest(source, DocumentKind.Docx, pdf, PdfExportRequest.DefaultTimeout), CancellationToken.None);
        return PdfTextExtractor.GetPageTexts(pdf);
    }
}
