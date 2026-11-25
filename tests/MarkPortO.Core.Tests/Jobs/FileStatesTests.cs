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
using MarkPortO.Core.Vlm;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Jobs;

/// <summary>FR-157: the list is the last record of each file of the inputs; "Resume" goes on per file (adding a step too).</summary>
[Trait("Engine", "LibreOffice")]
public class FileStatesTests : JapaneseUiTests
{
    private static readonly DocumentKind[] AllKinds = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf];

    private static IReadOnlyList<FileEntry> Find(string output, bool step2, bool step3, params string[] inputs) =>
        FileStates.Find(output, inputs, AllKinds, recurse: true, FolderLayout.Folder, step2, step3);

    private static string Docx(TempDir dir, string folder, string name, string text)
    {
        var path = dir.Combine("in", folder, name);
        DocxFactory.CreatePaged(path, [text]);
        return path;
    }

    private static async Task Convert(TempDir dir, string output, OptionsSnapshot options, string given, params string[] files)
    {
        using var engine = Engine(dir);
        var request = new ConvertRequest
        {
            Inputs = [.. files.Select(f => new PlannedInput(f))],
            GivenInputs = [given],
            OutputFolder = output,
            Options = options,
        };
        await new ConvertService(engine).RunAsync(request);
    }

    [LibreOfficeFact]
    public async Task EachFile_HasItsOwnLastRecord_WhateverSetOfInputsConvertedIt()
    {
        using var dir = new TempDir();
        var a = Docx(dir, "A", "a.docx", "alpha");
        var b = Docx(dir, "B", "b.docx", "beta");
        var c = Docx(dir, "C", "c.docx", "gamma");
        var output = dir.Combine("out");
        await Convert(dir, output, Options(), dir.Combine("in"), a, b); // A and B together

        // A alone, then A with a new folder C: A has its record, C has none.
        var onlyA = Find(output, true, false, dir.Combine("in", "A"));
        Assert.Equal(FileState.Done, Assert.Single(onlyA).State);
        var aAndC = Find(output, true, false, dir.Combine("in", "A"), dir.Combine("in", "C"));
        Assert.Equal([FileState.Done, FileState.NotConverted], aAndC.Select(e => e.State));
        Assert.Null(aAndC[1].Document);
        Assert.Equal(Path.Combine("C", "c.docx"), aAndC[1].DisplayName); // several folders: the folder name in front (FR-013a)

        // A converted again alone: its newer record is the last one; B keeps the older.
        await Convert(dir, output, Options(), dir.Combine("in", "A"), a);
        var both = Find(output, true, false, dir.Combine("in", "A"), dir.Combine("in", "B"));
        Assert.NotEqual(both[0].Job!.JobId, both[1].Job!.JobId);
        Assert.True(both[0].Job!.CreatedAt > both[1].Job!.CreatedAt);
        _ = c;
    }

    [LibreOfficeFact]
    public async Task AFileConvertedWithoutStep3_IsStep3Pending_WhenStep3IsOn_AndChangedWinsWhenTheFileWasEdited()
    {
        using var dir = new TempDir();
        var a = Docx(dir, "A", "a.docx", "alpha");
        var b = Docx(dir, "A", "b.docx", "beta");
        var output = dir.Combine("out");
        await Convert(dir, output, Options(step3: false), dir.Combine("in", "A"), a, b);

        Assert.All(Find(output, true, false, dir.Combine("in", "A")), e => Assert.Equal(FileState.Done, e.State));
        var on = Find(output, true, true, dir.Combine("in", "A"));
        Assert.All(on, e => Assert.True(e.State == FileState.StepPending && e.AddsStep3 && !e.AddsStep2 && e.CanResume));

        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(5)); // edited after the conversion
        var edited = Find(output, true, true, dir.Combine("in", "A"));
        Assert.Equal([FileState.StepPending, FileState.Changed], edited.Select(e => e.State));
    }

    [LibreOfficeFact]
    public async Task AFileWhoseFolderIsGone_IsNotConverted_AndRecordsUnderFindRecordsOfEveryKind()
    {
        using var dir = new TempDir();
        var a = Docx(dir, "A", "a.docx", "alpha");
        var output = dir.Combine("out");
        await Convert(dir, output, Options(), dir.Combine("in", "A"), a);

        var underA = FileStates.RecordsUnder(output, [dir.Combine("in", "A")]);
        Assert.Single(underA);
        // Only Excel on the screen: the record of the Word file is still found by the path (the first step of FR-157).
        Assert.Empty(FileStates.Find(output, [dir.Combine("in", "A")], [DocumentKind.Xlsx], true, FolderLayout.Folder, true, false));
        Assert.Single(FileStates.RecordsUnder(output, [dir.Combine("in")]));

        Directory.Delete(underA[0].Document.OutputFolder, recursive: true);
        Assert.Equal(FileState.NotConverted, Assert.Single(Find(output, true, false, dir.Combine("in", "A"))).State);
    }

    [LibreOfficeFact]
    public async Task ResumingAFailedFile_RetriesItAlone_AndAChangedUnfinishedFileStartsAgain()
    {
        using var dir = new TempDir();
        var a = Docx(dir, "A", "a.docx", "alpha");
        var b = Docx(dir, "A", "b.docx", "beta");
        var output = dir.Combine("out");
        await Convert(dir, output, Options(), dir.Combine("in", "A"), a, b);
        var entries = Find(output, true, false, dir.Combine("in", "A"));
        var job = entries[0].Job!;
        var store = new StateStore(output);
        var failed = store.LoadDocument(job.JobId, entries.Single(e => e.Path == a).Document!.DocId)!;
        failed.Step2 = StepStatus.Failed;
        failed.Error = new DocumentError(2, "made to fail");
        store.SaveDocument(job.JobId, failed);
        var unfinished = store.LoadDocument(job.JobId, entries.Single(e => e.Path == b).Document!.DocId)!;
        unfinished.Step2 = StepStatus.Pending;
        store.SaveDocument(job.JobId, unfinished);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(5));
        var states = Find(output, true, false, dir.Combine("in", "A"));
        Assert.Equal([FileState.Failed, FileState.Changed], states.Select(e => e.State));
        Assert.All(FileStates.ToResume(states), e => Assert.True(e.CanResume));

        using var engine = Engine(dir);
        var result = await new ConvertService(engine).ResumeAsync(new ResumeRequest
        {
            OutputFolder = output,
            JobId = job.JobId,
            Mode = ResumeMode.Files,
            DocumentIds = [failed.DocId],
            Step2 = true,
            Step3 = false,
        });

        Assert.True(result.Documents.Single(d => d.DocId == failed.DocId).IsDone());
        Assert.Equal(StepStatus.Pending, store.LoadDocument(job.JobId, unfinished.DocId)!.Step2); // not chosen: left as it was

        result = await new ConvertService(engine).ResumeAsync(new ResumeRequest
        {
            OutputFolder = output,
            JobId = job.JobId,
            Mode = ResumeMode.Files,
            DocumentIds = [unfinished.DocId],
            Step2 = true,
            Step3 = false,
        });
        var restarted = result.Documents.Single(d => d.DocId == unfinished.DocId);
        Assert.True(restarted.IsDone());
        Assert.Contains(restarted.Warnings, w => w.Code == ResumePlanner.RestartedCode); // converted again from Step1 (FR-015)
        Assert.All(Find(output, true, false, dir.Combine("in", "A")), e => Assert.Equal(FileState.Done, e.State));
    }

    [LibreOfficeFact]
    public async Task ResumingChosenFiles_OfAFinishedJob_AddsStep3ToThemOnly_AndKeepsTheConditionsOfTheJob()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();
        var a = Docx(dir, "A", "a.docx", "alpha");
        var b = Docx(dir, "A", "b.docx", "beta");
        var output = dir.Combine("out");
        await Convert(dir, output, Options(step3: false), dir.Combine("in", "A"), a, b);
        var entries = Find(output, true, true, dir.Combine("in", "A"));
        var job = entries[0].Job!;
        Assert.Equal(JobStatus.Completed, job.Status);
        var first = entries.Single(e => e.Path == a);
        var firstMarkdown = File.ReadAllText(Path.Combine(first.Document!.OutputFolder, "a.md"));

        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(new ConnectionProfile { Name = "fake", BaseUrl = server.BaseUrl, Model = "vision-model", TimeoutSeconds = 5 });
        settings.Vlm.Selected = "fake";
        var (setup, error) = VlmSetupFactory.Create(settings);
        Assert.True(setup is not null, error);
        using var engine = Engine(dir);
        var result = await new ConvertService(engine, vlm: setup).ResumeAsync(new ResumeRequest
        {
            OutputFolder = output,
            JobId = job.JobId,
            Mode = ResumeMode.Files,
            DocumentIds = [first.Document.DocId],
            Step2 = true,
            Step3 = true,
            Connection = "fake",
        });

        var resumedA = result.Documents.Single(d => d.DocId == first.Document.DocId);
        Assert.Equal(StepStatus.Done, resumedA.Step3); // Step3 added
        Assert.Equal(StepStatus.Done, resumedA.Step1);
        Assert.NotEqual(firstMarkdown, File.ReadAllText(Path.Combine(resumedA.OutputFolder, "a.md")));
        var store = new StateStore(output);
        var otherB = store.LoadDocument(job.JobId, entries.Single(e => e.Path == b).Document!.DocId)!;
        Assert.Equal(StepStatus.NotApplicable, otherB.Step3); // the other file is left as it was
        Assert.False(store.LoadJob(job.JobId)!.Options.Step3); // the job keeps its own selection

        var after = Find(output, true, true, dir.Combine("in", "A"));
        Assert.Equal([FileState.Done, FileState.StepPending], after.Select(e => e.State));
    }
}
