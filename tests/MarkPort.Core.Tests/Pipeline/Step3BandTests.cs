// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
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

using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MarkPort.Core.Engines;
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Pipeline;
using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Vlm;

namespace MarkPort.Core.Tests.Pipeline;

/// <summary>FR-041, FR-042: Step 3 reads a page with many lines in bands, with the fixed supplement, and joins the answers.</summary>
public class Step3BandTests
{
    private sealed class FakeReader(Func<int, string, int, VlmResponse> answer) : IVlmReader
    {
        public List<(string Prompt, int Height)> Calls { get; } = [];

        public Task<VlmResponse> ReadAsync(string prompt, byte[] png, CancellationToken cancellationToken)
        {
            using var bitmap = new Bitmap(new MemoryStream(png));
            Calls.Add((prompt, bitmap.Height));
            return Task.FromResult(answer(Calls.Count, prompt, bitmap.Height));
        }
    }

    private sealed class NoEngine : IDocumentEngine
    {
        public EngineKind Kind => EngineKind.LibreOffice;

        public EngineAvailability CheckAvailability() => throw new NotSupportedException();

        public Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static DocumentWork Work(TempDir dir, int lines, string language = "en")
    {
        var folder = dir.Combine("out", "doc");
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        using (var bitmap = new Bitmap(100, 1000))
        {
            using var stream = File.Create(Path.Combine(folder, "images", "page-1.png"));
            bitmap.Save(stream, ImageFormat.Png);
        }

        var page = new PageRecord
        {
            Index = 1,
            SourceLabel = "page 1",
            ImageFile = "images/page-1.png",
            TextLines = [.. Enumerable.Range(0, lines).Select(i => new LineSpan(0.05 + (i * 0.02), 0.06 + (i * 0.02)))],
        };
        var document = new DocumentRecord
        {
            DocId = "d0001",
            SourcePath = dir.Combine("in", "a.docx"),
            OutputFolder = folder,
            Kind = DocumentKind.Docx,
            MarkdownLanguage = language,
            Step1 = StepStatus.Done,
            Step2 = StepStatus.Done,
            Step3 = StepStatus.Pending,
            Pages = [page],
        };
        return new DocumentWork(document, Context(dir, document));
    }

    private static JobContext Context(TempDir dir, DocumentRecord document, int maxLines = 0, IVlmReader? reader = null)
    {
        var store = new StateStore(dir.Combine("out"));
        var job = new JobState { JobId = Guid.NewGuid(), Documents = [document.DocId] };
        return new JobContext(job, store, new NoEngine(), NullLogger.Instance, TimeSpan.FromSeconds(1))
        {
            Vlm = reader,
            Prompts = new PromptSettings { Docx = "DOCX-PROMPT" },
            MaxLinesPerRead = maxLines,
        };
    }

    private static DocumentWork WithContext(DocumentWork work, TempDir dir, int maxLines, IVlmReader reader) =>
        new(work.Document, Context(dir, work.Document, maxLines, reader));

    private static string Table(int from, int to) =>
        string.Join("\n", Enumerable.Range(from, to - from + 1).Select(i => $"| {i:000} | item {i} |"));

    [Fact]
    public async Task AFewLines_AreReadInOneRequest_WithTheSupplement()
    {
        using var dir = new TempDir();
        var reader = new FakeReader((_, _, _) => new VlmResponse("whole", new UsageInfo(10, 5, 15)));
        var work = WithContext(Work(dir, lines: 10), dir, maxLines: 15, reader);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var call = Assert.Single(reader.Calls);
        Assert.Equal(PromptSupplement.Build("DOCX-PROMPT", "en"), call.Prompt);
        Assert.Equal(1000, call.Height);
        var page = work.Document.Pages[0];
        Assert.Equal(PageStep3Status.Done, page.Step3);
        Assert.Equal(0, page.Bands);
    }

    [Fact]
    public async Task ManyLines_AreReadInBands_FromTheTop_AndTheAnswersAreJoined()
    {
        using var dir = new TempDir();
        var reader = new FakeReader((n, _, _) => new VlmResponse(n switch
        {
            1 => "| No | Name |\n|---|---|\n" + Table(1, 15),
            2 => Table(16, 30),
            _ => Table(31, 44),
        }, new UsageInfo(10, 5, 15)));
        var work = WithContext(Work(dir, lines: 44), dir, maxLines: 15, reader);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Equal(3, reader.Calls.Count);
        Assert.All(reader.Calls, c => Assert.InRange(c.Height, 1, 500));
        Assert.Contains("part 1 of 3", reader.Calls[0].Prompt);
        Assert.Contains("part 3 of 3", reader.Calls[2].Prompt);
        var page = work.Document.Pages[0];
        Assert.Equal(PageStep3Status.Done, page.Step3);
        Assert.Equal(3, page.Bands);
        Assert.Equal("| No | Name |\n|---|---|\n" + Table(1, 44), page.VlmText);
        Assert.Equal(45, page.Usage!.TotalTokens); // the usage of the bands is added up
    }

    [Fact]
    public async Task TheMarkdown_ShowsTheNumberOfBands_AndOneUsage()
    {
        using var dir = new TempDir();
        var reader = new FakeReader((n, _, _) => new VlmResponse($"band {n}", new UsageInfo(10, 5, 15)));
        var work = WithContext(Work(dir, lines: 44), dir, maxLines: 15, reader);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var markdown = File.ReadAllText(Path.Combine(work.Document.OutputFolder, "a.md"));
        Assert.Contains("<!-- vlm:begin page=1 bands=3 -->", markdown);
        Assert.Contains("band 1\n\nband 2\n\nband 3", markdown);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markdown, "<!-- usage page=1 "));
    }

    [Fact]
    public async Task APageReadWhole_HasNoBandsMark()
    {
        using var dir = new TempDir();
        var reader = new FakeReader((_, _, _) => new VlmResponse("whole", new UsageInfo(1, 1, 2)));
        var work = WithContext(Work(dir, lines: 44), dir, maxLines: 0, reader);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Single(reader.Calls);
        var markdown = File.ReadAllText(Path.Combine(work.Document.OutputFolder, "a.md"));
        Assert.Contains("<!-- vlm:begin page=1 -->", markdown);
        Assert.DoesNotContain("bands=", markdown);
    }

    [Fact]
    public async Task APageWithoutLineInformation_IsReadWhole()
    {
        using var dir = new TempDir();
        var reader = new FakeReader((_, _, _) => new VlmResponse("whole", UsageInfo.Unavailable));
        var work = WithContext(Work(dir, lines: 44), dir, maxLines: 15, reader);
        work.Document.Pages[0].TextLines = null;

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Single(reader.Calls);
    }

    [Fact]
    public async Task WhenOneBandFails_TheWholePageFails_AndNoPartialTextIsKept()
    {
        using var dir = new TempDir();
        var reader = new FakeReader((n, _, _) => n == 2 ? throw new VlmException("server error") : new VlmResponse($"band {n}", new UsageInfo(1, 1, 2)));
        var work = WithContext(Work(dir, lines: 44), dir, maxLines: 15, reader);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        var page = work.Document.Pages[0];
        Assert.Equal(PageStep3Status.Failed, page.Step3);
        Assert.Null(page.VlmText);
        Assert.Contains("server error", page.Error);
        Assert.Equal(2, reader.Calls.Count); // the third band is not read
    }

    [Fact]
    public async Task ATimedOutBand_IsReadOnceMore()
    {
        using var dir = new TempDir();
        var attempts = 0;
        var reader = new FakeReader((n, _, _) =>
        {
            attempts++;
            return n == 2 && attempts == 2 ? throw new VlmException("timed out", timedOut: true) : new VlmResponse($"band {attempts}", new UsageInfo(1, 1, 2));
        });
        var work = WithContext(Work(dir, lines: 44), dir, maxLines: 15, reader);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.Equal(PageStep3Status.Done, work.Document.Pages[0].Step3);
        Assert.Equal(4, reader.Calls.Count); // three bands and one more try
    }

    [Fact]
    public async Task WhenTheBandsReportNoUsage_TheUsageOfThePageIsUnavailable()
    {
        using var dir = new TempDir();
        var reader = new FakeReader((n, _, _) => new VlmResponse($"band {n}", UsageInfo.Unavailable));
        var work = WithContext(Work(dir, lines: 44), dir, maxLines: 15, reader);

        await new Step3Worker().ProcessAsync(work, CancellationToken.None);

        Assert.False(work.Document.Pages[0].Usage!.IsAvailable);
    }
}
