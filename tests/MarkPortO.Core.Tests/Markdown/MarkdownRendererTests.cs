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
using MarkPortO.Core.Logging;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Markdown;

public class MarkdownRendererTests
{
    private static DocumentRecord Docx(StepStatus step2 = StepStatus.Done, StepStatus step3 = StepStatus.NotApplicable)
    {
        var doc = new DocumentRecord
        {
            MarkdownLanguage = "ja",
            DocId = "d0001",
            SourcePath = @"D:\in\報告書.docx",
            Kind = DocumentKind.Docx,
            OutputFolder = @"D:\out\報告書",
            Step1 = StepStatus.Done,
            Step2 = step2,
            Step3 = step3,
        };
        doc.Pages =
        [
            new PageRecord { Index = 1, SourceLabel = "1ページ目", ImageFile = "images/page-001.png" },
            new PageRecord { Index = 2, SourceLabel = "2ページ目", ImageFile = "images/page-002.png" },
        ];
        doc.Blocks =
        [
            new Block { Kind = BlockKind.Heading, Markdown = "# Title", PageIndex = 1 },
            new Block { Kind = BlockKind.Paragraph, Markdown = "First page text.", PageIndex = 1 },
            new Block { Kind = BlockKind.Paragraph, Markdown = "Second page text.", PageIndex = 2 },
        ];
        return doc;
    }

    [Fact]
    public void Render_StartsWithTheFileNameAndTheStructureNote()
    {
        var markdown = MarkdownRenderer.Render(Docx());

        Assert.StartsWith("# 報告書.docx\n\n> **このMarkdownの構造**", markdown);
        Assert.Contains("> - 元のファイル: `報告書.docx`", markdown);
        Assert.Contains("> - ページの区切り: `<!-- page: N -->`", markdown);
        Assert.Contains("> - ページ画像（Step2）: `![ページ N](images/page-NNN.png)`", markdown);
        Assert.Contains("> - 削除ページ: `<!-- deleted page=N -->` の目印のあとの、`> **[削除ページ]**` で始まる引用。", markdown);
    }

    [Fact]
    public void Render_PutsEachPagesImageThenItsText_InOrder()
    {
        var markdown = MarkdownRenderer.Render(Docx());

        var page1 = markdown.IndexOf("<!-- page: 1 -->", StringComparison.Ordinal);
        var image1 = markdown.IndexOf("![ページ 1](images/page-001.png)", StringComparison.Ordinal);
        var text1 = markdown.IndexOf("First page text.", StringComparison.Ordinal);
        var page2 = markdown.IndexOf("<!-- page: 2 -->", StringComparison.Ordinal);
        var image2 = markdown.IndexOf("![ページ 2](images/page-002.png)", StringComparison.Ordinal);
        var text2 = markdown.IndexOf("Second page text.", StringComparison.Ordinal);

        Assert.True(page1 < image1 && image1 < text1 && text1 < page2 && page2 < image2 && image2 < text2);
        Assert.Equal(1, CountOf(markdown, "![ページ 1]"));
    }

    [Fact]
    public void Render_WithoutStep2_HasNoImageReferences_ButSaysWhereTheImagesAre()
    {
        var markdown = MarkdownRenderer.Render(Docx(step2: StepStatus.NotApplicable));

        Assert.DoesNotContain("![ページ", markdown.Replace("`![ページ N]", string.Empty, StringComparison.Ordinal));
        Assert.Contains("> - ページ画像（Step2）: このMarkdownには含まれていません。画像ファイルは `images/` に保存されています。", markdown);
        Assert.Contains("<!-- page: 1 -->", markdown);
        Assert.Contains("First page text.", markdown);
    }

    [Fact]
    public void Render_WithoutStep3_SaysTheAiResultIsNotIncluded_AndHasNoVlmBlocks()
    {
        var markdown = MarkdownRenderer.Render(Docx());

        Assert.Contains("> - VLM読み取り結果（Step3）: このMarkdownには含まれていません。", markdown);
        Assert.DoesNotContain("<!-- vlm:begin", markdown);
        Assert.DoesNotContain("VLM使用量", markdown);
    }

    private static DocumentRecord WithVlm(bool separate)
    {
        var doc = Docx(step3: StepStatus.Done);
        doc.VlmSeparate = separate;
        doc.Pages[0].Step3 = PageStep3Status.Done;
        doc.Pages[0].VlmText = "READ-ONE";
        doc.Pages[0].Usage = new UsageInfo(10, 5, 15);
        doc.Pages[1].IsDeleted = true;
        return doc;
    }

    [Fact]
    public void Render_WithAnotherFile_HasNoVlmTextAndNamesTheFile_AndTheVlmFileHasOnlyTheResults()
    {
        var doc = WithVlm(separate: true);

        var main = MarkdownRenderer.Render(doc);
        var vlm = MarkdownRenderer.RenderVlm(doc);

        Assert.DoesNotContain("READ-ONE", main, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- vlm:", main, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- usage", main, StringComparison.Ordinal);
        Assert.Contains("報告書_vlm.md", main, StringComparison.Ordinal);
        Assert.Contains("<!-- page: 1 -->", main, StringComparison.Ordinal);
        Assert.Contains("First page text.", main, StringComparison.Ordinal);

        Assert.Contains("<!-- page: 1 -->", vlm, StringComparison.Ordinal);
        Assert.Contains("<!-- vlm:begin page=1 -->\nREAD-ONE\n<!-- vlm:end -->", vlm, StringComparison.Ordinal);
        Assert.Contains("<!-- usage page=1 prompt=10 completion=5 total=15 -->", vlm, StringComparison.Ordinal);
        Assert.Contains("<!-- deleted page=2 -->", vlm, StringComparison.Ordinal);
        Assert.Contains("報告書.md", vlm, StringComparison.Ordinal);
        Assert.DoesNotContain("First page text.", vlm, StringComparison.Ordinal);
        Assert.DoesNotContain("![", vlm, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithoutAnotherFile_KeepsTheResultsInTheMarkdown()
    {
        var main = MarkdownRenderer.Render(WithVlm(separate: false));

        Assert.Contains("READ-ONE", main, StringComparison.Ordinal);
        Assert.DoesNotContain("_vlm.md", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_MakesTheVlmFileOnlyWhenAsked_AndRemovesItWhenNotAnymore()
    {
        using var dir = new TempDir();
        var doc = WithVlm(separate: true);
        doc.OutputFolder = dir.Combine("out");
        Directory.CreateDirectory(doc.OutputFolder);
        var vlmPath = Path.Combine(doc.OutputFolder, "報告書_vlm.md");

        MarkdownRenderer.Write(doc);
        Assert.True(File.Exists(Path.Combine(doc.OutputFolder, "報告書.md")));
        Assert.True(File.Exists(vlmPath));

        doc.VlmSeparate = false;
        MarkdownRenderer.Write(doc);
        Assert.False(File.Exists(vlmPath));

        doc.VlmSeparate = true;
        doc.Step3 = StepStatus.NotApplicable;
        MarkdownRenderer.Write(doc);
        Assert.False(File.Exists(vlmPath));
    }

    [Fact]
    public void Render_AMergedCellMark_IsWrittenInTheLanguageOfTheJob_AndExplainedInTheNote()
    {
        var doc = Docx();
        doc.Blocks[1] = new Block { Kind = BlockKind.Table, Markdown = MarkdownText.Table([["a", "b"], ["c", MarkdownText.MergedCell]]), PageIndex = 1 };

        var ja = MarkdownRenderer.Render(doc);
        doc.MarkdownLanguage = "en";
        var en = MarkdownRenderer.Render(doc);

        Assert.Contains("| c | [結合] |", ja);
        Assert.Contains("> - 表の結合セル: `[結合]`", ja);
        Assert.Contains("| c | [merged] |", en);
        Assert.Contains("> - Merged table cells: `[merged]`", en);
        Assert.DoesNotContain(MarkdownText.MergedCell, ja + en, StringComparison.Ordinal);
        Assert.DoesNotContain("表の結合セル", MarkdownRenderer.Render(Docx()), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DeletedPage_HasOnlyTheNotice_WithNoTextNoImageReferenceAndNoVlmResult()
    {
        var doc = Docx(step3: StepStatus.Done);
        doc.Pages[1].IsDeleted = true;
        doc.Pages[1].Step3 = PageStep3Status.Skipped;
        doc.Blocks.Single(b => b.PageIndex == 2).Markdown = "Text of the deleted page";

        var markdown = MarkdownRenderer.Render(doc);

        var deleted = markdown[markdown.IndexOf("<!-- page: 2 -->", StringComparison.Ordinal)..];
        Assert.Contains("> **[削除ページ]** 元の文書の2ページ目は、全面に×印が付けられていたため除外しました。画像は `images/page-002.png` に保存しています。", deleted);
        Assert.DoesNotContain("Text of the deleted page", markdown);
        Assert.DoesNotContain("![ページ 2]", markdown);
        Assert.DoesNotContain("vlm:begin page=2", markdown);
    }

    [Fact]
    public void Render_DeletedPage_IsWrittenEvenWhenStep2IsOff()
    {
        var doc = Docx(step2: StepStatus.NotApplicable);
        doc.Pages[0].IsDeleted = true;

        var markdown = MarkdownRenderer.Render(doc);

        Assert.Contains("> **[削除ページ]** 元の文書の1ページ目は", markdown);
        Assert.DoesNotContain("First page text.", markdown);
    }

    [Fact]
    public void Render_Xlsx_WritesASheetMarkerOnEveryPage_AndNamesSplitParts()
    {
        var doc = new DocumentRecord
        {
            MarkdownLanguage = "ja",
            SourcePath = @"D:\in\book.xlsx",
            Kind = DocumentKind.Xlsx,
            Step2 = StepStatus.Done,
            Step3 = StepStatus.NotApplicable,
            Pages =
            [
                new PageRecord { Index = 1, ImageFile = "images/page-001.png", Sheet = new SheetInfo("売上", "A:Z", 1, 2) },
                new PageRecord { Index = 2, ImageFile = "images/page-002.png", Sheet = new SheetInfo("売上", "AA:AZ", 2, 2) },
                new PageRecord { Index = 3, ImageFile = "images/page-003.png", Sheet = new SheetInfo("メモ", "A:B", 1, 1) },
            ],
            Blocks =
            [
                new Block { Kind = BlockKind.SheetTable, Markdown = "| a |\n|---|\n| 1 |", PageIndex = 1 },
                new Block { Kind = BlockKind.SheetTable, Markdown = "| b |\n|---|\n| 2 |", PageIndex = 2 },
                new Block { Kind = BlockKind.SheetTable, Markdown = "| m |\n|---|\n| 3 |", PageIndex = 3 },
            ],
        };

        var markdown = MarkdownRenderer.Render(doc);

        Assert.Contains("<!-- sheet: 売上 part=1/2 columns=A:Z -->", markdown);
        Assert.Contains("<!-- sheet: 売上 part=2/2 columns=AA:AZ -->", markdown);
        Assert.Contains("<!-- sheet: メモ part=1/1 columns=A:B -->", markdown);
        Assert.Contains("> - シート（xlsx）:", markdown);
    }

    [Fact]
    public void Render_Step3_PutsTheAiResultAndUsageAtTheEndOfThePage()
    {
        var doc = Docx(step3: StepStatus.Done);
        doc.Pages[0].Step3 = PageStep3Status.Done;
        doc.Pages[0].VlmText = "## Read by the VLM\n\n- item";
        doc.Pages[0].Usage = new UsageInfo(820, 240, 1060);
        doc.Pages[1].Step3 = PageStep3Status.Done;
        doc.Pages[1].VlmText = "second";
        doc.Pages[1].Usage = UsageInfo.Unavailable;

        var markdown = MarkdownRenderer.Render(doc);

        Assert.Contains("<!-- vlm:begin page=1 -->\n## Read by the VLM\n\n- item\n<!-- vlm:end -->\n<!-- usage page=1 prompt=820 completion=240 total=1060 -->", markdown);
        Assert.Contains("<!-- usage page=2 unavailable -->", markdown);
        Assert.True(
            markdown.IndexOf("First page text.", StringComparison.Ordinal) < markdown.IndexOf("<!-- vlm:begin page=1 -->", StringComparison.Ordinal),
            "the VLM result comes after the text of the page");
    }

    [Fact]
    public void Render_Step3_ShowsTheTotalUsageNearTheTop_AndSumsOnlyWhatIsKnown()
    {
        var doc = Docx(step3: StepStatus.Done);
        foreach (var page in doc.Pages)
        {
            page.Step3 = PageStep3Status.Done;
            page.VlmText = "x";
        }

        doc.Pages[0].Usage = new UsageInfo(100, 10, 110);
        doc.Pages[1].Usage = new UsageInfo(200, 20, 220);

        var markdown = MarkdownRenderer.Render(doc);

        Assert.Contains("> **VLM使用量**: 合計 prompt=300 completion=30 total=330（ページ数: 2、取得できたページ: 2）", markdown);
        Assert.True(markdown.IndexOf("VLM使用量", StringComparison.Ordinal) < markdown.IndexOf("<!-- page: 1 -->", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_Step3_WhenNoPageHasUsage_SaysItIsUnavailable()
    {
        var doc = Docx(step3: StepStatus.Done);
        foreach (var page in doc.Pages)
        {
            page.Step3 = PageStep3Status.Done;
            page.VlmText = "x";
            page.Usage = UsageInfo.Unavailable;
        }

        Assert.Contains("> **VLM使用量**: **取得不可**（ページ数: 2、取得できたページ: 0）", MarkdownRenderer.Render(doc));
    }

    [Fact]
    public void Render_Step3_FailedPage_GetsAFailedMarker_WithoutTheApiKey()
    {
        SecretRegistry.Shared.Register("sk-render-secret-123");
        var doc = Docx(step3: StepStatus.Done);
        doc.Pages[0].Step3 = PageStep3Status.Failed;
        doc.Pages[0].Error = "HTTP 401 for key sk-render-secret-123\n--> injected";

        var markdown = MarkdownRenderer.Render(doc);

        Assert.DoesNotContain("sk-render-secret-123", markdown);
        var marker = markdown.Split('\n').Single(l => l.StartsWith("<!-- vlm:failed page=1", StringComparison.Ordinal));
        Assert.EndsWith("\" -->", marker);
        Assert.Equal(1, CountOf(marker, "-->")); // a "-->" in the reason cannot close the comment early
    }

    [Fact]
    public void Render_VlmResultCannotInjectMarkPortOMarkers()
    {
        var doc = Docx(step3: StepStatus.Done);
        doc.Pages[0].Step3 = PageStep3Status.Done;
        doc.Pages[0].VlmText = "text <!-- page: 99 --> more";
        doc.Pages[0].Usage = UsageInfo.Unavailable;

        var markdown = MarkdownRenderer.Render(doc);

        Assert.DoesNotContain("<!-- page: 99 -->", markdown);
        Assert.Contains("&lt;!-- page: 99 -->", markdown);
    }

    [Fact]
    public void Render_ABlockWithoutAPage_StaysWithTheBlockBeforeIt()
    {
        var doc = Docx();
        doc.Blocks.Insert(2, new Block { Kind = BlockKind.Paragraph, Markdown = "Unplaced paragraph.", PageIndex = null });

        var markdown = MarkdownRenderer.Render(doc);

        var unplaced = markdown.IndexOf("Unplaced paragraph.", StringComparison.Ordinal);
        Assert.True(unplaced > markdown.IndexOf("First page text.", StringComparison.Ordinal));
        Assert.True(unplaced < markdown.IndexOf("<!-- page: 2 -->", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ADocumentWithoutPages_HasOnlyTheHeader()
    {
        var doc = Docx();
        doc.Pages = [];
        doc.Blocks = [];

        var markdown = MarkdownRenderer.Render(doc);

        Assert.DoesNotContain("<!-- page:", markdown.Replace("`<!-- page: N -->`", string.Empty, StringComparison.Ordinal));
        Assert.StartsWith("# 報告書.docx", markdown);
    }

    [Fact]
    public void Render_UsesUnixLineEndings_AndEndsWithOneNewline()
    {
        var markdown = MarkdownRenderer.Render(Docx());

        Assert.DoesNotContain('\r', markdown);
        Assert.EndsWith("\n", markdown);
        Assert.False(markdown.EndsWith("\n\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_IsTheSameEveryTime()
    {
        Assert.Equal(MarkdownRenderer.Render(Docx()), MarkdownRenderer.Render(Docx()));
    }

    [Theory]
    [InlineData(1, 5, "images/page-001.png")]
    [InlineData(12, 999, "images/page-012.png")]
    [InlineData(7, 1000, "images/page-0007.png")]
    [InlineData(1234, 12345, "images/page-01234.png")]
    public void ImageFileName_HasAtLeastThreeDigits_AndMoreWhenThereAreManyPages(int index, int pageCount, string expected)
    {
        Assert.Equal(expected, MarkdownRenderer.ImageFileName(index, pageCount));
    }

    [Fact]
    public void Write_CreatesTheFileNamedAfterTheDocument_WithoutBom()
    {
        using var dir = new TempDir();
        var doc = Docx();
        doc.OutputFolder = dir.Combine("報告書");

        MarkdownRenderer.Write(doc);

        var path = dir.Combine("報告書", "報告書.md");
        Assert.True(File.Exists(path));
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
        Assert.Equal(MarkdownRenderer.Render(doc), File.ReadAllText(path));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
