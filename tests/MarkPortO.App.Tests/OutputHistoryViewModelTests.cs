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

using MarkPortO.App.ViewModels;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.App.Tests.PresetViewModelTests;

namespace MarkPortO.App.Tests;

/// <summary>The list of the output folders remembered for the inputs (FR-156), and forgetting some of them.</summary>
public sealed class OutputHistoryViewModelTests
{
    [Fact]
    public void TheHistory_IsListedNewestFirst_AndTheOnesChosenAreForgotten() => Sta.Run(screen =>
    {
        var now = DateTimeOffset.Now;
        screen.Store.Update(s =>
        {
            s.History.Remember(@"C:\in\A", @"D:\out1", now.AddMinutes(-2));
            s.History.Remember(@"C:\in\B", @"D:\out2", now.AddMinutes(-1));
            s.History.Remember(@"C:\in\C", @"D:\out3", now);
        });

        Assert.Equal([@"C:\in\C", @"C:\in\B", @"C:\in\A"], screen.Model.OutputHistory.Select(o => o.Input));

        screen.Model.ForgetOutputs([@"c:\in\b", @"C:\in\C"]); // without case, as inputs are compared

        Assert.Equal([@"C:\in\A"], screen.Model.OutputHistory.Select(o => o.Input));
        Assert.Equal(@"D:\out1", screen.Settings.History.OutputOf(@"C:\in\A"));

        screen.Model.ClearHistory();
        Assert.Empty(screen.Model.OutputHistory);
    });

    [Fact]
    public void TheStateOfEachInput_SaysWhatGoingBackWouldFind() => Sta.Run(screen =>
    {
        var root = System.IO.Path.GetDirectoryName(screen.Store.FilePath)!;
        string Folder(params string[] parts) => System.IO.Directory.CreateDirectory(System.IO.Path.Combine([root, .. parts])).FullName;
        var (input, empty, converted) = (Folder("in", "A"), Folder("out", "empty"), Folder("out", "converted"));
        var file = System.IO.Path.Combine(Folder("in", "C"), "c.docx");
        System.IO.File.WriteAllBytes(file, [1, 2, 3]);
        FakeRecords.Write(converted, [file]);
        var now = DateTimeOffset.Now;
        screen.Store.Update(s =>
        {
            s.History.Remember(System.IO.Path.Combine(root, "in", "gone"), empty, now.AddMinutes(-3));
            s.History.Remember(input, System.IO.Path.Combine(root, "out", "gone"), now.AddMinutes(-2));
            s.History.Remember(Folder("in", "B"), empty, now.AddMinutes(-1));
            s.History.Remember(System.IO.Path.GetDirectoryName(file)!, converted, now);
        });

        var states = screen.Model.OutputHistoryStates().Select(h => h.State);

        Assert.Equal(
            [MainViewModel.HistoryState.Ready, MainViewModel.HistoryState.NoRecord, MainViewModel.HistoryState.OutputGone, MainViewModel.HistoryState.InputGone],
            states);
    });

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

    [Fact]
    public void GoingBackAgain_PutsBackTheConditionsOfTheRecord_ThoughTheyWereChanged() => Sta.Run(screen => // history review R1-05
    {
        var root = System.IO.Path.GetDirectoryName(screen.Store.FilePath)!;
        var folder = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "in", "A")).FullName;
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "a.docx"), [1, 2, 3]);
        var output = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "out", "1")).FullName;
        FakeRecords.Write(output, [System.IO.Path.Combine(folder, "a.docx")], o => o.Dpi = 222);

        Assert.True(screen.Model.OpenFromHistory(folder, output));
        WaitFor(screen, () => screen.Settings.Images.Dpi == 222, "the conditions were not put back");
        screen.Model.DpiText = "300";
        WaitFor(screen, () => screen.Settings.Images.Dpi == 300, "the DPI was not changed");

        Assert.True(screen.Model.OpenFromHistory(folder, output));
        WaitFor(screen, () => screen.Settings.Images.Dpi == 222, "going back again did not put the conditions back");
    });

    [Fact]
    public void GoingBackToAnInputWithoutARecord_PutsBackTheSavedConditions() => Sta.Run(screen => // history review R2-01
    {
        var root = System.IO.Path.GetDirectoryName(screen.Store.FilePath)!;
        var a = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "in", "A")).FullName;
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(a, "a.docx"), [1, 2, 3]);
        var b = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "in", "B")).FullName;
        var output = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "out", "1")).FullName;
        FakeRecords.Write(output, [System.IO.Path.Combine(a, "a.docx")], o => o.Dpi = 222);
        var saved = screen.Settings.Images.Dpi;

        Assert.True(screen.Model.OpenFromHistory(a, output));
        WaitFor(screen, () => screen.Settings.Images.Dpi == 222, "the conditions of A were not put back");

        Assert.True(screen.Model.OpenFromHistory(b, output)); // B has no record there
        WaitFor(screen, () => screen.Settings.Images.Dpi == saved, "the conditions of A stayed");
    });

    [Fact]
    public void InputsWaitingToBeAdded_AreNotAddedToTheListPutIn() => Sta.Run(screen => // history review R2-04
    {
        var root = System.IO.Path.GetDirectoryName(screen.Store.FilePath)!;
        string Folder(params string[] parts) => System.IO.Directory.CreateDirectory(System.IO.Path.Combine([root, .. parts])).FullName;
        var (a, b, c, output) = (Folder("in", "A"), Folder("in", "B"), Folder("in", "C"), Folder("out", "1"));

        screen.Model.DropInputs([a]);
        screen.Model.DropInputs([b]); // waits for A
        Assert.True(screen.Model.OpenFromHistory(c, output));

        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            screen.Pump();
            Thread.Sleep(20);
        }

        Assert.Equal([c], screen.Model.InputPaths);
    });

    [Fact]
    public void AnInputAddedJustBeforeGoingBack_IsNotAddedToTheListPutIn() => Sta.Run(screen => // history review R1-03
    {
        var root = System.IO.Path.GetDirectoryName(screen.Store.FilePath)!;
        string Folder(params string[] parts) => System.IO.Directory.CreateDirectory(System.IO.Path.Combine([root, .. parts])).FullName;
        var (a, b, output) = (Folder("in", "A"), Folder("in", "B"), Folder("out", "1"));

        screen.Model.DropInputs([a]); // checked away from the screen
        Assert.True(screen.Model.OpenFromHistory(b, output)); // before the check of A ends

        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            screen.Pump();
            Thread.Sleep(20);
        }

        Assert.Equal([b], screen.Model.InputPaths);
    });

    [Fact]
    public void GoingBackToAnInputOfTheHistory_PutsItAloneInTheList_WithItsOutputFolder() => Sta.Run(screen =>
    {
        var root = System.IO.Path.GetDirectoryName(screen.Store.FilePath)!;
        string Folder(params string[] parts) => System.IO.Directory.CreateDirectory(System.IO.Path.Combine([root, .. parts])).FullName;
        var (a, b, output) = (Folder("in", "A"), Folder("in", "B"), Folder("out", "1"));
        screen.Model.InputPaths.Add(a);

        Assert.True(screen.Model.OpenFromHistory(b, output));

        Assert.Equal([b], screen.Model.InputPaths);
        Assert.Equal(output, screen.Model.OutputFolder);
        Assert.Equal([b], screen.Settings.Input.Paths); // saved, as a choice of the inputs is

        // An input or an output folder that is gone: nothing changes.
        Assert.False(screen.Model.OpenFromHistory(System.IO.Path.Combine(root, "in", "gone"), output));
        Assert.False(screen.Model.OpenFromHistory(a, System.IO.Path.Combine(root, "out", "gone")));
        Assert.Equal([b], screen.Model.InputPaths);
        Assert.Equal(output, screen.Model.OutputFolder);
    });
}
