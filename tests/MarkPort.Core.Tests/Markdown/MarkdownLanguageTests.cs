// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using MarkPort.Core.Convert;
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Localization;
using MarkPort.Core.Markdown;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Tests.Localization;
using static MarkPort.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPort.Core.Tests.Markdown;

/// <summary>FR-036: the texts of the Markdown follow the language of the job; the structure does not change.</summary>
public class MarkdownLanguageTests
{
    private static DocumentRecord Document(string language)
    {
        var doc = new DocumentRecord
        {
            MarkdownLanguage = language,
            DocId = "d0001",
            SourcePath = @"D:\in\report.docx",
            Kind = DocumentKind.Docx,
            OutputFolder = @"D:\out\report",
            Step1 = StepStatus.Done,
            Step2 = StepStatus.Done,
            Step3 = StepStatus.Done,
        };
        doc.Pages =
        [
            new PageRecord
            {
                Index = 1,
                SourceLabel = PageLabel.Page(1),
                ImageFile = "images/page-001.png",
                Step3 = PageStep3Status.Done,
                VlmText = "read text",
                Usage = new UsageInfo(100, 20, 120),
            },
            new PageRecord { Index = 2, SourceLabel = PageLabel.Page(2), ImageFile = "images/page-002.png", IsDeleted = true, Step3 = PageStep3Status.Skipped },
        ];
        doc.Blocks = [new Block { Kind = BlockKind.Paragraph, Markdown = "First page text.", PageIndex = 1 }];
        return doc;
    }

    /// <summary>The lines of the Markdown that are marks for a machine, which must not depend on the language.</summary>
    private static List<string> Marks(string markdown) =>
        [.. markdown.Split('\n').Where(l => l.StartsWith("<!--", StringComparison.Ordinal) || l.StartsWith("![", StringComparison.Ordinal))
            .Select(l => l.StartsWith("![", StringComparison.Ordinal) ? l[(l.IndexOf("](", StringComparison.Ordinal))..] : l)];

    private static string MarkdownOf(DocumentRecord document) =>
        File.ReadAllText(Path.Combine(document.OutputFolder, Path.GetFileNameWithoutExtension(document.SourcePath) + ".md"));

    [Fact]
    public void English_WritesTheTextsInEnglish()
    {
        var markdown = MarkdownRenderer.Render(Document("en"));

        Assert.StartsWith("# report.docx\n\n> **About this Markdown**", markdown);
        Assert.Contains("> - Source file: `report.docx`", markdown);
        Assert.Contains("> **[Deleted page]** Page 2 of the original document was excluded because an X was drawn over the whole page. The image is saved as `images/page-002.png`.", markdown);
        Assert.Contains("> **VLM usage**: total prompt=100 completion=20 total=120 (pages: 1, pages with usage: 1)", markdown);
        Assert.Contains("![Page 1](images/page-001.png)", markdown);
        Assert.DoesNotMatch("[ぁ-んァ-ン一-龥]", markdown.Replace("report.docx", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Japanese_WritesTheTextsInJapanese()
    {
        var markdown = MarkdownRenderer.Render(Document("ja"));

        Assert.StartsWith("# report.docx\n\n> **このMarkdownの構造**", markdown);
        Assert.Contains("> **[削除ページ]** 元の文書の2ページ目は、全面に×印が付けられていたため除外しました。画像は `images/page-002.png` に保存しています。", markdown);
        Assert.Contains("> **VLM使用量**: 合計 prompt=100 completion=20 total=120（ページ数: 1、取得できたページ: 1）", markdown);
        Assert.Contains("![ページ 1](images/page-001.png)", markdown);
    }

    [Fact]
    public void TheMarksForAMachine_AreTheSameInBothLanguages_AndTheDeletedPageHasALanguageFreeMark()
    {
        var english = MarkdownRenderer.Render(Document("en"));
        var japanese = MarkdownRenderer.Render(Document("ja"));

        Assert.Equal(Marks(english), Marks(japanese));
        Assert.Contains("<!-- deleted page=2 -->", english);
        Assert.Contains("<!-- deleted page=2 -->", japanese);
    }

    [Fact]
    public void TheLanguageOfTheMessages_DoesNotChangeTheMarkdown()
    {
        // The Markdown follows the language of the job, not the language the screen has now.
        string inEnglishScreen;
        string inJapaneseScreen;
        using (TestCulture.Use("en"))
        {
            inEnglishScreen = MarkdownRenderer.Render(Document("ja"));
        }

        using (TestCulture.Use("ja"))
        {
            inJapaneseScreen = MarkdownRenderer.Render(Document("ja"));
        }

        Assert.Equal(inEnglishScreen, inJapaneseScreen);
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task TheJobRecordsTheLanguage_AndAResumeKeepsIt()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);
        var outFolder = dir.Combine("out");

        var result = await RunAsync(dir, Request(outFolder, Options(markdownLanguage: "en"), source));

        Assert.Equal("en", result.Job.Options.MarkdownLanguage);
        Assert.Equal("en", Assert.Single(result.Documents).MarkdownLanguage);
        Assert.StartsWith("# a.docx\n\n> **About this Markdown**", MarkdownOf(result.Documents[0]));

        // The language is in the saved state, so a resume writes English again even when the screen is Japanese by then.
        var store = new StateStore(outFolder);
        Assert.Equal("en", store.LoadJob(result.Job.JobId)!.Options.MarkdownLanguage);
        Assert.Equal("en", store.LoadDocument(result.Job.JobId, result.Documents[0].DocId)!.MarkdownLanguage);
    }
}
