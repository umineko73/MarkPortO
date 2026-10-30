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

public class PreviousRunFinderTests
{
    private static JobState Save(StateStore store, JobStatus status, DateTime updated, params DocumentRecord[] documents)
    {
        var job = new JobState
        {
            JobId = Guid.NewGuid(),
            CreatedAt = updated,
            UpdatedAt = updated,
            Status = status,
            Inputs = [@"D:\in"],
            OutputRoot = store.OutRoot,
            Documents = [.. documents.Select(d => d.DocId)],
        };
        store.SaveJob(job, touch: false);
        foreach (var document in documents)
        {
            store.SaveDocument(job.JobId, document);
        }

        return job;
    }

    private static DocumentRecord Doc(string id, StepStatus step1, PageStep3Status page = PageStep3Status.Done) => new()
    {
        DocId = id,
        SourcePath = $@"D:\in\{id}.docx",
        Kind = DocumentKind.Docx,
        Step1 = step1,
        Pages = [new PageRecord { Index = 1, Step3 = page }],
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Z:
o\sucholder")]
    public void NoOutputFolder_HasNoPreviousRun(string? folder) => Assert.Null(PreviousRunFinder.Find(folder));

    [Fact]
    public void AnOutputFolderWithoutJobs_HasNoPreviousRun()
    {
        using var dir = new TempDir();

        Assert.Null(PreviousRunFinder.Find(dir.Path));
    }

    [Fact]
    public void AnInterruptedJob_CanBeResumed_AndItsFilesAreShown()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = Save(store, JobStatus.Interrupted, DateTime.UtcNow, Doc("d0001", StepStatus.Done), Doc("d0002", StepStatus.Interrupted));

        var run = PreviousRunFinder.Find(dir.Path)!;

        Assert.Equal(job.JobId, run.Job.JobId);
        Assert.Equal(2, run.Documents.Count);
        Assert.True(run.CanResume);
        Assert.False(run.CanRetryFailed);
    }

    [Fact]
    public void AFinishedJobWithAFailedPage_CanOnlyRetryTheFailure()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        Save(store, JobStatus.CompletedWithErrors, DateTime.UtcNow, Doc("d0001", StepStatus.Done, PageStep3Status.Failed));

        var run = PreviousRunFinder.Find(dir.Path)!;

        Assert.False(run.CanResume);
        Assert.True(run.CanRetryFailed);
    }

    [Fact]
    public void AJobThatFinishedWithoutFailures_IsNotOffered()
    {
        using var dir = new TempDir();
        Save(new StateStore(dir.Path), JobStatus.Completed, DateTime.UtcNow, Doc("d0001", StepStatus.Done));

        Assert.Null(PreviousRunFinder.Find(dir.Path));
    }

    [Fact]
    public void TheNewestJobIsShown_WhateverTheInputs()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        Save(store, JobStatus.Interrupted, DateTime.UtcNow.AddHours(-2), Doc("d0001", StepStatus.Interrupted));
        var newer = Save(store, JobStatus.Interrupted, DateTime.UtcNow, Doc("d0001", StepStatus.Interrupted));

        Assert.Equal(newer.JobId, PreviousRunFinder.Find(dir.Path)!.Job.JobId);
    }

    [Fact]
    public void AJobThatAnotherProcessIsRunning_IsNotOffered()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = Save(store, JobStatus.Running, DateTime.UtcNow, Doc("d0001", StepStatus.Running));
        using var held = store.TryAcquireLock(job.JobId);

        Assert.Null(PreviousRunFinder.Find(dir.Path));
    }

    [Fact]
    public void AJobWhoseStateIsPartlyMissing_IsNotOffered()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = Save(store, JobStatus.Interrupted, DateTime.UtcNow, Doc("d0001", StepStatus.Interrupted));
        job.Documents.Add("d0002"); // no state file for it
        store.SaveJob(job, touch: false);

        Assert.Null(PreviousRunFinder.Find(dir.Path));
    }

    [Fact]
    public void AnInterruptedJobComesBeforeANewerJobThatFinishedWithFailures()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var interrupted = Save(store, JobStatus.Interrupted, DateTime.UtcNow.AddHours(-1), Doc("d0001", StepStatus.Interrupted));
        Save(store, JobStatus.CompletedWithErrors, DateTime.UtcNow, Doc("d0001", StepStatus.Done, PageStep3Status.Failed));

        var run = PreviousRunFinder.Find(dir.Path)!;

        Assert.Equal(interrupted.JobId, run.Job.JobId);
        Assert.True(run.CanResume);
    }

    [Theory]
    [InlineData(JobStatus.Interrupted)]
    [InlineData(JobStatus.CompletedWithErrors)]
    public void AnEarlierJobThatFailedOrWasInterrupted_IsNotOffered_WhenTheNewestJobSucceeded(JobStatus earlier)
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        Save(store, earlier, DateTime.UtcNow.AddHours(-1), Doc("d0001", StepStatus.Interrupted, PageStep3Status.Failed));
        Save(store, JobStatus.Completed, DateTime.UtcNow, Doc("d0001", StepStatus.Done));

        Assert.Null(PreviousRunFinder.Find(dir.Path));
    }

    [Fact]
    public void AnAbandonedJob_IsNotOffered_AndItsStateStays()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = Save(store, JobStatus.Interrupted, DateTime.UtcNow, Doc("d0001", StepStatus.Interrupted));

        store.Abandon([job.JobId]);

        Assert.Null(PreviousRunFinder.Find(dir.Path));
        Assert.Equal(JobStatus.Abandoned, store.LoadJob(job.JobId)!.Status);
        Assert.NotNull(store.LoadDocument(job.JobId, "d0001"));
    }

    [Fact]
    public void Abandon_LeavesAJobThatFinishedAlone()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = Save(store, JobStatus.CompletedWithErrors, DateTime.UtcNow, Doc("d0001", StepStatus.Done, PageStep3Status.Failed));

        store.Abandon([job.JobId]);

        Assert.Equal(JobStatus.CompletedWithErrors, store.LoadJob(job.JobId)!.Status);
    }
}
