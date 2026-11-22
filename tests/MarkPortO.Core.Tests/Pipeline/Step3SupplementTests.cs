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
using System.Drawing.Imaging;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Pipeline;

/// <summary>FR-150: Step3 adds to the text of Step1. It reads the pictures that Step1 saved one by one, and a page whole only when Step1 has (almost) nothing of it.</summary>
public class Step3SupplementTests
{
    private const string KindPrompt = "PAGE-PROMPT {pagetext}";
    private const string FigurePrompt = "FIGURE-PROMPT {pagetext}";
    private const string Caption = "Figure 1: Response time by month, from the monitoring of the service.";

    private sealed class FakeReader(Func<string, string> answer) : IVlmReader
    {
        public List<(string Prompt, int Width, bool IsPng)> Calls { get; } = [];

        public Task<VlmResponse> ReadAsync(string prompt, byte[] png, CancellationToken cancellationToken)
        {
            using var bitmap = new Bitmap(new MemoryStream(png));
            Calls.Add((prompt, bitmap.Width, png.Length > 4 && png[0] == 0x89 && png[1] == 0x50));
            return Task.FromResult(new VlmResponse(answer(prompt), new UsageInfo(10, 5, 15)));
        }
    }

    private sealed class NoEngine : IDocumentEngine
    {
        public EngineKind Kind => EngineKind.LibreOffice;

        public EngineAvailability CheckAvailability() => throw new NotSupportedException();

        public Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static void SaveImage(string path, int width, int height, ImageFormat format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(width, height);
        using var stream = File.Create(path);
        bitmap.Save(stream, format);
    }

    private static DocumentWork Work(TempDir dir, IVlmReader reader, Step3Mode mode, DocumentKind kind = DocumentKind.Docx, params string[] blocks)
    {
        var folder = dir.Combine("out", "doc");
        SaveImage(Path.Combine(folder, "images", "page-001.png"), 100, 140, ImageFormat.Png);
        var document = new DocumentRecord
        {
            DocId = "d0001",
            SourcePath = dir.Combine("in", kind == DocumentKind.Pdf ? "a.pdf" : "a.docx"),
            OutputFolder = folder,
            Kind = kind,
            MarkdownLanguage = "en",
            Step1 = StepStatus.Done,
            Step2 = StepStatus.Done,
            Step3 = StepStatus.Pending,
            Step3Mode = mode,
            ExtractPictures = true,
            Pages = [new PageRecord { Index = 1, SourceLabel = "page 1", ImageFile = "images/page-001.png" }],
            Blocks = [.. blocks.Select(b => new Block { Kind = BlockKind.Paragraph, Markdown = b, PageIndex = 1 })],
        };
        var store = new StateStore(dir.Combine("out"));
        var job = new JobState { JobId = Guid.NewGuid(), Documents = [document.DocId] };
        var context = new JobContext(job, store, new NoEngine(), NullLogger.Instance, TimeSpan.FromSeconds(1))
        {
            Vlm = reader,
            Prompts = new PromptSettings { Docx = KindPrompt, Pdf = KindPrompt, Figure = FigurePrompt },
        };
        return new DocumentWork(document, context);
    }

    private static string Picture(TempDir dir, string name = "page-001-1.png", ImageFormat? format = null)
    {
        SaveImage(Path.Combine(dir.Combine("out", "doc"), "pictures", name), 420, 180, format ?? ImageFormat.Png);
        return "pictures/" + name;
    }

    [Fact]
    public async Task APageWithASavedPicture_ReadsThePictureOnItsOwn_WithTheTextAroundIt()
    {
        using var dir = new TempDir();
        var file = Picture(dir);
        var reader = new FakeReader(_ => "July 196 ms, August 183 ms, September 180 ms.");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx, "The service was stable this quarter.", $"![chart]({file})", Caption);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.StartsWith("FIGURE-PROMPT ", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("![chart]", call.Prompt, StringComparison.Ordinal);
        Assert.Contains(Caption, call.Prompt, StringComparison.Ordinal); // the next block is sent with the picture
        Assert.DoesNotContain("stable this quarter", call.Prompt, StringComparison.Ordinal); // the block before is not
        Assert.DoesNotContain("pictures/", call.Prompt, StringComparison.Ordinal); // the file is not sent (FR-133)
        Assert.Equal(420, call.Width); // the picture itself, not the page image

        var page = work.Document.Pages[0];
        Assert.Equal(PageStep3Status.Done, page.Step3);
        Assert.Equal(PageReading.Pictures, page.ReadAs);
        Assert.Equal(Step3Mode.Supplement, page.ReadMode);
        Assert.Null(page.VlmText);
        Assert.Equal("July 196 ms, August 183 ms, September 180 ms.", Assert.Single(page.Figures!).Text);
        Assert.Equal(15, page.Usage!.TotalTokens);
    }

    [Fact]
    public async Task WhatWasReadInAPicture_IsWrittenAfterThePicture_InTheMarkdownAndInTheFileOfTheVlm()
    {
        using var dir = new TempDir();
        var file = Picture(dir);
        var reader = new FakeReader(_ => "| Month | ms |\n|---|---|\n| July | 196 |");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx, "The service was stable this quarter.", $"![chart]({file})", Caption);
        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var markdown = MarkdownRenderer.Render(work.Document);
        // After the caption: the reading does not come between the figure and its caption.
        Assert.Contains($"![chart]({file})\n\n{Caption}\n\n<!-- vlm:figure picture={file} -->\n> **What the VLM read in the figure**\n>\n> | Month | ms |\n> |---|---|\n> | July | 196 |\n<!-- vlm:figure-end -->", markdown, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(markdown, "The service was stable this quarter.")); // the text of Step1 is not written twice
        Assert.DoesNotContain("<!-- vlm:begin page=1", markdown, StringComparison.Ordinal);
        Assert.Contains("<!-- usage page=1 prompt=10 completion=5 total=15 -->", markdown, StringComparison.Ordinal);

        // Read alone, the file of the VLM has the text, the picture and what was read in it.
        work.Document.VlmSeparate = true;
        var vlm = MarkdownRenderer.RenderVlm(work.Document);
        Assert.Contains("The service was stable this quarter.", vlm, StringComparison.Ordinal);
        Assert.Contains($"![chart]({file})\n\n{Caption}\n\n<!-- vlm:figure picture={file} -->", vlm, StringComparison.Ordinal);
        Assert.Contains("on the other pages, the text of the document with what the VLM read in its figures", vlm, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- vlm:figure", MarkdownRenderer.Render(work.Document), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APictureWithAWidth_GetsWhatWasReadAfterTheImgTag()
    {
        using var dir = new TempDir();
        var file = Picture(dir);
        var img = $"<img src=\"{file}\" alt=\"chart\" width=\"400\">";
        var reader = new FakeReader(_ => "July 196 ms.");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx, img + " " + Caption);
        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Contains(img + " " + Caption + "\n\n<!-- vlm:figure picture=" + file + " -->", MarkdownRenderer.Render(work.Document), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Figure 1: Response time by month.", true)]
    [InlineData("図3 処理の流れ", true)]
    [InlineData("**Table 2.** Results", true)]
    [InlineData("Figures are drawn by the service team.", false)]
    [InlineData("The service was stable this quarter.", false)]
    [InlineData("Figure 1\nand a second line", false)]
    public void ACaption_IsOneShortLineThatNamesAFigure(string block, bool caption) =>
        Assert.Equal(caption, MarkdownRenderer.IsCaption(block));

    [Fact]
    public async Task WithoutACaption_WhatWasReadComesRightAfterThePicture()
    {
        using var dir = new TempDir();
        var file = Picture(dir);
        var work = Work(dir, new FakeReader(_ => "July 196 ms."), Step3Mode.Supplement, DocumentKind.Docx, $"![chart]({file})", "The service was stable this quarter.");
        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Contains($"![chart]({file})\n\n<!-- vlm:figure picture={file} -->", MarkdownRenderer.Render(work.Document), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("**None.**")]
    [InlineData("  ")]
    public async Task AnAnswerWithNothingToAdd_AddsNothing(string answer)
    {
        using var dir = new TempDir();
        var file = Picture(dir);
        var work = Work(dir, new FakeReader(_ => answer), Step3Mode.Supplement, DocumentKind.Docx, $"![chart]({file})", Caption);
        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(work.Document.Pages[0].Figures!).Text);
        Assert.DoesNotContain("vlm:figure", MarkdownRenderer.Render(work.Document), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NONE", true)]
    [InlineData("なし", true)]
    [InlineData("アイコン・飾りの画像・写真など、文書の内容として付け加えることがないもの: NONE", true)] // what Gemma E4B answered
    [InlineData("画面の写し:\nNONE", true)]
    [InlineData("**NONE**。", true)]
    [InlineData("- アクセスキー: なし", false)] // a value that was read
    [InlineData("| 項目 | 値 |\n|---|---|\n| 状態 | NONE |", false)] // a table with a value NONE
    [InlineData("July 196 ms, August 183 ms.", false)]
    [InlineData("The status is NONEXISTENT", false)]
    public void AnAnswerThatSaysNothingToAdd_IsTakenAsNothing(string answer, bool nothing) =>
        Assert.Equal(nothing, Step3Worker.FigureAnswer(answer).Length == 0);

    [Fact]
    public void ALongAnswerThatEndsWithNone_IsKept()
    {
        var answer = "- 種類: OpenAI互換のサーバー\n- 名前: local\n- URL: http://localhost:1234/v1\n- アクセスキー: NONE";
        Assert.Equal(answer, Step3Worker.FigureAnswer(answer));
    }

    [Fact]
    public async Task EachPictureOfAPage_IsReadOnce_AndAJpegIsSentAsAPng()
    {
        using var dir = new TempDir();
        var first = Picture(dir);
        var second = Picture(dir, "page-001-2.jpg", ImageFormat.Jpeg);
        var calls = 0;
        var reader = new FakeReader(_ => ++calls == 1 ? "FIRST" : "SECOND");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx, $"![first]({first})", $"![second]({second})", $"![first again]({first})");
        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Equal(2, reader.Calls.Count);
        Assert.All(reader.Calls, c => Assert.True(c.IsPng));
        Assert.Equal(["FIRST", "SECOND"], work.Document.Pages[0].Figures!.Select(f => f.Text));
        var markdown = MarkdownRenderer.Render(work.Document);
        Assert.Equal(1, CountOf(markdown, "> FIRST")); // after the first reference only
        Assert.Equal(1, CountOf(markdown, "> SECOND"));
    }

    [Fact]
    public async Task APageThatTheTextOfStep1IsEnoughFor_IsNotSent()
    {
        using var dir = new TempDir();
        var reader = new FakeReader(_ => "never");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx, "# Exception handling", "The worker catches every exception and records it in the log of the document.");

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Empty(reader.Calls);
        var page = work.Document.Pages[0];
        Assert.Equal(PageStep3Status.Done, page.Step3);
        Assert.Equal(PageReading.None, page.ReadAs);
        Assert.Null(page.Usage);
        Assert.Equal(StepStatus.Done, work.Document.Step3);

        var markdown = MarkdownRenderer.Render(work.Document);
        Assert.Contains("<!-- vlm:skipped page=1 reason=\"step1 text\" -->", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("VLM usage", markdown, StringComparison.Ordinal);
        work.Document.VlmSeparate = true;
        Assert.Contains("The worker catches every exception", MarkdownRenderer.RenderVlm(work.Document), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Page 3")] // Step1 has almost nothing of the page: its text is in a picture, a shape or a scan
    [InlineData("See the chart. ![chart]")] // a picture that was not saved
    public async Task APageThatStep1HasLittleOf_IsReadWhole(string text)
    {
        using var dir = new TempDir();
        var reader = new FakeReader(_ => "WHOLE PAGE");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx, text);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.StartsWith("PAGE-PROMPT", call.Prompt, StringComparison.Ordinal);
        Assert.Equal(100, call.Width); // the page image
        Assert.Equal(PageReading.Whole, work.Document.Pages[0].ReadAs);
        Assert.Equal("WHOLE PAGE", work.Document.Pages[0].VlmText);
        Assert.Contains("<!-- vlm:begin page=1 -->\nWHOLE PAGE\n<!-- vlm:end -->", MarkdownRenderer.Render(work.Document), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePictureSyntaxInCode_IsNotAPictureThatWasNotSaved()
    {
        using var dir = new TempDir();
        var reader = new FakeReader(_ => "never");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx,
            "- The text and the image reference (`![…]`) are not written. The VLM does not read the page.",
            $"- A code span of Step1 is written with marks: {CodeText.CodeMark}![Page N]{CodeText.CodeMark} and {CodeText.CodeMark}![…](images/page-NNN.png){CodeText.CodeMark}.",
            "```\n<!-- page: 5 -->\n\n![Page 5](images/page-005.png)\n![alt]\n```");

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Empty(reader.Calls);
        Assert.Equal(PageReading.None, work.Document.Pages[0].ReadAs);
    }

    [Theory]
    [InlineData(2, false)] // the end of a table row pushed over the page break, and the footer
    [InlineData(3, false)]
    [InlineData(12, true)] // a page that prints many lines that Step1 does not have (text in a shape, a scan): read whole
    public async Task APageWithNoBlockOfStep1_IsReadWhole_OnlyWhenItPrintsMoreThanAFewLines(int lines, bool read)
    {
        using var dir = new TempDir();
        var reader = new FakeReader(_ => "| | | and testing |");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx);
        work.Document.Pages[0].TextLines = [.. Enumerable.Range(0, lines).Select(i => new LineSpan(0.05 + (i * 0.05), 0.07 + (i * 0.05)))];

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Equal(read ? 1 : 0, reader.Calls.Count);
        Assert.Equal(read ? PageReading.Whole : PageReading.None, work.Document.Pages[0].ReadAs);
    }

    [Fact]
    public async Task APageWithOnlyTheRestOfAParagraphOfThePageBefore_IsNotSent()
    {
        using var dir = new TempDir();
        var reader = new FakeReader(_ => "and testing");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx, "Postman is a tool for API development and testing, used by many teams in their daily work.");
        SaveImage(Path.Combine(work.Document.OutputFolder, "images", "page-002.png"), 100, 140, ImageFormat.Png);
        work.Document.Pages.Add(new PageRecord { Index = 2, SourceLabel = "page 2", ImageFile = "images/page-002.png", TextLines = [.. Enumerable.Range(0, 8).Select(i => new LineSpan(0.05 + (i * 0.05), 0.07 + (i * 0.05)))] });
        work.Document.Blocks.Add(new Block { Kind = BlockKind.Paragraph, Markdown = "and testing", PageIndex = 2, JoinedToPage = 1 });

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Empty(reader.Calls);
        Assert.Equal(PageReading.None, work.Document.Pages[1].ReadAs);
    }

    [Fact]
    public async Task APdf_IsReadWhole()
    {
        using var dir = new TempDir();
        var reader = new FakeReader(_ => "WHOLE PAGE");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Pdf, "A long enough text layer of the page, with many words in it.");

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.StartsWith("PAGE-PROMPT", Assert.Single(reader.Calls).Prompt, StringComparison.Ordinal);
        Assert.Equal(PageReading.Whole, work.Document.Pages[0].ReadAs);
    }

    [Fact]
    public async Task Transcribe_ReadsAPageWithPicturesWhole_AsBefore()
    {
        using var dir = new TempDir();
        var file = Picture(dir);
        var reader = new FakeReader(_ => "WHOLE PAGE");
        var work = Work(dir, reader, Step3Mode.Transcribe, DocumentKind.Docx, "The service was stable this quarter.", $"![chart]({file})");

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.StartsWith("PAGE-PROMPT", call.Prompt, StringComparison.Ordinal);
        Assert.Equal(100, call.Width);
        Assert.Equal(PageReading.Whole, work.Document.Pages[0].ReadAs);
        Assert.Equal(Step3Mode.Transcribe, work.Document.Pages[0].ReadMode);
        Assert.Null(work.Document.Pages[0].Figures);
    }

    [Fact]
    public async Task APageReadWithTheOtherUse_IsOutOfDate()
    {
        using var dir = new TempDir();
        var work = Work(dir, new FakeReader(_ => "never"), Step3Mode.Supplement, DocumentKind.Docx, "# Exception handling", "The worker catches every exception and records it in the log.");
        await new Step3Worker().ProcessAsync(work, CancellationToken.None);
        var document = work.Document;

        Assert.Empty(RereadMarks.Stale(document, "other-model", "other-prompt", Step3Mode.Supplement)); // not sent: no model or prompt to be out of date
        Assert.Single(RereadMarks.Stale(document, null, "standard", Step3Mode.Transcribe));

        // A page read before FR-150 was read whole.
        document.Pages[0].ReadMode = null;
        document.Pages[0].ReadAs = PageReading.Whole;
        document.Pages[0].VlmPrompt = "standard";
        Assert.Empty(RereadMarks.Stale(document, null, "standard", Step3Mode.Transcribe));
        Assert.Single(RereadMarks.Stale(document, null, "standard", Step3Mode.Supplement));
        Assert.Empty(RereadMarks.Stale(document, null, "standard")); // the use not compared
    }

    [Fact]
    public async Task ThePreview_ShowsThePageWithWhatWasReadAsTheVlmResult_AndTheTextWithoutIt()
    {
        using var dir = new TempDir();
        var file = Picture(dir);
        var work = Work(dir, new FakeReader(_ => "July 196 ms."), Step3Mode.Supplement, DocumentKind.Docx, "The service was stable this quarter.", $"![chart]({file})");
        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var page = Assert.Single(PagePreview.Load(work.Document));
        Assert.NotNull(page.Vlm);
        Assert.Contains("The service was stable this quarter.", page.Vlm, StringComparison.Ordinal);
        Assert.Contains("> July 196 ms.", page.Vlm, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- usage", page.Vlm, StringComparison.Ordinal);
        var text = PagePreview.WithoutVlm(page.Markdown);
        Assert.Contains($"![chart]({file})", text, StringComparison.Ordinal);
        Assert.DoesNotContain("July 196", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDrawnShapesOfAWordPage_AreReadFromThePageImage_AndWrittenAfterTheirParagraph()
    {
        using var dir = new TempDir();
        var reader = new FakeReader(_ => "Two boxes, a circle and an arrow, from left to right.");
        var work = Work(dir, reader, Step3Mode.Supplement, DocumentKind.Docx, "# Overview", "This document is designed for testing document processors.", "## Definitions");
        work.Document.Blocks[1].HasDrawing = true;

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.Equal(100, call.Width); // the page image
        Assert.StartsWith("FIGURE-PROMPT", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("This image is a whole page, not one figure.", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("# Overview", call.Prompt, StringComparison.Ordinal); // the whole text of the page, so that it is not written again
        Assert.Contains("## Definitions", call.Prompt, StringComparison.Ordinal);
        Assert.Equal(PageReading.Pictures, work.Document.Pages[0].ReadAs);

        var markdown = MarkdownRenderer.Render(work.Document);
        Assert.Contains("This document is designed for testing document processors.\n\n<!-- vlm:figure picture=images/page-001.png -->\n> **What the VLM read in the figure**\n>\n> Two boxes, a circle and an arrow, from left to right.\n<!-- vlm:figure-end -->\n\n## Definitions", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.3, 0.2, false, 1, true)] // a shape without text
    [InlineData(0.3, 0.2, true, 1, false)] // one text box: its text is in Step1
    [InlineData(0.3, 0.2, true, 2, true)] // boxes with words in them: a diagram
    [InlineData(0.02, 0.01, false, 1, false)] // a small mark
    [InlineData(0.02, 0.01, false, 3, false)] // small marks
    public void WhatIsADrawing(double width, double height, bool hasText, int count, bool drawing) =>
        Assert.Equal(drawing, MarkPortO.Core.Ooxml.DocxReader.IsDrawing([.. Enumerable.Repeat(new MarkPortO.Core.Ooxml.ShapeBox("rect", width, height, HasText: hasText), count)]));

    [Fact]
    public async Task TheFileList_SaysThatNothingWasSent_NotThatTheUsageIsUnavailable()
    {
        using var dir = new TempDir();
        var work = Work(dir, new FakeReader(_ => "never"), Step3Mode.Supplement, DocumentKind.Docx, "# Exception handling", "The worker catches every exception and records it in the log.");
        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var row = FileStatusProjector.Project(work.Document);
        Assert.Equal(FileStatusProjector.NotSentText, row.UsageText);
        Assert.Equal(FileStatusProjector.NotSentText, Assert.Single(row.Pages).UsageText);
        Assert.NotEqual(FileStatusProjector.UnavailableText, FileStatusProjector.NotSentText);

        // A file with a page that was sent has its tokens; the page that was not sent still says so.
        work.Document.Pages.Add(new PageRecord { Index = 2, SourceLabel = "page 2", Step3 = PageStep3Status.Done, ReadAs = PageReading.Pictures, Usage = new UsageInfo(10, 5, 15) });
        work.Document.UsageTotal = null;
        row = FileStatusProjector.Project(work.Document);
        Assert.Equal("prompt=10 completion=5 total=15", row.UsageText);
        Assert.Equal(FileStatusProjector.NotSentText, row.Pages[0].UsageText);
    }

    [Fact]
    public void APageOfJoinedTableRowsOnly_SaysWhereTheRowsWent_InTheFileOfTheVlmAndInThePreview()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("out", "doc");
        Directory.CreateDirectory(folder);
        var document = new DocumentRecord
        {
            DocId = "d0001",
            SourcePath = dir.Combine("in", "a.docx"),
            OutputFolder = folder,
            Kind = DocumentKind.Docx,
            MarkdownLanguage = "en",
            Step1 = StepStatus.Done,
            Step2 = StepStatus.NotApplicable,
            Step3 = StepStatus.Done,
            Step1Tables = Step1TablesMode.Replace,
            VlmSeparate = true,
            Pages =
            [
                new PageRecord { Index = 1, SourceLabel = "page 1", Step3 = PageStep3Status.Done, VlmText = "READ WHOLE" },
                new PageRecord { Index = 2, SourceLabel = "page 2", Step3 = PageStep3Status.Skipped, IsJoinedTableOnly = true },
            ],
            Blocks =
            [
                new Block { Kind = BlockKind.Table, Markdown = "| a | b |\n|---|---|\n| 1 | 2 |", PageIndex = 1 },
                new Block { Kind = BlockKind.Table, Markdown = "| a | b |\n|---|---|\n| 3 | 4 |", PageIndex = 2, JoinedToPage = 1 },
            ],
        };

        MarkdownRenderer.Write(document);

        var vlm = MarkdownRenderer.RenderVlm(document);
        var page2 = vlm[vlm.IndexOf("<!-- page: 2 -->", StringComparison.Ordinal)..];
        Assert.Contains("> The rows of this table are joined to the table on page 1.", page2, StringComparison.Ordinal);
        Assert.Contains("reason=\"joined table\"", page2, StringComparison.Ordinal);
        Assert.DoesNotContain("| 3 | 4 |", page2, StringComparison.Ordinal);
        Assert.Contains("The rows of this table are joined", PagePreview.Load(document)[1].Vlm, StringComparison.Ordinal);

        // The results in the Markdown of the document: the preview shows the quote as the result of the VLM too.
        document.VlmSeparate = false;
        MarkdownRenderer.Write(document);
        Assert.Contains("The rows of this table are joined", PagePreview.Load(document)[1].Vlm, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettings_AddToTheText_ButAJobFromBefore_ReadsWhole()
    {
        Assert.Equal(Step3Mode.Supplement, JsonSerializer.Deserialize<StepSettings>("{}")!.Step3Mode);
        Assert.Equal(Step3Mode.Transcribe, JsonSerializer.Deserialize<OptionsSnapshot>("{}")!.Step3Mode);
        Assert.Equal(Step3Mode.Transcribe, JsonSerializer.Deserialize<DocumentRecord>("{}")!.Step3Mode);
        Assert.Equal(PageReading.Whole, JsonSerializer.Deserialize<PageRecord>("{}")!.ReadAs);
        Assert.Equal("\"transcribe\"", JsonSerializer.Serialize(Step3Mode.Transcribe));
        Assert.Equal(Step3Mode.Supplement, ConvertRequestOptions().Step3Mode);
    }

    [Fact]
    public void TheFigurePrompt_FollowsTheLanguage_UntilItIsEdited()
    {
        var prompts = new PromptSettings();
        Assert.Equal(PromptSettings.DefaultFigureEn, prompts.FigureFor("en"));
        Assert.Equal(PromptSettings.DefaultFigure, prompts.FigureFor("ja"));
        Assert.Contains(PromptSupplement.PageTextPlaceholder, PromptSettings.DefaultFigure, StringComparison.Ordinal);
        Assert.Contains(PromptSupplement.PageTextPlaceholder, PromptSettings.DefaultFigureEn, StringComparison.Ordinal);
        prompts.Figure = "MINE {pagetext}";
        Assert.Equal("MINE {pagetext}", prompts.FigureFor("ja"));
        prompts.Figure = " ";
        Assert.Equal(PromptSettings.DefaultFigureEn, prompts.FigureFor("en"));
    }

    private static OptionsSnapshot ConvertRequestOptions() => MarkPortO.Core.Convert.ConvertRequest.OptionsFrom(new AppSettings());

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
