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

using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Jobs;

/// <summary>FR-121: the runs of an output folder that can be resumed, also the ones that were given up.</summary>
public class ResumableRunsTests
{
    private static JobState Save(StateStore store, JobStatus status, DateTime updated, StepStatus step1 = StepStatus.Pending)
    {
        var document = new DocumentRecord { DocId = "d0001", SourcePath = @"C:\in\a.docx", Step1 = step1 };
        var job = new JobState { JobId = Guid.NewGuid(), Status = status, Documents = [document.DocId], UpdatedAt = updated };
        Directory.CreateDirectory(store.JobDirectory(job.JobId));
        store.SaveJob(job, touch: false);
        store.SaveDocument(job.JobId, document);
        return job;
    }

    [Fact]
    public void Resumable_ListsInterruptedAndGivenUpRuns_NewestFirst_AndLeavesOutFinishedOnes()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var now = DateTime.UtcNow;
        var old = Save(store, JobStatus.Abandoned, now.AddHours(-3));
        var interrupted = Save(store, JobStatus.Interrupted, now.AddHours(-1));
        Save(store, JobStatus.Completed, now.AddHours(-2), StepStatus.Done);

        var runs = PreviousRunFinder.Resumable(dir.Path);

        Assert.Equal([interrupted.JobId, old.JobId], runs.Select(r => r.Job.JobId));
        Assert.All(runs, r => Assert.True(r.CanResume));
    }

    [Fact]
    public void Resumable_ListsAFinishedRunWithFailures_ForRetry()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var failed = Save(store, JobStatus.CompletedWithErrors, DateTime.UtcNow, StepStatus.Failed);

        var run = Assert.Single(PreviousRunFinder.Resumable(dir.Path));
        Assert.Equal(failed.JobId, run.Job.JobId);
        Assert.False(run.CanResume);
        Assert.True(run.CanRetryFailed);
    }

    [Fact]
    public void Resumable_LeavesOutARunWhoseStateIsMissing_AndAnEmptyFolder()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = Save(store, JobStatus.Interrupted, DateTime.UtcNow);
        File.Delete(Path.Combine(store.JobDirectory(job.JobId), "docs", "d0001.json"));

        Assert.Empty(PreviousRunFinder.Resumable(dir.Path));
        Assert.Empty(PreviousRunFinder.Resumable(dir.Combine("none")));
    }

    [Fact]
    public void IsResumable_AGivenUpRunOnlyWhenAskedFor_SoTheSearchForTheEarlierRunLeavesItOut()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = Save(store, JobStatus.Abandoned, DateTime.UtcNow);

        Assert.False(store.IsResumable(job));
        Assert.True(store.IsResumable(job, includeAbandoned: true));
        Assert.Empty(store.FindResumableJobs());
        Assert.Null(PreviousRunFinder.Find(dir.Path));
    }
}
