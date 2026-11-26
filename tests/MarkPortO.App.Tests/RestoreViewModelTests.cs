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
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.App.Tests.PresetViewModelTests;

namespace MarkPortO.App.Tests;

/// <summary>FR-156〜FR-160: choosing inputs puts back the output folder and the conditions; adding asks; work sets.</summary>
public sealed class RestoreViewModelTests
{
    private static void WaitFor(Screen screen, Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
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
        var path = Folder(screen, "in", folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static int SavedDpi(Screen screen)
    {
        screen.Store.Flush();
        return new SettingsStore(screen.Store.FilePath).Load().Settings.Images.Dpi;
    }

    [Fact]
    public void AnInputWithARecord_PutsBackItsConditions_WithoutSavingThem_AndEmptyingTheInputsBringsTheSavedOnesBack() => Sta.Run(screen =>
    {
        var a = Source(screen, "A", "a.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [a], o => { o.Dpi = 222; o.Chunks = true; });

        screen.Model.InputPaths.Add(Folder(screen, "in", "A"));
        WaitFor(screen, () => screen.Settings.Images.Dpi == 222, "the conditions were not put back");

        Assert.True(screen.Settings.Output.Chunks);
        Assert.Equal(FileState.Done, Assert.Single(screen.Model.Files).Entry?.State);
        Assert.Equal(SettingsLimits.DefaultDpi, SavedDpi(screen)); // FR-018: not saved by choosing an input
        Assert.NotEqual(string.Empty, screen.Model.PresetStateText); // "earlier conditions"

        screen.Model.InputPaths.Clear();
        WaitFor(screen, () => screen.Settings.Images.Dpi == SettingsLimits.DefaultDpi, "the saved conditions did not come back");
        Assert.False(screen.Settings.Output.Chunks);
    });

    [Fact]
    public void AConditionChangedAfterItWasPutBack_IsSaved_AndStaysWhenTheInputsAreEmptied() => Sta.Run(screen =>
    {
        var a = Source(screen, "A", "a.docx");
        FakeRecords.Write(screen.Settings.Output.Folder, [a], o => o.Dpi = 222);
        screen.Model.InputPaths.Add(Folder(screen, "in", "A"));
        WaitFor(screen, () => screen.Settings.Images.Dpi == 222, "the conditions were not put back");

        screen.Model.Chunks = true;
        Assert.Equal(222, SavedDpi(screen));

        screen.Model.InputPaths.Clear();
        WaitFor(screen, () => screen.Model.Files.Count == 0, "the list was not emptied");
        Assert.Equal(222, screen.Settings.Images.Dpi);
    });

    [Fact]
    public void TheOutputFolder_FollowsTheRememberedOutputOfTheFirst_AndAChosenFolderIsKept() => Sta.Run(screen =>
    {
        var a = Source(screen, "A", "a.docx");
        var there = Folder(screen, "there");
        FakeRecords.Write(there, [a], o => o.Dpi = 180);
        screen.Store.Update(s => s.History.Remember(Folder(screen, "in", "A"), there, DateTimeOffset.Now));

        screen.Model.InputPaths.Add(Folder(screen, "in", "A"));
        WaitFor(screen, () => screen.Settings.Output.Folder == there, "the output folder did not follow the input");
        WaitFor(screen, () => screen.Settings.Images.Dpi == 180, "the conditions were not put back");

        var chosen = Folder(screen, "chosen");
        screen.Model.OutputFolder = chosen;
        WaitFor(screen, () => screen.Settings.Images.Dpi == SettingsLimits.DefaultDpi, "the conditions stayed without a record");
        Assert.Equal(chosen, screen.Settings.Output.Folder); // kept until the inputs change
    });

    [Fact]
    public void AddingAnInputConvertedIntoAnotherFolder_Asks_AndCancelChangesNothing() => Sta.Run(screen =>
    {
        var a = Source(screen, "A", "a.docx");
        var b = Source(screen, "B", "b.docx");
        var there = Folder(screen, "there");
        FakeRecords.Write(screen.Settings.Output.Folder, [a]);
        FakeRecords.Write(there, [b], o => o.Dpi = 300);
        screen.Store.Update(s => s.History.Remember(Folder(screen, "in", "B"), there, DateTimeOffset.Now));
        screen.Model.InputPaths.Add(Folder(screen, "in", "A"));
        WaitFor(screen, () => screen.Model.Files.Count == 1, "the list was not filled");

        // The texts of the screen are not loaded in the tests: the lines of the differences are checked as they are made.
        var line = Assert.Single(screen.Model.AddDifferences([Folder(screen, "in", "B")]));
        Assert.Contains("Add_OtherOutput", line); // "output folder {0}" (the text is the key without the resources)
        Assert.Contains("DPI 300", line);

        var asked = 0;
        screen.Model.ConfirmAdd = _ => { asked++; return false; };
        screen.Model.DropInputs([Folder(screen, "in", "B")]); // the records are read away from the screen
        WaitFor(screen, () => asked == 1, "it was not asked");
        Thread.Sleep(100);
        Assert.Single(screen.Model.InputPaths);

        screen.Model.ConfirmAdd = _ => true;
        screen.Model.DropInputs([Folder(screen, "in", "B")]);
        WaitFor(screen, () => screen.Model.Files.Count == 2, "the added input was not listed");
        Assert.NotEqual(there, screen.Settings.Output.Folder); // the output folder of the first
        Assert.Equal(FileState.NotConverted, screen.Model.Files.Single(f => f.FullPath == b).Entry?.State);
    });

    [Fact]
    public void AWorkSet_PutsBackItsInputsOutputAndConditions_AfterTheScreenIsReset() => Sta.Run(screen =>
    {
        Source(screen, "A", "a.docx");
        screen.Model.InputPaths.Add(Folder(screen, "in", "A"));
        screen.Model.Chunks = true;
        screen.Store.Update(s => s.Images.Dpi = 210);
        var output = screen.Settings.Output.Folder;
        screen.Model.SaveWorkSet("set 1");

        screen.Model.InputPaths.Clear();
        screen.Model.OutputFolder = Folder(screen, "other");
        screen.Model.Chunks = false;
        screen.Store.Update(s => { s.Images.Dpi = 150; s.Input.Recurse = true; });

        Assert.True(screen.Model.OpenWorkSet("set 1"));
        screen.Pump();

        Assert.Equal([Folder(screen, "in", "A")], screen.Model.InputPaths);
        Assert.Equal(output, screen.Settings.Output.Folder);
        Assert.True(screen.Settings.Output.Chunks);
        Assert.Equal(210, screen.Settings.Images.Dpi);
        Assert.False(screen.Settings.Input.Recurse); // reset to the default first, as the work set had it
        screen.Store.Flush();
        Assert.Equal(210, new SettingsStore(screen.Store.FilePath).Load().Settings.Images.Dpi); // saved
        Assert.Equal(["set 1"], screen.Model.WorkSetNames);
    });
}
