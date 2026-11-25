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
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.App.Tests.PresetViewModelTests;

namespace MarkPortO.App.Tests;

/// <summary>The findings of the review of FR-155〜160 (2026-11-26) on the screen.</summary>
public sealed class ReviewFixesViewModelTests
{
    private static void WaitFor(Screen screen, Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, what);
            screen.Pump();
            Thread.Sleep(20);
        }

        screen.Pump();
    }

    private static string Folder(Screen screen, params string[] parts) =>
        Path.Combine([Path.GetDirectoryName(screen.Store.FilePath)!, .. parts]);

    private static string Source(Screen screen, string folder, string name)
    {
        var path = Folder(screen, [.. folder.Split('/'), name]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public void RedoAll_LeavesOutAFileThatIsConvertedElsewhere() => Sta.Run(screen =>
    {
        var a = Source(screen, "in", "a.docx");
        var output = screen.Settings.Output.Folder;
        var job = FakeRecords.Write(output, [a]);
        var store = new StateStore(output);
        var running = store.LoadJob(job.JobId)!;
        running.Status = JobStatus.Running;
        store.SaveJob(running, touch: false);
        var markdown = Path.Combine(store.LoadDocument(job.JobId, "d0001")!.OutputFolder, "a.md");
        File.WriteAllText(markdown, "kept");
        using var held = store.TryAcquireLock(job.JobId);
        screen.Store.Update(s => s.Output.SameFolder = true); // "Overwrite"

        screen.Model.DropInputs([Folder(screen, "in")]); // saved in the settings, as the screen does
        WaitFor(screen, () => screen.Model.Files.Count == 1, "the list was not filled");
        Assert.Equal(FileState.RunningElsewhere, screen.Model.Files[0].Entry?.State);
        Assert.True(screen.Model.RedoAllCommand.CanExecute(null));
        screen.Model.RedoAllCommand.Execute(null);
        WaitFor(screen, () => !screen.Model.IsRunning, "the run did not end");

        Assert.True(store.ListJobs().Count == 1, screen.Model.Status); // no new job
        Assert.Equal("kept", File.ReadAllText(markdown));
    });

    [Fact]
    public void AddingOnlyStep2_StartsWithoutAConnection_ThoughStep3IsOnOnTheScreen() => Sta.Run(screen =>
    {
        var a = Source(screen, "in", "a.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [a], o => { o.Step2 = false; o.Step3 = true; }); // Step3 done, Step2 never run
        screen.Model.DropInputs([Folder(screen, "in")]);
        WaitFor(screen, () => screen.Model.Files.Count == 1 && screen.Settings.Steps.Step3, "the list and the conditions were not put back");
        screen.Model.Step2 = true;
        WaitFor(screen, () => screen.Model.Files[0].Entry?.State == FileState.StepPending, "Step2 was not to be added");
        Assert.Equal(string.Empty, screen.Settings.Vlm.Selected);

        var before = screen.Model.Status;
        screen.Model.ResumeCommand.Execute(null);
        WaitFor(screen, () => !screen.Model.IsRunning && screen.Model.Status != before, "the run did not end");

        Assert.DoesNotContain("Status_CheckSettings", screen.Model.Status, StringComparison.Ordinal); // not stopped for a connection
        Assert.DoesNotContain("Status_VlmNotReady", screen.Model.Status, StringComparison.Ordinal);
    });

    [Fact]
    public void AWorkSetWithoutAConnection_LeavesNoConnectionChosen() => Sta.Run(screen =>
    {
        Source(screen, "in", "a.docx");
        screen.Model.InputPaths.Add(Folder(screen, "in"));
        screen.Model.SaveWorkSet("none"); // no connection chosen
        screen.Store.Update(s =>
        {
            s.Vlm.Connections.Add(new ConnectionProfile { Name = "local", BaseUrl = "http://localhost:1234/v1", Model = "m" });
            s.Vlm.Selected = "local";
        });

        Assert.True(screen.Model.OpenWorkSet("none"));

        Assert.Equal(string.Empty, screen.Settings.Vlm.Selected);
    });

    [Fact]
    public void ChangingTheSubfoldersOrTheKinds_ListsTheFilesAgain() => Sta.Run(screen =>
    {
        Source(screen, "in", "a.docx");
        Source(screen, "in/sub", "b.docx");
        Source(screen, "in", "c.xlsx");
        screen.Store.Update(s => s.Input.Recurse = false);
        screen.Model.InputPaths.Add(Folder(screen, "in"));
        WaitFor(screen, () => screen.Model.Files.Count == 2, "the list was not filled");

        screen.Model.Recurse = true;
        WaitFor(screen, () => screen.Model.Files.Count == 3, "the files of the subfolder were not listed");

        screen.Model.TypeXlsx = false;
        WaitFor(screen, () => screen.Model.Files.Count == 2, "the Excel file stayed in the list");
    });
}
