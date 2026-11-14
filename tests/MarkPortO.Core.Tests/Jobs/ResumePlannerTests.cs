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

using MarkPortO.Core.Convert;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Jobs;

public class ResumePlannerTests
{
    /// <summary>A document whose input file exists and whose saved fingerprint matches it.</summary>
    private static DocumentRecord Doc(TempDir dir, string name = "a.docx", StepStatus s1 = StepStatus.Done, StepStatus s2 = StepStatus.Done, StepStatus s3 = StepStatus.NotApplicable)
    {
        var source = dir.Combine("in", name);
        DocxFactory.CreatePaged(source, ["text"]);
        return new DocumentRecord
        {
            DocId = "d0001",
            SourcePath = source,
            Kind = DocumentKind.Docx,
            Fingerprint = StateStore.ComputeFingerprint(source),
            OutputFolder = dir.Combine("out", Path.GetFileNameWithoutExtension(name)),
            Step1 = s1,
            Step2 = s2,
            Step3 = s3,
        };
    }

    private static ResumeDecision PlanOne(DocumentRecord document, ResumeMode mode = ResumeMode.Unfinished, bool step2 = true, bool step3 = false) =>
        Assert.Single(ResumePlanner.Plan([document], mode, step2, step3));

    [Fact]
    public void Reread_MarkedPagesBecomePending_AndOnlyTheirFileRuns_FR139()
    {
        using var dir = new TempDir();
        var marked = Doc(dir, "a.docx", s3: StepStatus.Done);
        marked.Pages =
        [
            new PageRecord { Index = 1, Step3 = PageStep3Status.Done, VlmText = "one" },
            new PageRecord { Index = 2, Step3 = PageStep3Status.Done, VlmText = "two", RereadMark = true },
            new PageRecord { Index = 3, Step3 = PageStep3Status.Done, VlmText = "three", IsDeleted = true, RereadMark = true }, // a deleted page is not read
        ];
        var plain = Doc(dir, "b.docx", s3: StepStatus.Done);
        plain.Pages = [new PageRecord { Index = 1, Step3 = PageStep3Status.Done, VlmText = "x" }];

        var decisions = ResumePlanner.Plan([marked, plain], ResumeMode.Reread, step2: true, step3: true);

        Assert.Equal([ResumeAction.Run, ResumeAction.Skip], decisions.Select(d => d.Action));
        Assert.Equal(StepStatus.Pending, marked.Step3);
        Assert.Equal([PageStep3Status.Done, PageStep3Status.Pending, PageStep3Status.Done], marked.Pages.Select(p => p.Step3));
        Assert.Equal("two", marked.Pages[1].VlmText); // the earlier text stays until the page is read
        Assert.Equal(StepStatus.Done, plain.Step3);
    }

    [Fact]
    public void RereadMarks_CountOnlyThePagesThatTheVlmReads_AndSetNeedsTheSavedState()
    {
        using var dir = new TempDir();
        var document = Doc(dir, s3: StepStatus.Done);
        document.Pages =
        [
            new PageRecord { Index = 1, Step3 = PageStep3Status.Done },
            new PageRecord { Index = 2, Step3 = PageStep3Status.Failed },
            new PageRecord { Index = 3, IsBlank = true },
        ];
        var store = new StateStore(dir.Combine("out"));
        var job = new JobState { JobId = Guid.NewGuid() };
        store.SaveJob(job);
        store.SaveDocument(job.JobId, document);

        Assert.Equal((0, 2), RereadMarks.Count(document));
        Assert.True(RereadMarks.MarkFailed(store, document));
        Assert.Equal((1, 2), RereadMarks.Count(document));
        Assert.True(RereadMarks.SetAll(store, document, marked: true));
        Assert.Equal((2, 2), RereadMarks.Count(document));
        Assert.False(document.Pages[2].RereadMark); // a blank page cannot be marked

        // The marks are in the saved state.
        Assert.Equal([true, true, false], store.LoadDocument(job.JobId, document.DocId)!.Pages.Select(p => p.RereadMark));

        // A document that is not in the state of the store is left alone.
        var other = Doc(dir, "c.docx", s3: StepStatus.Done);
        other.Pages = [new PageRecord { Index = 1, Step3 = PageStep3Status.Done }];
        Assert.False(RereadMarks.SetAll(store, other, marked: true));
        Assert.False(other.Pages[0].RereadMark);
    }

    [Fact]
    public void AFinishedDocument_IsSkipped()
    {
        using var dir = new TempDir();
        var doc = Doc(dir);

        Assert.Equal(ResumeAction.Skip, PlanOne(doc).Action);
        Assert.Equal(StepStatus.Done, doc.Step1);
        Assert.Equal(StepStatus.Done, doc.Step2);
    }

    [Fact]
    public void AnInterruptedDocument_IsRunAgain()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s1: StepStatus.Interrupted, s2: StepStatus.Pending);

        Assert.Equal(ResumeAction.Run, PlanOne(doc).Action);
        Assert.Equal(StepStatus.Pending, doc.Step1);
    }

    [Fact]
    public void AStepLeftRunning_ByACrash_IsTreatedAsInterrupted()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s1: StepStatus.Running, s2: StepStatus.Pending);

        Assert.Equal(ResumeAction.Run, PlanOne(doc).Action);
        Assert.Equal(StepStatus.Pending, doc.Step1);
    }

    [Fact]
    public void FinishedStepsAreNotDoneAgain_OnlyTheRestRuns()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s1: StepStatus.Done, s2: StepStatus.Interrupted);
        doc.Pages = [new PageRecord { Index = 1, ImageFile = "images/page-001.png" }];

        var decision = PlanOne(doc);

        Assert.Equal(ResumeAction.Run, decision.Action);
        Assert.Equal(StepStatus.Done, doc.Step1); // step 1 is kept
        Assert.Equal(StepStatus.Pending, doc.Step2);
        Assert.Single(doc.Pages); // the pages are kept
    }

    [Fact]
    public void WhenTheInputChanged_TheEarlierResultIsDiscarded_AndTheFileStartsOver()
    {
        using var dir = new TempDir();
        var doc = Doc(dir);
        doc.Pages = [new PageRecord { Index = 1 }];
        doc.Blocks = [new Block { Markdown = "old" }];
        Directory.CreateDirectory(Path.Combine(doc.OutputFolder, "images"));
        File.WriteAllText(Path.Combine(doc.OutputFolder, "a.md"), "old markdown");
        File.WriteAllText(Path.Combine(doc.OutputFolder, "images", "page-001.png"), "old image");
        File.WriteAllText(Path.Combine(doc.OutputFolder, "notes-by-the-user.txt"), "keep me");
        DocxFactory.CreatePaged(doc.SourcePath, ["changed text, now longer than before"]);

        var decision = PlanOne(doc);

        Assert.Equal(ResumeAction.Restart, decision.Action);
        Assert.Equal([StepStatus.Pending, StepStatus.Pending], [doc.Step1, doc.Step2]);
        Assert.Empty(doc.Pages);
        Assert.Empty(doc.Blocks);
        Assert.Contains(ResumePlanner.RestartedCode, doc.Warnings.Select(w => w.Code));
        Assert.False(File.Exists(Path.Combine(doc.OutputFolder, "a.md")));
        Assert.False(File.Exists(Path.Combine(doc.OutputFolder, "images", "page-001.png")));
        Assert.True(File.Exists(Path.Combine(doc.OutputFolder, "notes-by-the-user.txt"))); // only this job's own files go
        Assert.False(StateStore.HasChanged(doc.Fingerprint, doc.SourcePath)); // the new state of the file is remembered
    }

    [Fact]
    public void WhenTheInputIsGone_TheDocumentFails_WithAReason()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s1: StepStatus.Interrupted, s2: StepStatus.Pending);
        File.Delete(doc.SourcePath);

        var decision = PlanOne(doc);

        Assert.Equal(ResumeAction.Fail, decision.Action);
        Assert.Equal(StepStatus.Failed, doc.Step1);
        Assert.Contains("not found", doc.Error!.Message);
    }

    [Fact]
    public void AFailedFile_IsLeftAlone_ByAPlainResume()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s1: StepStatus.Failed, s2: StepStatus.Pending);
        doc.Error = new DocumentError(1, "broken");

        Assert.Equal(ResumeAction.Skip, PlanOne(doc).Action);
        Assert.Equal(StepStatus.Failed, doc.Step1);
    }

    [Fact]
    public void RetryFailed_RunsOnlyTheFailedFiles_AndClearsTheirError()
    {
        using var dir = new TempDir();
        var failed = Doc(dir, "failed.docx", s1: StepStatus.Failed, s2: StepStatus.Pending);
        failed.Error = new DocumentError(1, "broken");
        var finished = Doc(dir, "finished.docx");
        var unfinished = Doc(dir, "unfinished.docx", s1: StepStatus.Interrupted, s2: StepStatus.Pending);

        var decisions = ResumePlanner.Plan([failed, finished, unfinished], ResumeMode.RetryFailed, step2: true, step3: false);

        Assert.Equal([ResumeAction.Run, ResumeAction.Skip, ResumeAction.Skip], decisions.Select(d => d.Action));
        Assert.Equal(StepStatus.Pending, failed.Step1);
        Assert.Null(failed.Error);
    }

    [Fact]
    public void RetryFailed_AFailedFileWhoseInputWasFixed_StartsOver()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s1: StepStatus.Failed, s2: StepStatus.Pending);
        doc.Error = new DocumentError(1, "broken");
        DocxFactory.CreatePaged(doc.SourcePath, ["the file was replaced by a longer valid one"]);

        var decision = PlanOne(doc, ResumeMode.RetryFailed);

        Assert.Equal(ResumeAction.Restart, decision.Action);
        Assert.Null(doc.Error);
    }

    [Fact]
    public void ATurnedOnStep_IsAddedToFinishedFiles_WithoutRepeatingTheFinishedOnes()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s3: StepStatus.NotApplicable); // the earlier run had no step 3

        var decision = PlanOne(doc, step3: true);

        Assert.Equal(ResumeAction.Run, decision.Action);
        Assert.Equal([StepStatus.Done, StepStatus.Done, StepStatus.Pending], [doc.Step1, doc.Step2, doc.Step3]);
    }

    [Fact]
    public void ATurnedOffStep_BecomesNotApplicable_AndTheFileCountsAsFinished()
    {
        using var dir = new TempDir();
        var doc = Doc(dir);

        var decision = PlanOne(doc, step2: false);

        Assert.Equal(ResumeAction.Skip, decision.Action);
        Assert.Equal(StepStatus.NotApplicable, doc.Step2);
        Assert.Equal(StepStatus.Done, doc.Step1);
    }

    [Fact]
    public void ATurnedOnStep_ForAnUnfinishedFile_JustStartsPending()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s1: StepStatus.Interrupted, s2: StepStatus.NotApplicable);

        var decision = PlanOne(doc, step2: true);

        Assert.Equal(ResumeAction.Run, decision.Action);
        Assert.Equal(StepStatus.Pending, doc.Step2);
    }

    [Fact]
    public void TheOutputFolder_IsNeverChanged()
    {
        using var dir = new TempDir();
        var doc = Doc(dir, s1: StepStatus.Interrupted, s2: StepStatus.Pending);
        var folder = doc.OutputFolder;

        PlanOne(doc);

        Assert.Equal(folder, doc.OutputFolder);
    }

    [Fact]
    public void ResumeRequest_DefaultsToResumingUnfinishedFiles()
    {
        var request = new ResumeRequest { OutputFolder = @"D:\out" };

        Assert.Equal(ResumeMode.Unfinished, request.Mode);
        Assert.Null(request.Step2);
        Assert.Null(request.Step3);
        Assert.Empty(request.GivenInputs);
    }
}
