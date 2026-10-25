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

using System.Diagnostics;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Pipeline;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Tests.Localization;
using static MarkPort.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPort.Core.Tests.Pipeline;

public class FileStatusProjectorTests : JapaneseUiTests
{
    private static DocumentRecord Doc(StepStatus s1, StepStatus s2 = StepStatus.NotApplicable, StepStatus s3 = StepStatus.NotApplicable) => new()
    {
        DocId = "d0001",
        SourcePath = @"D:\in\報告書.docx",
        Kind = DocumentKind.Docx,
        Step1 = s1,
        Step2 = s2,
        Step3 = s3,
    };

    [Theory]
    [InlineData(StepStatus.Pending, StepStatus.Pending, StepStatus.NotApplicable, FileDisplayState.Waiting, "待機中")]
    [InlineData(StepStatus.Running, StepStatus.Pending, StepStatus.NotApplicable, FileDisplayState.Step1Running, "Step1 実行中")]
    [InlineData(StepStatus.Done, StepStatus.Running, StepStatus.NotApplicable, FileDisplayState.Step2Running, "Step2 実行中")]
    [InlineData(StepStatus.Done, StepStatus.Done, StepStatus.Running, FileDisplayState.Step3Running, "Step3 実行中")]
    [InlineData(StepStatus.Done, StepStatus.Done, StepStatus.Done, FileDisplayState.Done, "完了")]
    [InlineData(StepStatus.Done, StepStatus.NotApplicable, StepStatus.NotApplicable, FileDisplayState.Done, "完了")]
    [InlineData(StepStatus.Failed, StepStatus.Pending, StepStatus.NotApplicable, FileDisplayState.Failed, "失敗")]
    [InlineData(StepStatus.Done, StepStatus.Failed, StepStatus.NotApplicable, FileDisplayState.Failed, "失敗")]
    [InlineData(StepStatus.Interrupted, StepStatus.Pending, StepStatus.NotApplicable, FileDisplayState.Interrupted, "中断")]
    [InlineData(StepStatus.Done, StepStatus.Interrupted, StepStatus.NotApplicable, FileDisplayState.Interrupted, "中断")]
    public void TheStateOfAFile_IsOneOfTheSevenStatesOfTheList(StepStatus s1, StepStatus s2, StepStatus s3, FileDisplayState expected, string text)
    {
        var row = FileStatusProjector.Project(Doc(s1, s2, s3));

        Assert.Equal(expected, row.State);
        Assert.Equal(text, FileStatusProjector.Text(row.State));
    }

    [Fact]
    public void AStepThatIsNotSelected_IsShownAsNotApplicable()
    {
        var row = FileStatusProjector.Project(Doc(StepStatus.Done, StepStatus.NotApplicable, StepStatus.NotApplicable));

        Assert.Equal([StepStatus.Done, StepStatus.NotApplicable, StepStatus.NotApplicable], row.StepStates);
        Assert.Equal("対象外", FileStatusProjector.StepText(StepStatus.NotApplicable));
        Assert.Equal("実行中", FileStatusProjector.StepText(StepStatus.Running));
    }

    [Fact]
    public void AFailedFile_ShowsTheReason()
    {
        var document = Doc(StepStatus.Failed);
        document.Error = new DocumentError(1, "The file cannot be opened as a .docx document.");

        var row = FileStatusProjector.Project(document);

        Assert.Equal("The file cannot be opened as a .docx document.", row.Message);
        Assert.Equal("報告書.docx", row.FileName);
    }

    [Fact]
    public void ARestartedFile_ShowsThatItStartedOver()
    {
        var document = Doc(StepStatus.Pending);
        document.Warnings.Add(new Warning("RestartedInputChanged", "The input file changed since the earlier run."));

        Assert.Contains("変更された", FileStatusProjector.Project(document).Message);
    }

    [Fact]
    public void PagesAndDeletedPages_AreCounted_AndListedInTheDetail()
    {
        var document = Doc(StepStatus.Done, StepStatus.Done);
        document.Pages =
        [
            new PageRecord { Index = 1, SourceLabel = "1ページ目" },
            new PageRecord { Index = 2, SourceLabel = "2ページ目", IsDeleted = true },
        ];

        var row = FileStatusProjector.Project(document);

        Assert.Equal((2, 1), (row.PageCount, row.DeletedPageCount));
        Assert.Equal([false, true], row.Pages.Select(p => p.IsDeleted));
        Assert.Equal(string.Empty, row.UsageText); // step 3 is not used
    }

    [Fact]
    public void VlmUsage_IsShownPerPageAndAsATotal_AndIsUnavailableWhenTheServerDoesNotSayIt()
    {
        var document = Doc(StepStatus.Done, StepStatus.Done, StepStatus.Done);
        document.Pages =
        [
            new PageRecord { Index = 1, Step3 = PageStep3Status.Done, Usage = new UsageInfo(100, 10, 110) },
            new PageRecord { Index = 2, Step3 = PageStep3Status.Done, Usage = new UsageInfo(200, 20, 220) },
            new PageRecord { Index = 3, Step3 = PageStep3Status.Done, Usage = UsageInfo.Unavailable },
            new PageRecord { Index = 4, IsDeleted = true, Step3 = PageStep3Status.Skipped },
            new PageRecord { Index = 5, Step3 = PageStep3Status.Failed },
        ];

        var row = FileStatusProjector.Project(document);

        Assert.Equal("prompt=300 completion=30 total=330", row.UsageText); // only the pages that reported it
        Assert.Equal(["prompt=100 completion=10 total=110", "prompt=200 completion=20 total=220", "取得不可", string.Empty, string.Empty], row.Pages.Select(p => p.UsageText));
        Assert.Equal(["完了", "完了", "完了", "対象外", "失敗"], row.Pages.Select(p => p.VlmState));
    }

    [Fact]
    public void WhenNoPageReportedUsage_TheFileTotalIsUnavailable()
    {
        var document = Doc(StepStatus.Done, StepStatus.Done, StepStatus.Done);
        document.Pages = [new PageRecord { Index = 1, Step3 = PageStep3Status.Done, Usage = UsageInfo.Unavailable }];

        Assert.Equal("取得不可", FileStatusProjector.Project(document).UsageText);
    }

    [Fact]
    public void TheSavedTotal_IsUsedWhenThereIsOne()
    {
        var document = Doc(StepStatus.Done, StepStatus.Done, StepStatus.Done);
        document.UsageTotal = new UsageInfo(5, 6, 11);
        document.Pages = [new PageRecord { Index = 1, Step3 = PageStep3Status.Done, Usage = new UsageInfo(1, 1, 2) }];

        Assert.Equal("prompt=5 completion=6 total=11", FileStatusProjector.Project(document).UsageText);
    }

    // ---- the feed: updates are batched, and arrive well inside the 2 seconds of SC-011 ----

    [Fact]
    public void TheFeed_BatchesManyChanges_AndKeepsTheLatestStateOfEachFile()
    {
        using var feed = new FileStatusFeed(TimeSpan.FromMilliseconds(100));
        var batches = new List<IReadOnlyList<FileRow>>();
        feed.Updated += rows => { lock (batches) { batches.Add(rows); } };

        var a = Doc(StepStatus.Pending);
        a.DocId = "dA";
        var b = Doc(StepStatus.Pending);
        b.DocId = "dB";
        for (var i = 0; i < 200; i++)
        {
            a.Step1 = i % 2 == 0 ? StepStatus.Running : StepStatus.Done;
            feed.Post(a);
            feed.Post(b);
        }

        a.Step1 = StepStatus.Done;
        feed.Post(a);
        feed.Flush();

        IReadOnlyList<FileRow> last;
        lock (batches)
        {
            Assert.InRange(batches.Count, 1, 3);
            last = batches[^1];
        }

        Assert.Equal(FileDisplayState.Waiting, batches.SelectMany(x => x).Last(r => r.DocId == "dB").State);
        Assert.Equal(StepStatus.Done, batches.SelectMany(x => x).Last(r => r.DocId == "dA").StepStates[0]);
        Assert.NotEmpty(last);
    }

    [Fact]
    public void TheFeed_DeliversAChange_WithinTheTwoSecondsOfTheRequirement()
    {
        using var feed = new FileStatusFeed(); // the default interval
        var arrived = new ManualResetEventSlim();
        feed.Updated += _ => arrived.Set();
        var document = Doc(StepStatus.Running);
        var watch = Stopwatch.StartNew();

        feed.Post(document);

        Assert.True(arrived.Wait(TimeSpan.FromSeconds(2)), "no update within 2 seconds");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public void TheFeed_SaysNothingWhenNothingChanged()
    {
        using var feed = new FileStatusFeed(TimeSpan.FromMilliseconds(50));
        var count = 0;
        feed.Updated += _ => Interlocked.Increment(ref count);

        Thread.Sleep(300);

        Assert.Equal(0, count);
    }

    [Fact]
    public void TheFeed_FlushesWhatIsPending_WhenItIsDisposed()
    {
        var feed = new FileStatusFeed(TimeSpan.FromMinutes(10));
        var received = new List<FileRow>();
        feed.Updated += rows => received.AddRange(rows);
        feed.Post(Doc(StepStatus.Done, StepStatus.Done));

        feed.Dispose();

        Assert.Equal(FileDisplayState.Done, Assert.Single(received).State);
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task ARealRun_IsFollowedThroughTheFeed_ToTheFinalState()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["text"], ["more"]);
        using var engine = Engine(dir);
        var service = new MarkPort.Core.Convert.ConvertService(engine);
        var seen = new List<FileRow>();
        using (var feed = new FileStatusFeed(TimeSpan.FromMilliseconds(50)))
        {
            feed.Updated += rows => { lock (seen) { seen.AddRange(rows); } };
            service.DocumentChanged += feed.Post;

            await service.RunAsync(Request(dir.Combine("out"), Options(), source));
        } // disposing hands on the last state

        lock (seen)
        {
            Assert.Equal(FileDisplayState.Done, seen[^1].State);
            Assert.Equal(2, seen[^1].PageCount);
        }
    }
}
