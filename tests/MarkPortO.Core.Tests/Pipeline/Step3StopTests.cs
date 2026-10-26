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
using Microsoft.Extensions.Logging.Abstractions;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Pipeline;

/// <summary>
/// FR-044, FR-049, FR-054, FR-057, SC-026 to SC-029: Step 3 reads several pages at a time, in the right order, and stops
/// starting new readings on a usage limit, a login, repeated failures or a limit of pages or cost.
/// </summary>
public class Step3StopTests
{
    private sealed class Reader(Func<int, int, VlmResponse> answer, int delayMilliseconds = 0) : IVlmReader
    {
        private int _calls;
        private int _running;

        public int Calls => _calls;

        public int MostAtOnce { get; private set; }

        public async Task<VlmResponse> ReadAsync(string prompt, byte[] png, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var now = Interlocked.Increment(ref _running);
            lock (this)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
            }

            try
            {
                using var bitmap = new Bitmap(new MemoryStream(png));
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, cancellationToken);
                }

                return answer(call, bitmap.Height);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private sealed class NoEngine : IDocumentEngine
    {
        public EngineKind Kind => EngineKind.LibreOffice;

        public EngineAvailability CheckAvailability() => throw new NotSupportedException();

        public Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A document of <paramref name="pages"/> pages; the image of page n is 100 + n pixels high, so the reader can tell the pages apart.</summary>
    private static DocumentRecord Document(TempDir dir, int pages)
    {
        var folder = dir.Combine("out", "doc");
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        var records = new List<PageRecord>();
        for (var n = 1; n <= pages; n++)
        {
            using (var bitmap = new Bitmap(50, 100 + n))
            {
                using var stream = File.Create(Path.Combine(folder, "images", $"page-{n}.png"));
                bitmap.Save(stream, ImageFormat.Png);
            }

            records.Add(new PageRecord { Index = n, SourceLabel = $"page {n}", ImageFile = $"images/page-{n}.png" });
        }

        return new DocumentRecord
        {
            DocId = "d0001",
            SourcePath = dir.Combine("in", "a.docx"),
            OutputFolder = folder,
            Kind = DocumentKind.Docx,
            MarkdownLanguage = "en",
            Step1 = StepStatus.Done,
            Step2 = StepStatus.Done,
            Pages = records,
        };
    }

    private static JobContext Context(TempDir dir, DocumentRecord document, IVlmReader reader, JobState job, Step3Governor governor, int parallel = 1) =>
        new(job, new StateStore(dir.Combine("out")), new NoEngine(), NullLogger.Instance, TimeSpan.FromSeconds(1))
        {
            Vlm = reader,
            Prompts = new PromptSettings { Docx = "P" },
            Governor = governor,
            Step3Parallel = parallel,
        };

    private static JobState NewJob() => new() { JobId = Guid.NewGuid(), Documents = ["d0001"] };

    private static VlmResponse Ok(int height, double? cost = null) => new($"h{height}", new UsageInfo(10, 5, 15), CostUsd: cost);

    [Fact]
    public async Task WithSeveralReadingsAtOnce_ThePagesGetTheirOwnAnswers_AndNoMoreThanTheSettingRunAtOnce()
    {
        using var dir = new TempDir();
        var document = Document(dir, 9);
        var reader = new Reader((_, height) => Ok(height), delayMilliseconds: 60);
        var job = NewJob();
        var context = Context(dir, document, reader, job, new Step3Governor(), parallel: 3);

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.Equal(StepStatus.Done, document.Step3);
        Assert.InRange(reader.MostAtOnce, 2, 3);
        for (var n = 1; n <= 9; n++)
        {
            Assert.Equal($"h{100 + n}", document.Pages[n - 1].VlmText); // each page has the answer for its own image
        }
    }

    [Fact]
    public async Task TheMarkdown_IsTheSame_WhetherOneOrThreePagesAreReadAtOnce_SC027()
    {
        string Run(int parallel)
        {
            using var dir = new TempDir();
            var document = Document(dir, 8);
            var reader = new Reader((_, height) => Ok(height), delayMilliseconds: 20);
            var context = Context(dir, document, reader, NewJob(), new Step3Governor(), parallel);
            new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None).GetAwaiter().GetResult();
            return File.ReadAllText(Path.Combine(document.OutputFolder, "a.md")).Replace(dir.Path, "<dir>", StringComparison.Ordinal);
        }

        Assert.Equal(Run(1), Run(3));
    }

    [Fact]
    public async Task AUsageLimit_StopsNewReadings_LeavesThePagePending_AndInterruptsTheDocument_SC026()
    {
        using var dir = new TempDir();
        var document = Document(dir, 6);
        var reader = new Reader((call, height) => call == 3 ? throw new VlmException("usage limit reached", kind: VlmFailureKind.UsageLimit) : Ok(height));
        var governor = new Step3Governor();
        var context = Context(dir, document, reader, NewJob(), governor);

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.Equal(3, reader.Calls);
        Assert.Equal(Step3Stop.UsageLimit, governor.Stop);
        Assert.Equal(StepStatus.Interrupted, document.Step3);
        Assert.Equal(PageStep3Status.Done, document.Pages[1].Step3);
        Assert.All(document.Pages.Skip(2), p => Assert.Equal(PageStep3Status.Pending, p.Step3)); // the page that met the limit is read again on a resume
        Assert.Null(document.Pages[2].Error);
    }

    [Fact]
    public async Task AfterTheLimitIsLifted_AResumeReadsOnlyTheRemainingPages()
    {
        using var dir = new TempDir();
        var document = Document(dir, 6);
        var first = new Reader((call, height) => call == 3 ? throw new VlmException("usage limit reached", kind: VlmFailureKind.UsageLimit) : Ok(height));
        var job = NewJob();
        await new Step3Worker().ProcessAsync(new DocumentWork(document, Context(dir, document, first, job, new Step3Governor())), CancellationToken.None);

        var second = new Reader((_, height) => Ok(height));
        await new Step3Worker().ProcessAsync(new DocumentWork(document, Context(dir, document, second, job, new Step3Governor())), CancellationToken.None);

        Assert.Equal(4, second.Calls); // pages 3 to 6; pages 1 and 2 are not read again
        Assert.Equal(StepStatus.Done, document.Step3);
    }

    [Fact]
    public async Task ThreeFailuresInARow_StopTheJob_AndTheFailedPagesStayFailed_SC028()
    {
        using var dir = new TempDir();
        var document = Document(dir, 8);
        var reader = new Reader((_, _) => throw new VlmException("nothing could be read", kind: VlmFailureKind.BadOutput));
        var governor = new Step3Governor();
        var context = Context(dir, document, reader, NewJob(), governor);

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.Equal(3, reader.Calls);
        Assert.Equal(Step3Stop.RepeatedFailures, governor.Stop);
        Assert.Equal(StepStatus.Interrupted, document.Step3);
        Assert.All(document.Pages.Take(3), p => Assert.Equal(PageStep3Status.Failed, p.Step3));
        Assert.All(document.Pages.Skip(3), p => Assert.Equal(PageStep3Status.Pending, p.Step3));
    }

    [Fact]
    public async Task ASuccessBetweenTheFailures_KeepsTheJobGoing()
    {
        using var dir = new TempDir();
        var document = Document(dir, 8);
        var reader = new Reader((call, height) => call % 3 == 0 ? Ok(height) : throw new VlmException("odd", kind: VlmFailureKind.Unknown));
        var governor = new Step3Governor();
        var context = Context(dir, document, reader, NewJob(), governor);

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.Equal(8, reader.Calls);
        Assert.Equal(Step3Stop.None, governor.Stop);
        Assert.Equal(StepStatus.Done, document.Step3);
    }

    [Fact]
    public async Task ThePageLimit_StopsAfterThatManyPages_AndAResumeContinuesFromTheTotal_SC029()
    {
        using var dir = new TempDir();
        var document = Document(dir, 10);
        var job = NewJob();
        var first = new Reader((_, height) => Ok(height));
        await new Step3Worker().ProcessAsync(new DocumentWork(document, Context(dir, document, first, job, new Step3Governor(maxPages: 5))), CancellationToken.None);

        Assert.Equal(5, first.Calls);
        Assert.Equal(StepStatus.Interrupted, document.Step3);
        Assert.Equal(5, job.Step3Totals.Pages);

        // The limit is raised: the job goes on from the 5 pages it has read.
        var second = new Reader((_, height) => Ok(height));
        var resumed = new Step3Governor(maxPages: 8, pagesRead: job.Step3Totals.Pages, costUsd: job.Step3Totals.CostUsd);
        await new Step3Worker().ProcessAsync(new DocumentWork(document, Context(dir, document, second, job, resumed)), CancellationToken.None);

        Assert.Equal(3, second.Calls);
        Assert.Equal(8, job.Step3Totals.Pages);
    }

    [Fact]
    public async Task WithSeveralReadingsAtOnce_AtMostTheLimitPlusTheOthersInProgressAreRead_SC029()
    {
        using var dir = new TempDir();
        var document = Document(dir, 20);
        var reader = new Reader((_, height) => Ok(height), delayMilliseconds: 40);
        var governor = new Step3Governor(maxPages: 4);
        var context = Context(dir, document, reader, NewJob(), governor, parallel: 3);

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.InRange(governor.PagesRead, 4, 4 + 3 - 1);
        Assert.Equal(StepStatus.Interrupted, document.Step3);
    }

    [Fact]
    public async Task TheCostLimit_StopsOnceTheCostIsReached()
    {
        using var dir = new TempDir();
        var document = Document(dir, 10);
        var reader = new Reader((_, height) => Ok(height, cost: 0.04));
        var governor = new Step3Governor(maxCostUsd: 0.10);
        var context = Context(dir, document, reader, NewJob(), governor);

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.Equal(3, reader.Calls); // 0.04, 0.08, 0.12
        Assert.Equal(Step3Stop.CostLimit, governor.Stop);
    }

    [Fact]
    public async Task WithoutALimit_EveryPageIsRead()
    {
        using var dir = new TempDir();
        var document = Document(dir, 12);
        var reader = new Reader((_, height) => Ok(height));
        var context = Context(dir, document, reader, NewJob(), new Step3Governor());

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.Equal(12, reader.Calls);
        Assert.Equal(StepStatus.Done, document.Step3);
    }

    [Fact]
    public async Task TheCostOfEveryPage_AndOfTheDocument_IsKeptAndWrittenToTheMarkdown_FR054()
    {
        using var dir = new TempDir();
        var document = Document(dir, 3);
        var reader = new Reader((_, height) => Ok(height, cost: 0.04));
        var context = Context(dir, document, reader, NewJob(), new Step3Governor());

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.All(document.Pages, p => Assert.Equal(0.04, p.CostUsd));
        Assert.Equal(0.12, document.CostTotalUsd!.Value, 6);
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "a.md"));
        Assert.Contains("cost=0.04 -->", markdown);
        Assert.Contains("**VLM cost**: 0.12 US dollars in total", markdown);
    }

    [Fact]
    public async Task WhenNoCostIsReported_NoCostIsWritten()
    {
        using var dir = new TempDir();
        var document = Document(dir, 2);
        var reader = new Reader((_, height) => Ok(height));
        var context = Context(dir, document, reader, NewJob(), new Step3Governor());

        await new Step3Worker().ProcessAsync(new DocumentWork(document, context), CancellationToken.None);

        Assert.Null(document.CostTotalUsd);
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "a.md"));
        Assert.DoesNotContain("cost", markdown);
    }
}
