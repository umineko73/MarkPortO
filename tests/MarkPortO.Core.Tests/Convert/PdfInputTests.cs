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

using Microsoft.Extensions.Logging.Abstractions;
using MarkPortO.Core.Convert;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Vlm;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Pdf;
using MarkPortO.Core.Tests.Fixtures;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Convert;

/// <summary>A PDF is an input (FR-076): the page images are the content, the text layer only helps. No Office or LibreOffice is used.</summary>
public class PdfInputTests
{
    private static OptionsSnapshot PdfOptions(bool usePdfText = true)
    {
        var options = Options();
        options.Types = [DocumentKind.Pdf];
        options.UsePdfText = usePdfText;
        return options;
    }

    [Fact]
    public async Task APdfHasOneImagePerPage_AndTheTextLayerIsTheTextOfThePage()
    {
        using var dir = new TempDir();
        var source = PdfFactory.Create(dir.Combine("in", "規程 2026.pdf"), PageSize.A4, "First page text", "Second page text");

        var result = await RunAsync(dir, Request(dir.Combine("out"), PdfOptions(), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Equal(DocumentKind.Pdf, document.Kind);
        Assert.Equal(2, ImageFiles(document.OutputFolder).Length);
        Assert.Equal(["page:1", "page:2"], document.Pages.Select(p => p.SourceLabel));
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "規程 2026.md"));
        Assert.Contains("<!-- page: 1 -->\n\n![ページ 1](images/page-001.png)\n\nFirst page text", markdown);
        Assert.Contains("<!-- page: 2 -->\n\n![ページ 2](images/page-002.png)\n\nSecond page text", markdown);
    }

    [Fact]
    public async Task WithTheTextOff_OnlyTheImagesAreMade()
    {
        using var dir = new TempDir();
        var source = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Hello text");

        var result = await RunAsync(dir, Request(dir.Combine("out"), PdfOptions(usePdfText: false), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Single(ImageFiles(document.OutputFolder));
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "a.md"));
        Assert.DoesNotContain("Hello text", markdown);
        Assert.Empty(document.Blocks);
    }

    [Fact]
    public async Task APageWithoutATextLayer_IsKept_AndTheRunTellsIt()
    {
        using var dir = new TempDir();
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A5).AddText("with text", 14, new PdfPoint(50, 300), font);
        builder.AddPage(PageSize.A5); // like a scan: nothing to read
        var source = dir.Combine("in", "scan.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, builder.Build());

        var result = await RunAsync(dir, Request(dir.Combine("out"), PdfOptions(), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Equal(2, document.Pages.Count);
        Assert.Contains(document.Warnings, w => w.Code == "PdfNoText");
    }

    [Fact]
    public async Task AFileThatIsNotAPdf_FailsWithAMessage_AndDoesNotStopTheRun()
    {
        using var dir = new TempDir();
        var bad = dir.Combine("in", "broken.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllText(bad, "this is not a pdf");
        var good = PdfFactory.Create(dir.Combine("in", "good.pdf"), PageSize.A4, "fine");

        var result = await RunAsync(dir, Request(dir.Combine("out"), PdfOptions(), bad, good));

        Assert.Equal(2, result.Documents.Count);
        Assert.True(result.Documents.Single(d => d.SourcePath == bad).IsFailed());
        Assert.True(result.Documents.Single(d => d.SourcePath == good).IsDone());
    }

    [Fact]
    public void AFolderIsSearchedForPdfFiles_WhenThePdfTypeIsChosen()
    {
        using var dir = new TempDir();
        PdfFactory.Create(dir.Combine("docs", "a.pdf"), PageSize.A4, "x");

        var with = InputEnumerator.Enumerate([dir.Combine("docs")], [DocumentKind.Pdf], recurse: false);
        var without = InputEnumerator.Enumerate([dir.Combine("docs")], [DocumentKind.Docx], recurse: false);

        Assert.Single(with.Inputs);
        Assert.Empty(without.Inputs);
    }

    [Fact]
    public void LinesThatStandCloseAreOneParagraph_AndALargerGapStartsANewOne()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A5);
        page.AddText("alpha beta", 12, new PdfPoint(40, 380), font);
        page.AddText("gamma delta", 12, new PdfPoint(40, 365), font);
        page.AddText("far below", 12, new PdfPoint(40, 300), font);
        using var document = UglyToad.PdfPig.PdfDocument.Open(builder.Build());

        var paragraphs = PdfParagraphExtractor.GetParagraphs(document, 0);

        Assert.Equal(["alpha beta gamma delta", "far below"], paragraphs);
    }

    // FR-108: two columns are read one after the other, not line by line across both.
    private static UglyToad.PdfPig.PdfDocument TwoColumns(int lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        for (var i = 0; i < lines; i++)
        {
            page.AddText($"left column line {i + 1} with some words", 11, new PdfPoint(40, 780 - (i * 16)), font);
            page.AddText($"right column line {i + 1} with some words", 11, new PdfPoint(320, 780 - (i * 16)), font);
        }

        return UglyToad.PdfPig.PdfDocument.Open(builder.Build());
    }

    [Fact]
    public void TwoColumns_AreReadOneAfterTheOther()
    {
        using var document = TwoColumns(10);

        var text = string.Join(" ", PdfParagraphExtractor.GetParagraphs(document, 0));

        Assert.Contains("left column line 1 with some words left column line 2", text);
        Assert.True(text.IndexOf("left column line 10", StringComparison.Ordinal) < text.IndexOf("right column line 1 ", StringComparison.Ordinal));
    }

    [Fact]
    public void AFewLinesInTwoPlaces_AreNotColumns()
    {
        using var document = TwoColumns(3);

        var text = string.Join(" ", PdfParagraphExtractor.GetParagraphs(document, 0));

        Assert.Contains("words right column line 1", text);
    }

    [Fact]
    public void ThePromptOfAPdfSaysThatTheImageComesFirst()
    {
        var prompts = new MarkPortO.Core.Settings.PromptSettings();

        Assert.Contains("{pagetext}", prompts.For(DocumentKind.Pdf, "ja"));
        Assert.Contains("画像を優先", prompts.For(DocumentKind.Pdf, "ja"));
        Assert.Contains("trust the image", prompts.For(DocumentKind.Pdf, "en"));
    }

    private sealed class UnavailableEngine : IDocumentEngine
    {
        public EngineKind Kind => EngineKind.Office;

        public EngineAvailability CheckAvailability() => EngineAvailability.Unavailable("no engine on this machine");

        public Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AnOnlyPdfRun_NeedsNoEngine_ButARunWithAnOfficeFileDoes()
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "text");
        var docx = dir.Combine("in", "b.docx");
        DocxFactory.CreatePaged(docx, ["x"]);

        var service = new ConvertService(new UnavailableEngine());
        var done = await service.RunAsync(Request(dir.Combine("out"), PdfOptions(), pdf), CancellationToken.None);
        var start = await Assert.ThrowsAsync<ConvertStartException>(() => service.RunAsync(Request(dir.Combine("out2"), PdfOptions(), pdf, docx), CancellationToken.None));

        Assert.True(Assert.Single(done.Documents).IsDone());
        Assert.Contains("no engine", start.Message);
    }

    [Fact]
    public async Task AFileOfAnotherExtension_IsRefusedBeforeTheRun_AndTheMessageNamesPdf()
    {
        using var dir = new TempDir();
        var txt = dir.Combine("in", "a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(txt)!);
        File.WriteAllText(txt, "x");

        var start = await Assert.ThrowsAsync<ConvertStartException>(() => RunAsync(dir, Request(dir.Combine("out"), PdfOptions(), txt)));

        Assert.Contains(".pdf", start.Message);
    }

    [Fact]
    public async Task APasswordProtectedPdf_FailsWithAMessage()
    {
        using var dir = new TempDir();
        var path = dir.Combine("in", "locked.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, EncryptedPdf());

        var result = await RunAsync(dir, Request(dir.Combine("out"), PdfOptions(), path));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsFailed());
        Assert.False(string.IsNullOrEmpty(document.Error?.Message));
    }

    // A PDF whose trailer has an /Encrypt dictionary (standard handler) that the empty password does not open.
    private static string EncryptedPdf() =>
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
        + "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\n"
        + "4 0 obj<</Filter/Standard/V 1/R 2/O(01234567890123456789012345678901)/U(01234567890123456789012345678901)/P -4>>endobj\n"
        + "trailer<</Size 5/Root 1 0 R/Encrypt 4 0 R/ID[<00112233445566778899aabbccddeeff><00112233445566778899aabbccddeeff>]>>\n%%EOF\n";

    [LibreOfficeFact]
    public async Task APdfAndAnOfficeFile_AreConvertedInOneRun()
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "pdf page");
        var docx = dir.Combine("in", "b.docx");
        DocxFactory.CreatePaged(docx, ["docx page"]);
        var options = Options();
        options.Types = [DocumentKind.Docx, DocumentKind.Pdf];

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, pdf, docx));

        Assert.Equal(2, result.Documents.Count);
        Assert.All(result.Documents, d => Assert.True(d.IsDone()));
    }

    [Fact]
    public void TheOptionsOfAJobKeepTheSwitchOfThePdfText_AndAnOldJobHasItOn()
    {
        var options = Options();
        options.UsePdfText = false;
        var json = System.Text.Json.JsonSerializer.Serialize(options);
        var back = System.Text.Json.JsonSerializer.Deserialize<OptionsSnapshot>(json)!;
        var old = System.Text.Json.JsonSerializer.Deserialize<OptionsSnapshot>("{\"dpi\":150}")!;

        Assert.False(back.UsePdfText);
        Assert.True(old.UsePdfText);
    }

    private sealed class RecordingReader : IVlmReader
    {
        public List<string> Prompts { get; } = [];

        public Task<VlmResponse> ReadAsync(string prompt, byte[] png, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return Task.FromResult(new VlmResponse("read from the image", new UsageInfo(1, 1, 2)));
        }
    }

    [Fact]
    public async Task Step3_UsesThePromptOfThePdf_AndSendsTheTextOfThePage()
    {
        using var dir = new TempDir();
        var source = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Invoice number 4711");
        var out1 = dir.Combine("out");
        var result = await RunAsync(dir, Request(out1, PdfOptions(), source));
        var document = Assert.Single(result.Documents);
        document.Step3 = StepStatus.Pending;
        var reader = new RecordingReader();
        var job = new JobState { JobId = Guid.NewGuid(), Documents = [document.DocId] };
        var context = new JobContext(job, new StateStore(out1), new UnavailableEngine(), NullLogger.Instance, TimeSpan.FromSeconds(1))
        {
            Vlm = reader,
            Prompts = new MarkPortO.Core.Settings.PromptSettings { Pdf = "PDF-PROMPT\n{pagetext}", Docx = "WRONG-PROMPT" },
        };

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        var prompt = Assert.Single(reader.Prompts);
        Assert.Contains("PDF-PROMPT", prompt);
        Assert.Contains("Invoice number 4711", prompt);
        Assert.DoesNotContain("WRONG-PROMPT", prompt);
        Assert.Equal("read from the image", document.Pages[0].VlmText);
    }

    [Fact]
    public void TheTextOfAPage_CannotBreakTheStructureOfTheMarkdown()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("# not a heading", 12, new PdfPoint(40, 700), font);
        page.AddText("<!-- page: 9 -->", 12, new PdfPoint(40, 600), font);
        using var document = UglyToad.PdfPig.PdfDocument.Open(builder.Build());

        var paragraphs = PdfParagraphExtractor.GetParagraphs(document, 0);

        Assert.Equal(2, paragraphs.Count);
        Assert.StartsWith("\\#", paragraphs[0]);
        Assert.DoesNotContain("<!--", paragraphs[1]);
    }
}
