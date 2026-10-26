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

using System.Security.Cryptography;
using MarkPortO.Core.Convert;
using MarkPortO.Core.Engines;
using MarkPortO.Core.IO;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Pipeline;

/// <summary>FR-014 to FR-016, SC-009: interrupt a run, then resume it.</summary>
[Trait("Engine", "LibreOffice")]
public class InterruptTests
{
    private const int DocumentCount = 6;

    private static string[] MakeDocuments(TempDir dir, int count = DocumentCount)
    {
        var files = new List<string>();
        for (var i = 1; i <= count; i++)
        {
            var path = dir.Combine("in", $"d{i}.docx");
            DocxFactory.CreatePaged(path, [$"document {i} page one", $"document {i} first paragraph"], [$"document {i} page two"]);
            files.Add(path);
        }

        return [.. files];
    }

    /// <summary>The files below a folder with a fingerprint of their content (state files excluded).</summary>
    private static Dictionary<string, string> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".markporto", StringComparison.Ordinal))
            .ToDictionary(
                f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                f => System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    /// <summary>Starts a run and cancels it as soon as one document is completely finished (all its steps).</summary>
    private static async Task<ConvertResult> InterruptedRunAsync(TempDir dir, ConvertRequest request)
    {
        using var cts = new CancellationTokenSource();
        using var engine = Engine(dir);
        var service = new ConvertService(engine);
        service.DocumentChanged += d =>
        {
            if (d.IsDone())
            {
                cts.Cancel();
            }
        };

        return await service.RunAsync(request, cts.Token);
    }

    [LibreOfficeFact]
    public async Task AnInterruptedRun_IsMarkedInterrupted_KeepsWhatWasDone_AndLeavesNoHalfWrittenFiles()
    {
        using var dir = new TempDir();
        var files = MakeDocuments(dir);
        var outFolder = dir.Combine("out");

        var result = await InterruptedRunAsync(dir, Request(outFolder, Options(), files));

        Assert.Equal(JobStatus.Interrupted, result.Job.Status);
        Assert.True(result.Documents.Any(d => d.IsDone() || d.Step1 == StepStatus.Done), "something was finished before the interruption");
        Assert.True(result.Documents.Any(d => !d.IsDone()), "the run was cut short");
        Assert.Empty(Directory.EnumerateFiles(outFolder, "*" + AtomicFile.TempSuffix, SearchOption.AllDirectories));

        // What was finished is intact: its Markdown exists.
        foreach (var done in result.Documents.Where(d => d.IsDone()))
        {
            Assert.True(File.Exists(Path.Combine(done.OutputFolder, Path.GetFileNameWithoutExtension(done.SourcePath) + ".md")));
        }

        // The state on disk says the same.
        var store = new StateStore(outFolder);
        Assert.Equal(JobStatus.Interrupted, store.LoadJob(result.Job.JobId)!.Status);
        Assert.Single(store.FindResumableJobs());
    }

    [LibreOfficeFact]
    public async Task Resuming_FinishesTheRest_WithoutRepeatingFinishedFiles_AndGivesTheSameOutputAsAnUninterruptedRun()
    {
        using var dir = new TempDir();
        var files = MakeDocuments(dir);
        var interruptedOut = dir.Combine("interrupted-out");
        var interrupted = await InterruptedRunAsync(dir, Request(interruptedOut, Options(), files));
        var finishedBefore = interrupted.Documents
            .Where(d => d.IsDone())
            .ToDictionary(d => d.DocId, d => File.GetLastWriteTimeUtc(Path.Combine(d.OutputFolder, Path.GetFileNameWithoutExtension(d.SourcePath) + ".md")));
        Assert.NotEmpty(finishedBefore);

        using var engine = Engine(dir);
        var service = new ConvertService(engine);
        var skipped = new List<string>();
        service.DocumentSkipped += d => skipped.Add(d.DocId);
        var resumed = await service.ResumeAsync(new ResumeRequest { OutputFolder = interruptedOut });

        // SC-009: all files are finished; the ones finished before were not touched again.
        Assert.Equal(JobStatus.Completed, resumed.Job.Status);
        Assert.Equal(interrupted.Job.JobId, resumed.Job.JobId);
        Assert.All(resumed.Documents, d => Assert.True(d.IsDone(), $"{d.DocId} is not finished"));
        Assert.All(finishedBefore.Keys, id => Assert.Contains(id, skipped));
        foreach (var (id, time) in finishedBefore)
        {
            var doc = resumed.Documents.Single(d => d.DocId == id);
            Assert.Equal(time, File.GetLastWriteTimeUtc(Path.Combine(doc.OutputFolder, Path.GetFileNameWithoutExtension(doc.SourcePath) + ".md")));
        }

        // The result equals the result of a run that was never interrupted.
        var straightOut = dir.Combine("straight-out");
        await RunAsync(dir, Request(straightOut, Options(), files));
        Assert.Equal(Tree(straightOut), Tree(interruptedOut));
    }

    [LibreOfficeFact]
    public async Task Resuming_KeepsTheSameOutputFolders()
    {
        using var dir = new TempDir();
        var files = MakeDocuments(dir, 4);
        var outFolder = dir.Combine("out");
        var interrupted = await InterruptedRunAsync(dir, Request(outFolder, Options(), files));
        var folders = interrupted.Documents.ToDictionary(d => d.DocId, d => d.OutputFolder);

        using var engine = Engine(dir);
        var resumed = await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder });

        Assert.Equal(folders, resumed.Documents.ToDictionary(d => d.DocId, d => d.OutputFolder));
        Assert.DoesNotContain(Directory.GetDirectories(outFolder), d => d.Contains("(2)", StringComparison.Ordinal));
    }

    [LibreOfficeFact]
    public async Task ACrashedRun_LeftRunning_IsResumedAndItsStaleTempFilesAreRemoved()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["crash test text"]);
        var outFolder = dir.Combine("out");

        // Prepare the state a killed process leaves: step 1 "running", a half-written temp file, no lock held.
        var store = new StateStore(outFolder);
        var request = Request(outFolder, Options(), source);
        var (job, documents) = ConvertService.Plan(request, store);
        documents[0].Step1 = StepStatus.Running;
        store.SaveJob(job);
        store.SaveDocument(job.JobId, documents[0]);
        Directory.CreateDirectory(documents[0].OutputFolder);
        var stale = Path.Combine(documents[0].OutputFolder, "a.md" + AtomicFile.TempSuffix);
        File.WriteAllText(stale, "half written");

        using var engine = Engine(dir);
        var resumed = await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder });

        Assert.Equal(JobStatus.Completed, resumed.Job.Status);
        Assert.False(File.Exists(stale));
        Assert.Contains("crash test text", File.ReadAllText(Path.Combine(documents[0].OutputFolder, "a.md")));
    }

    [LibreOfficeFact]
    public async Task WhenAnInputChangedWhileInterrupted_ThatFileStartsOver_AndShowsTheNewText()
    {
        using var dir = new TempDir();
        var files = MakeDocuments(dir, 4);
        var outFolder = dir.Combine("out");
        var interrupted = await InterruptedRunAsync(dir, Request(outFolder, Options(), files));
        var done = interrupted.Documents.First(d => d.IsDone());
        DocxFactory.CreatePaged(done.SourcePath, ["BRAND NEW TEXT after the interruption"]);

        using var engine = Engine(dir);
        var resumed = await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder });

        var again = resumed.Documents.Single(d => d.DocId == done.DocId);
        Assert.True(again.IsDone());
        Assert.Contains(ResumePlanner.RestartedCode, again.Warnings.Select(w => w.Code));
        var markdown = File.ReadAllText(Path.Combine(again.OutputFolder, Path.GetFileNameWithoutExtension(again.SourcePath) + ".md"));
        Assert.Contains("BRAND NEW TEXT", markdown);
        Assert.DoesNotContain("first paragraph", markdown); // nothing of the old text
    }

    [LibreOfficeFact]
    public async Task WhenAnInputWasDeletedWhileInterrupted_ThatFileFails_AndTheOthersFinish()
    {
        using var dir = new TempDir();
        var files = MakeDocuments(dir, 4);
        var outFolder = dir.Combine("out");
        var interrupted = await InterruptedRunAsync(dir, Request(outFolder, Options(), files));
        var unfinished = interrupted.Documents.First(d => !d.IsDone());
        File.Delete(unfinished.SourcePath);

        using var engine = Engine(dir);
        var resumed = await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder });

        var failed = resumed.Documents.Single(d => d.DocId == unfinished.DocId);
        Assert.True(failed.IsFailed());
        Assert.Contains("not found", failed.Error!.Message);
        Assert.Equal(JobStatus.CompletedWithErrors, resumed.Job.Status);
        Assert.All(resumed.Documents.Where(d => d.DocId != unfinished.DocId), d => Assert.True(d.IsDone()));
    }

    [LibreOfficeFact]
    public async Task RetryFailed_RunsOnlyTheFailedFile_AfterItWasFixed()
    {
        using var dir = new TempDir();
        var good = dir.Combine("in", "good.docx");
        DocxFactory.CreatePaged(good, ["good text"]);
        var broken = dir.Combine("in", "broken.docx");
        DocxFactory.CreateCorrupt(broken);
        var outFolder = dir.Combine("out");
        var first = await RunAsync(dir, Request(outFolder, Options(), good, broken));
        Assert.Equal(JobStatus.CompletedWithErrors, first.Job.Status);
        var goodMarkdown = Path.Combine(first.Documents.Single(d => d.SourcePath == good).OutputFolder, "good.md");
        var goodTime = File.GetLastWriteTimeUtc(goodMarkdown);

        // A plain resume does not touch a failed file ...
        using var engine = Engine(dir);
        await Assert.ThrowsAsync<JobNotFoundException>(() =>
            new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder }));

        // ... retrying does, once the file is fixed.
        DocxFactory.CreatePaged(broken, ["now it is a valid document"]);
        var retried = await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder, Mode = ResumeMode.RetryFailed });

        Assert.Equal(JobStatus.Completed, retried.Job.Status);
        Assert.All(retried.Documents, d => Assert.True(d.IsDone()));
        Assert.Equal(goodTime, File.GetLastWriteTimeUtc(goodMarkdown));
        Assert.Contains("now it is a valid document", File.ReadAllText(Path.Combine(retried.Documents.Single(d => d.SourcePath == broken).OutputFolder, "broken.md")));
    }

    [LibreOfficeFact]
    public async Task ThereIsNothingToResume_AfterACompleteRun()
    {
        using var dir = new TempDir();
        var files = MakeDocuments(dir, 1);
        var outFolder = dir.Combine("out");
        await RunAsync(dir, Request(outFolder, Options(), files));

        using var engine = Engine(dir);
        var error = await Assert.ThrowsAsync<JobNotFoundException>(() =>
            new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder }));

        Assert.Contains("no job to resume", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [LibreOfficeFact]
    public async Task TheJobToResume_IsPickedByItsInputs_AndCanBeNamedExplicitly()
    {
        using var dir = new TempDir();
        var a = MakeDocuments(dir, 1)[0];
        var b = dir.Combine("in", "other.docx");
        DocxFactory.CreatePaged(b, ["other"]);
        var outFolder = dir.Combine("out");
        var store = new StateStore(outFolder);

        // Two interrupted jobs in one output folder (the state of each is prepared by hand).
        var jobs = new List<JobState>();
        foreach (var input in new[] { a, b })
        {
            var (job, documents) = ConvertService.Plan(Request(outFolder, Options(), input), store);
            documents[0].Step1 = StepStatus.Interrupted;
            job.Status = JobStatus.Interrupted;
            store.SaveJob(job);
            store.SaveDocument(job.JobId, documents[0]);
            jobs.Add(job);
        }

        using var engine = Engine(dir);
        var byInput = await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder, GivenInputs = [b] });
        Assert.Equal(jobs[1].JobId, byInput.Job.JobId);

        var byId = await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder, JobId = jobs[0].JobId });
        Assert.Equal(jobs[0].JobId, byId.Job.JobId);
        Assert.All(new[] { byInput, byId }, r => Assert.Equal(JobStatus.Completed, r.Job.Status));
    }

    [LibreOfficeFact]
    public async Task AJobThatIsRunningElsewhere_CannotBeResumed()
    {
        using var dir = new TempDir();
        var source = MakeDocuments(dir, 1)[0];
        var outFolder = dir.Combine("out");
        var store = new StateStore(outFolder);
        var (job, documents) = ConvertService.Plan(Request(outFolder, Options(), source), store);
        documents[0].Step1 = StepStatus.Running;
        store.SaveJob(job);
        store.SaveDocument(job.JobId, documents[0]);

        using var held = store.TryAcquireLock(job.JobId); // another process is running it
        using var engine = Engine(dir);

        await Assert.ThrowsAsync<JobNotFoundException>(() =>
            new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder, JobId = job.JobId }));
    }

    [Fact]
    public async Task ResumingInAFolderThatDoesNotExist_Fails()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);

        var error = await Assert.ThrowsAsync<JobNotFoundException>(() =>
            new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = dir.Combine("none") }));

        Assert.Contains("not found", error.Message);
    }

    [LibreOfficeFact]
    public async Task TurningStep2Off_OnResume_RemovesTheImageReferences_WithoutConvertingAgain()
    {
        using var dir = new TempDir();
        var files = MakeDocuments(dir, 1);
        var outFolder = dir.Combine("out");
        var first = await RunAsync(dir, Request(outFolder, Options(), files));
        var markdownPath = Path.Combine(first.Documents[0].OutputFolder, "d1.md");
        Assert.Contains("](images/page-001.png)", File.ReadAllText(markdownPath));
        var imageTime = File.GetLastWriteTimeUtc(Path.Combine(first.Documents[0].OutputFolder, "images", "page-001.png"));

        // The finished job has nothing to resume, so prepare it as interrupted with one more file pending.
        var store = new StateStore(outFolder);
        var job = store.LoadJob(first.Job.JobId)!;
        job.Status = JobStatus.Interrupted;
        store.SaveJob(job, touch: false);

        using var engine = Engine(dir);
        var resumed = await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = outFolder, Step2 = false });

        Assert.DoesNotContain("](images/page", File.ReadAllText(markdownPath).Replace("`![ページ N](images/page-NNN.png)`", string.Empty, StringComparison.Ordinal));
        Assert.Equal(imageTime, File.GetLastWriteTimeUtc(Path.Combine(resumed.Documents[0].OutputFolder, "images", "page-001.png")));
        Assert.Equal(StepStatus.NotApplicable, resumed.Documents[0].Step2);
    }
}
