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
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using MarkPortO.Core.Tests.Localization;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Jobs;

/// <summary>The findings of the review of FR-155〜160 (2026-11-26): resuming per file must not lose or misplace output.</summary>
[Trait("Engine", "LibreOffice")]
public class FileResumeReviewTests : JapaneseUiTests
{
    private static readonly DocumentKind[] AllKinds = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf];

    private static async Task<(JobState Job, DocumentRecord Document)> ConvertOne(TempDir dir, string output, OptionsSnapshot options)
    {
        var source = dir.Combine("in", "A", "a.docx");
        DocxFactory.CreatePaged(source, ["alpha"]);
        using var engine = Engine(dir);
        var result = await new ConvertService(engine).RunAsync(new ConvertRequest
        {
            Inputs = [new PlannedInput(source)],
            GivenInputs = [dir.Combine("in", "A")],
            OutputFolder = output,
            Options = options,
        });
        return (result.Job, result.Documents[0]);
    }

    private static ResumeRequest Files(string output, Guid job, string docId, bool step2, bool step3) => new()
    {
        OutputFolder = output,
        JobId = job,
        Mode = ResumeMode.Files,
        DocumentIds = [docId],
        Step2 = step2,
        Step3 = step3,
    };

    [LibreOfficeFact]
    public async Task AResumeThatCannotStart_DoesNotDeleteTheOutputOfAChangedFile()
    {
        using var dir = new TempDir();
        var output = dir.Combine("out");
        var (job, document) = await ConvertOne(dir, output, Options(step3: false));
        var store = new StateStore(output);
        var saved = store.LoadDocument(job.JobId, document.DocId)!;
        saved.Step2 = StepStatus.Pending; // not finished
        store.SaveDocument(job.JobId, saved);
        File.SetLastWriteTimeUtc(saved.SourcePath, DateTime.UtcNow.AddMinutes(5)); // changed
        var markdown = Path.Combine(saved.OutputFolder, "a.md");
        Assert.True(File.Exists(markdown));

        using var engine = Engine(dir);
        await Assert.ThrowsAsync<ConvertStartException>(() =>
            new ConvertService(engine).ResumeAsync(Files(output, job.JobId, document.DocId, step2: true, step3: true))); // no VLM

        Assert.True(File.Exists(markdown));
    }

    [LibreOfficeFact]
    public async Task AddingOnlyStep2_DoesNotNeedTheVlm_ThoughTheJobHadStep3()
    {
        using var dir = new TempDir();
        var output = dir.Combine("out");
        var (job, document) = await ConvertOne(dir, output, Options(step2: false, step3: false));
        var store = new StateStore(output);
        var saved = store.LoadJob(job.JobId)!;
        saved.Options.Step3 = true; // a job that had Step3 for its other files
        store.SaveJob(saved, touch: false);

        using var engine = Engine(dir);
        var result = await new ConvertService(engine).ResumeAsync(Files(output, job.JobId, document.DocId, step2: true, step3: false));

        Assert.Equal(StepStatus.Done, result.Documents.Single(d => d.DocId == document.DocId).Step2);
    }

    [LibreOfficeFact]
    public async Task AMovedOutputFolder_IsWrittenWhereItIsNow_NotWhereItWas()
    {
        using var dir = new TempDir();
        var before = dir.Combine("before");
        var (job, document) = await ConvertOne(dir, before, Options(step2: false, step3: false));
        var after = dir.Combine("after");
        Directory.Move(before, after);

        var entry = Assert.Single(FileStates.Find(after, [dir.Combine("in", "A")], AllKinds, true, FolderLayout.Folder, step2: true, step3: false));
        Assert.Equal(FileState.StepPending, entry.State);
        Assert.StartsWith(after, entry.Document!.OutputFolder, StringComparison.OrdinalIgnoreCase);

        using var engine = Engine(dir);
        var result = await new ConvertService(engine).ResumeAsync(Files(after, job.JobId, document.DocId, step2: true, step3: false));

        var resumed = result.Documents.Single(d => d.DocId == document.DocId);
        Assert.StartsWith(after, resumed.OutputFolder, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(before)); // nothing is written where it was
        Assert.Contains("images/", File.ReadAllText(Path.Combine(resumed.OutputFolder, "a.md")), StringComparison.Ordinal);
    }

    [LibreOfficeFact]
    public async Task AMovedCopy_WhoseFolderIsGone_NeverTakesAnotherFolderOfTheSameName_NorWritesTheOldPlace()
    {
        using var dir = new TempDir();
        var before = dir.Combine("before");
        var (job, document) = await ConvertOne(dir, before, Options(step2: false, step3: false));
        var after = dir.Combine("after");
        Directory.CreateDirectory(after);
        Directory.Move(Path.Combine(before, ".markporto"), Path.Combine(after, ".markporto")); // only the state was moved
        var other = Path.Combine(after, "x", Path.GetFileName(document.OutputFolder)); // another document's folder of the same name
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "a.md"), "another document");

        Assert.Empty(FileStates.LastRecords(after)); // its folder is not in the copy
        File.SetLastWriteTimeUtc(document.SourcePath, DateTime.UtcNow.AddMinutes(5));
        using var engine = Engine(dir);
        var result = await new ConvertService(engine).ResumeAsync(Files(after, job.JobId, document.DocId, step2: false, step3: false));

        Assert.Equal("another document", File.ReadAllText(Path.Combine(other, "a.md")));
        var resumed = result.Documents.Single(d => d.DocId == document.DocId);
        Assert.StartsWith(after, resumed.OutputFolder, StringComparison.OrdinalIgnoreCase); // made again in the copy
        Assert.NotEqual(other, resumed.OutputFolder, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(before, Path.GetFileName(document.OutputFolder), "a.md"))); // the old place is not written
    }

    [LibreOfficeFact]
    public async Task AResumeInPlace_KeepsTheOutputRootOfAFolderInput()
    {
        using var dir = new TempDir();
        var output = dir.Combine("out");
        var (job, document) = await ConvertOne(dir, output, Options(step2: false, step3: false));
        var store = new StateStore(output);
        var saved = store.LoadJob(job.JobId)!;
        saved.OutputRoot = Path.Combine(output, "A"); // as a folder input writes below the output folder
        store.SaveJob(saved, touch: false);

        using var engine = Engine(dir);
        var result = await new ConvertService(engine).ResumeAsync(Files(output, job.JobId, document.DocId, step2: true, step3: false));

        Assert.Equal(Path.Combine(output, "A"), result.Job.OutputRoot);
    }

    [Fact]
    public void AFileWhoseJobIsLocked_IsRunningElsewhere_WhateverItsSavedState()
    {
        using var dir = new TempDir();
        var a = dir.Combine("in", "a.docx");
        Directory.CreateDirectory(dir.Combine("in"));
        File.WriteAllBytes(a, [1]);
        var output = dir.Combine("out");
        var job = FakeRecords.Write(output, [a]); // completed: a resume holds the lock while it checks the VLM
        using var held = new StateStore(output).TryAcquireLock(job.JobId);
        Assert.NotNull(held);

        var entry = Assert.Single(FileStates.Find(output, [dir.Combine("in")], AllKinds, true, FolderLayout.Folder, true, false));

        Assert.Equal(FileState.RunningElsewhere, entry.State);
    }

    [Fact]
    public void TheLastRecordOfAFile_IsItsNewestConversion_NotTheJobChangedLast()
    {
        using var dir = new TempDir();
        var a = dir.Combine("in", "a.docx");
        var b = dir.Combine("in", "b.docx");
        Directory.CreateDirectory(dir.Combine("in"));
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(b, [1]);
        var output = dir.Combine("out");
        var both = FakeRecords.Write(output, [a, b], o => o.Dpi = 100, DateTime.UtcNow.AddHours(-2));
        var alone = FakeRecords.Write(output, [a], o => o.Dpi = 200, DateTime.UtcNow.AddHours(-1));
        var store = new StateStore(output);
        store.SaveJob(store.LoadJob(both.JobId)!, touch: true); // b of the older job was resumed just now

        var records = FileStates.LastRecords(output);

        Assert.Equal(alone.JobId, records[Path.GetFullPath(a)].Job.JobId);
        Assert.Equal(both.JobId, records[Path.GetFullPath(b)].Job.JobId);
    }

    [Fact]
    public void AFileWhoseJobRunsElsewhere_IsRunningElsewhere()
    {
        using var dir = new TempDir();
        var a = dir.Combine("in", "a.docx");
        Directory.CreateDirectory(dir.Combine("in"));
        File.WriteAllBytes(a, [1]);
        var output = dir.Combine("out");
        var job = FakeRecords.Write(output, [a]);
        var store = new StateStore(output);
        var running = store.LoadJob(job.JobId)!;
        running.Status = JobStatus.Running;
        store.SaveJob(running, touch: false);
        using var held = store.TryAcquireLock(job.JobId);
        Assert.NotNull(held);

        var entry = Assert.Single(FileStates.Find(output, [dir.Combine("in")], AllKinds, true, FolderLayout.Folder, true, false));

        Assert.Equal(FileState.RunningElsewhere, entry.State);
        Assert.Empty(FileStates.ToResume([entry]));
    }

    [Fact]
    public void Step3IsRead_WhenAFileToResumeHasItUnfinished_ThoughItIsOffOnTheScreen()
    {
        DocumentRecord Document(StepStatus step3) => new() { Step1 = StepStatus.Done, Step2 = StepStatus.Done, Step3 = step3 };
        FileEntry Entry(FileState state, StepStatus step3, bool adds3 = false) =>
            new("p", "p", DocumentKind.Docx, new JobState(), Document(step3), state) { AddsStep3 = adds3 };

        Assert.True(FileStates.ReadsStep3([Entry(FileState.Unfinished, StepStatus.Pending)], step3On: false));
        Assert.True(FileStates.ReadsStep3([Entry(FileState.DonePartlyFailed, StepStatus.Done)], step3On: false));
        Assert.True(FileStates.ReadsStep3([Entry(FileState.StepPending, StepStatus.NotApplicable, adds3: true)], step3On: true));
        Assert.False(FileStates.ReadsStep3([Entry(FileState.Unfinished, StepStatus.NotApplicable)], step3On: false));
        Assert.True(FileStates.ReadsStep3([new FileEntry("n", "n", DocumentKind.Docx, null, null, FileState.NotConverted)], step3On: true));
        Assert.False(FileStates.ReadsStep3([new FileEntry("n", "n", DocumentKind.Docx, null, null, FileState.NotConverted)], step3On: false));
    }
}
