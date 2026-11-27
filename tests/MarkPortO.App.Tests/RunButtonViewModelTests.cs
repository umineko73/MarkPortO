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

using System.IO;
using MarkPortO.App.Services;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.App.Tests.PresetViewModelTests;

namespace MarkPortO.App.Tests;

/// <summary>
/// The one main button of the conversion: what it does follows the list, and it says how many files it takes before it is pressed.
/// The tests have no dictionary of texts (<see cref="UiText"/> gives the key back), so the key says what the button does and the counts are
/// checked as numbers.
/// </summary>
public sealed class RunButtonViewModelTests
{
    private static void WaitFor(Screen screen, Func<bool> condition, Func<string> what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, what());
            screen.Pump();
            Thread.Sleep(20);
        }
    }

    private static string Listed(Screen screen) =>
        $"list: {string.Join(", ", screen.Model.Files.Select(f => f.Entry?.State.ToString() ?? "-"))}; resume: {screen.Model.ResumeCommand.CanExecute(null)}; status: {screen.Model.Status}";

    private static string Inputs(Screen screen, params string[] names)
    {
        var folder = Path.Combine(Path.GetDirectoryName(screen.Store.FilePath)!, "in");
        Directory.CreateDirectory(folder);
        foreach (var name in names)
        {
            File.WriteAllBytes(Path.Combine(folder, name), [1, 2, 3]);
        }

        return folder;
    }

    [Fact]
    public void FilesNeverConverted_AreStarted() => Sta.Run(screen =>
    {
        screen.Model.InputPaths.Add(Inputs(screen, "a.docx", "b.docx"));
        WaitFor(screen, () => screen.Model.Files.Count == 2, () => Listed(screen));

        Assert.Equal("Main_RunStartCount", screen.Model.MainRunText);
        Assert.Equal("Main_StartTip", screen.Model.MainRunTip);
        Assert.True(screen.Model.MainRunCommand.CanExecute(null));
    });

    [Fact]
    public void ANewFileNextToConvertedOnes_IsResumed_AndCounted() => Sta.Run(screen =>
    {
        var folder = Inputs(screen, "a.docx", "b.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [Path.Combine(folder, "a.docx")]);
        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 2, () => Listed(screen));

        Assert.Equal("Main_RunResumeCount", screen.Model.MainRunText);
        Assert.Equal("Main_ResumeTip", screen.Model.MainRunTip);
        Assert.Equal((0, 0, 1), screen.Model.ResumeCounts);
        Assert.True(screen.Model.MainRunCommand.CanExecute(null));
    });

    [Fact]
    public void AChangedFile_IsConvertedAgain_AndCounted() => Sta.Run(screen =>
    {
        var folder = Inputs(screen, "a.docx", "b.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [Path.Combine(folder, "a.docx"), Path.Combine(folder, "b.docx")]);
        File.WriteAllBytes(Path.Combine(folder, "b.docx"), [4, 5, 6, 7, 8]);
        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 2, () => Listed(screen));

        Assert.Equal("Main_RunChangedCount", screen.Model.MainRunText);
        Assert.Equal("Main_ChangedOnlyTip", screen.Model.MainRunTip);
        Assert.Equal(1, screen.Model.ChangedFileCount);
        Assert.True(screen.Model.MainRunCommand.CanExecute(null));
    });

    [Fact]
    public void AListOfOnlyNewFiles_IsStarted_EvenWhenAnEarlierRunOfTheInputsCouldGoOn() => Sta.Run(screen => // run button review MP-01
        {
            var folder = Inputs(screen, "a.docx", "b.pdf");
            var output = screen.Settings.Output.Folder;
            var job = FakeRecords.Write(output, [Path.Combine(folder, "a.docx")]);
            var store = new StateStore(output);
            job.Status = JobStatus.Interrupted;
            job.Inputs = [folder];
            store.SaveJob(job, touch: false);
            var document = store.LoadDocument(job.JobId, "d0001")!;
            document.Step2 = StepStatus.Pending;
            store.SaveDocument(job.JobId, document);

            screen.Model.InputPaths.Add(folder);
            WaitFor(screen, () => screen.Model.Files.Count == 2, () => Listed(screen));
            screen.Model.TypeDocx = false; // only PDF files are taken now: the list has b.pdf alone
            WaitFor(screen, () => screen.Model.Files.Count == 1 && screen.Model.ResumeCommand.CanExecute(null), () => Listed(screen));

            Assert.Equal("Main_RunStartCount", screen.Model.MainRunText);
        });

    [Fact]
    public void TheMenuCountsEveryFileThatChangedFilesOnlyConverts() => Sta.Run(screen => // run button review MP-04
    {
        var folder = Inputs(screen, "a.docx", "b.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [Path.Combine(folder, "a.docx")]);
        File.WriteAllBytes(Path.Combine(folder, "a.docx"), [4, 5, 6, 7, 8]);
        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 2, () => Listed(screen));

        Assert.Equal(2, screen.Model.ChangedFileCount); // the changed a.docx and the new b.docx
    });

    [Fact]
    public void AFileNeverConverted_CannotBeConvertedAgainFromStep1() => Sta.Run(screen => // run button review MP-05
    {
        screen.Model.InputPaths.Add(Inputs(screen, "a.docx"));
        WaitFor(screen, () => screen.Model.Files.Count == 1, () => Listed(screen));

        screen.Model.Selected = screen.Model.Files[0];

        Assert.False(screen.Model.CanReconvertSelected);
    });

    [Fact]
    public void AStartOfNewFiles_DoesNotGiveUpARunWhoseFilesTheListDoesNotTake() => Sta.Run(screen => // run button review MP-07
    {
        var folder = Inputs(screen, "a.docx", "b.pdf");
        var job = Interrupted(screen, folder, "a.docx");

        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 2 && screen.Model.ResumeCommand.CanExecute(null), () => Listed(screen));
        Assert.Equal([job], screen.Model.RunsAStartReplaces()); // the list takes a.docx: a new start replaces its run

        screen.Model.TypeDocx = false;
        WaitFor(screen, () => screen.Model.Files.Count == 1, () => Listed(screen));
        Assert.Empty(screen.Model.RunsAStartReplaces()); // a.docx is not in the list: its run is kept for a later resume
    });

    [Fact]
    public void TheMenuDoesNotCountAFileThatRunsElsewhere() => Sta.Run(screen => // run button review MP-08
    {
        var folder = Inputs(screen, "a.docx", "b.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [Path.Combine(folder, "a.docx")]);
        File.WriteAllBytes(Path.Combine(folder, "a.docx"), [4, 5, 6, 7, 8]);
        var other = Interrupted(screen, folder, "b.docx");
        using var running = new StateStore(screen.Settings.Output.Folder).TryAcquireLock(other);
        Assert.NotNull(running);

        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 2, () => Listed(screen));

        Assert.Equal(1, screen.Model.ChangedFileCount); // a.docx; b.docx is being converted by another process
    });

    [Fact]
    public void TheMenuCountsAFileConvertedWithTheOtherLayoutOfFolders() => Sta.Run(screen => // run button review MP-08
    {
        var folder = Inputs(screen, "a.docx", "b.docx");
        var output = screen.Settings.Output.Folder;
        FakeRecords.Write(output, [Path.Combine(folder, "a.docx")]);
        File.WriteAllBytes(Path.Combine(folder, "a.docx"), [4, 5, 6, 7, 8]);
        var other = FakeRecords.Write(output, [Path.Combine(folder, "b.docx")], o => o.FolderLayout = FolderLayout.Contents);
        var store = new StateStore(output);
        other.Inputs = [folder];
        store.SaveJob(other, touch: false);

        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 2, () => Listed(screen));
        screen.Model.FolderLayoutIndex = 0; // a folder of the input folder's name, not as b.docx was written
        WaitFor(screen, () => screen.Settings.Output.FolderLayout == FolderLayout.Folder && screen.Model.Files.Count == 2, () => Listed(screen));

        Assert.Equal(2, screen.Model.ChangedFileCount); // b.docx was written elsewhere: it is converted as a new file
    });

    [Fact]
    public void ConvertingAgainFromStep1_CanBeChosen_WhenOneOfTheChosenFilesWasConverted() => Sta.Run(screen => // run button review MP-09
    {
        var folder = Inputs(screen, "a.docx", "b.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [Path.Combine(folder, "b.docx")]);
        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 2, () => Listed(screen));
        var fresh = screen.Model.Files.Single(f => f.Source is null);
        var converted = screen.Model.Files.Single(f => f.Source is not null);

        screen.Model.Selected = fresh;
        screen.Model.SetSelectedFiles([fresh, converted]);

        Assert.True(screen.Model.CanReconvertSelected);
    });

    [Fact]
    public void TheMenuCountsTheMarkedPagesOfTheRunThatTheListDoesNotShow() => Sta.Run( // run button review MP-06
        screen =>
        {
            var folder = Inputs(screen, "a.docx", "b.docx");
            var output = screen.Settings.Output.Folder;
            var job = FakeRecords.Write(output, [Path.Combine(folder, "a.docx"), Path.Combine(folder, "b.docx")], o => o.Step3 = true);
            var store = new StateStore(output);
            foreach (var id in new[] { "d0001", "d0002" })
            {
                var document = store.LoadDocument(job.JobId, id)!;
                document.Pages = [new PageRecord { Index = 1, RereadMark = true }];
                store.SaveDocument(job.JobId, document);
            }

            screen.Model.InputPaths.Add(Path.Combine(folder, "a.docx")); // b.docx is not in the list, but "Run Step3 again" reads the whole run
            WaitFor(screen, () => screen.Model.Files.Count == 1 && screen.Model.Files[0].Source is not null, () => Listed(screen));

            Assert.Equal(2, screen.Model.RereadPageCount);
        },
        settings => settings.Steps.Step3 = true);

    private static Guid Interrupted(Screen screen, string folder, string name)
    {
        var output = screen.Settings.Output.Folder;
        var job = FakeRecords.Write(output, [Path.Combine(folder, name)]);
        var store = new StateStore(output);
        job.Status = JobStatus.Interrupted;
        job.Inputs = [folder];
        store.SaveJob(job, touch: false);
        var document = store.LoadDocument(job.JobId, "d0001")!;
        document.Step2 = StepStatus.Pending;
        store.SaveDocument(job.JobId, document);
        return job.JobId;
    }

    [Fact]
    public void WhenEverythingIsDoneAndUnchanged_TheMainButtonSaysSo_AndCannotBePressed() => Sta.Run(screen =>
    {
        var folder = Inputs(screen, "a.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [Path.Combine(folder, "a.docx")]);
        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 1 && screen.Model.Files[0].IsDone, () => Listed(screen));

        Assert.Equal("Main_RunNothing", screen.Model.MainRunText);
        Assert.False(screen.Model.MainRunCommand.CanExecute(null));
        Assert.True(screen.Model.RedoAllCommand.CanExecute(null)); // still in the menu

        screen.Model.Selected = screen.Model.Files[0];
        Assert.True(screen.Model.CanReconvertSelected); // a converted file can be converted again from Step1
    });

    [Fact]
    public void TheMenuSaysHowManyPagesStep3ReadsAgain() => Sta.Run(
        screen =>
        {
            var folder = Inputs(screen, "a.docx");
            var output = screen.Settings.Output.Folder;
            var job = FakeRecords.Write(output, [Path.Combine(folder, "a.docx")], o => o.Step3 = true);
            var store = new StateStore(output);
            var document = store.LoadDocument(job.JobId, "d0001")!;
            document.Pages = [.. Enumerable.Range(1, 3).Select(i => new PageRecord { Index = i, RereadMark = i != 2 })];
            store.SaveDocument(job.JobId, document);

            screen.Model.InputPaths.Add(folder);
            WaitFor(screen, () => screen.Model.Files.Count == 1 && screen.Model.Files[0].Source is not null, () => Listed(screen));

            Assert.Equal("Main_RereadCount", screen.Model.RereadMenuText);
            Assert.Equal(2, screen.Model.RereadPageCount);

            var told = false;
            screen.Model.PropertyChanged += (_, e) => told |= e.PropertyName == nameof(screen.Model.RereadMenuText);
            screen.Model.Step3 = false;
            Assert.True(told, "the menu was not told that Step3 went off");
            Assert.Equal("Main_Reread", screen.Model.RereadMenuText);
            Assert.Equal(0, screen.Model.RereadPageCount);
        },
        settings => settings.Steps.Step3 = true);
}
