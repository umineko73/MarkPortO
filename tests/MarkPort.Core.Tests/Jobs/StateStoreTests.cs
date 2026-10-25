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

using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.Jobs;

public class StateStoreTests
{
    private static JobState NewJob(JobStatus status, DateTime updated, params string[] inputs) => new()
    {
        JobId = Guid.NewGuid(),
        CreatedAt = updated,
        UpdatedAt = updated,
        Status = status,
        Inputs = [.. inputs],
        OutputRoot = @"D:\out",
    };

    [Fact]
    public void SaveJobAndDocument_RoundTripUnderTheJobFolder()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = NewJob(JobStatus.Running, DateTime.UtcNow, @"D:\in");
        store.SaveJob(job);
        var doc = new DocumentRecord
        {
            DocId = "d0001",
            SourcePath = @"D:\in\a.docx",
            Kind = DocumentKind.Docx,
            Step1 = StepStatus.Done,
            Pages = [new PageRecord { Index = 1, ImageFile = "images/page-001.png" }],
        };
        store.SaveDocument(job.JobId, doc);

        Assert.True(File.Exists(Path.Combine(dir.Path, ".markport", "jobs", job.JobId.ToString("D"), "job.json")));
        Assert.True(File.Exists(Path.Combine(dir.Path, ".markport", "jobs", job.JobId.ToString("D"), "docs", "d0001.json")));
        Assert.Equal(job.JobId, store.LoadJob(job.JobId)!.JobId);
        var back = store.LoadDocument(job.JobId, "d0001")!;
        Assert.Equal(StepStatus.Done, back.Step1);
        Assert.Equal("images/page-001.png", back.Pages[0].ImageFile);
    }

    [Fact]
    public void SaveJob_TouchesUpdatedAt_UnlessAsked()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var job = NewJob(JobStatus.Running, old);

        store.SaveJob(job, touch: false);
        Assert.Equal(old, store.LoadJob(job.JobId)!.UpdatedAt);

        store.SaveJob(job);
        Assert.True(store.LoadJob(job.JobId)!.UpdatedAt > old);
    }

    [Fact]
    public void TwoJobsInTheSameOutputFolderDoNotCollide()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var a = NewJob(JobStatus.Completed, DateTime.UtcNow, @"D:\in\a.docx");
        var b = NewJob(JobStatus.Interrupted, DateTime.UtcNow, @"D:\in\b.docx");
        store.SaveJob(a);
        store.SaveJob(b);

        Assert.Equal(2, store.ListJobs().Count);
        Assert.Equal(@"D:\in\a.docx", store.LoadJob(a.JobId)!.Inputs[0]);
        Assert.Equal(@"D:\in\b.docx", store.LoadJob(b.JobId)!.Inputs[0]);
    }

    [Fact]
    public void FindResumableJobs_ReturnsInterruptedJobs_NewestFirst_AndSkipsFinishedOnes()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var t = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        var oldest = NewJob(JobStatus.Interrupted, t, @"D:\in");
        var newest = NewJob(JobStatus.Interrupted, t.AddHours(2), @"D:\in");
        var done = NewJob(JobStatus.Completed, t.AddHours(3), @"D:\in");
        foreach (var job in new[] { oldest, newest, done })
        {
            store.SaveJob(job, touch: false);
        }

        var found = store.FindResumableJobs();

        Assert.Equal([newest.JobId, oldest.JobId], found.Select(j => j.JobId));
    }

    [Fact]
    public void FindResumableJobs_FiltersByInputs_IgnoringCaseAndOrder()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var t = DateTime.UtcNow;
        var a = NewJob(JobStatus.Interrupted, t, @"D:\in\A.docx", @"D:\in\b.docx");
        var other = NewJob(JobStatus.Interrupted, t.AddMinutes(1), @"D:\other");
        store.SaveJob(a, touch: false);
        store.SaveJob(other, touch: false);

        var found = store.FindResumableJobs([@"d:\in\b.docx", @"D:\IN\a.docx"]);

        Assert.Equal(a.JobId, Assert.Single(found).JobId);
    }

    [Fact]
    public void FindResumableJobs_CanIncludeFinishedJobsWithErrors_ForRetryFailed()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = NewJob(JobStatus.CompletedWithErrors, DateTime.UtcNow, @"D:\in");
        store.SaveJob(job, touch: false);

        Assert.Empty(store.FindResumableJobs());
        Assert.Single(store.FindResumableJobs(includeFinishedWithErrors: true));
    }

    [Fact]
    public void RunningJob_WithoutALiveLock_IsTreatedAsInterrupted()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = NewJob(JobStatus.Running, DateTime.UtcNow, @"D:\in");
        store.SaveJob(job, touch: false);

        Assert.Single(store.FindResumableJobs()); // the process that ran it is gone
    }

    [Fact]
    public void RunningJob_WithALiveLock_IsNotResumable()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var job = NewJob(JobStatus.Running, DateTime.UtcNow, @"D:\in");
        store.SaveJob(job, touch: false);

        using var held = store.TryAcquireLock(job.JobId);

        Assert.NotNull(held);
        Assert.True(store.IsLocked(job.JobId));
        Assert.Empty(store.FindResumableJobs());
    }

    [Fact]
    public void Lock_IsExclusive_AndReleasedOnDispose()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.Path);
        var id = Guid.NewGuid();

        var first = store.TryAcquireLock(id);
        Assert.NotNull(first);
        Assert.Null(store.TryAcquireLock(id)); // a second process cannot run the same job

        first.Dispose();

        Assert.False(store.IsLocked(id));
        using var again = store.TryAcquireLock(id);
        Assert.NotNull(again);
    }

    [Fact]
    public void Fingerprint_DetectsSizeAndTimeChanges_AndMissingFiles()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.docx");
        File.WriteAllText(path, "one");
        var saved = StateStore.ComputeFingerprint(path);

        Assert.False(StateStore.HasChanged(saved, path));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        Assert.True(StateStore.HasChanged(saved, path));

        File.WriteAllText(path, "one-longer");
        Assert.True(StateStore.HasChanged(saved, path));

        File.Delete(path);
        Assert.True(StateStore.HasChanged(saved, path));
    }

    [Fact]
    public void NormalizeInputs_MakesPathsAbsolute_Unique_AndSorted()
    {
        var result = StateStore.NormalizeInputs([@"D:\in\", @"d:\IN", @"D:\a"]);

        Assert.Equal([@"D:\a", @"D:\in"], result);
    }
}
