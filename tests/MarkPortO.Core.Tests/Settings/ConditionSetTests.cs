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
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using MarkPortO.Core.Tests.Localization;

namespace MarkPortO.Core.Tests.Settings;

/// <summary>FR-156〜FR-160: the items of the conditions, the memory of output folders, and the first input.</summary>
public class ConditionSetTests : JapaneseUiTests
{
    private static string Source(TempDir dir, string folder, string name)
    {
        var path = dir.Combine("in", folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public void TheGapOfTheShapesCutOutTogether_IsOneOfTheConditions() // MP-06
    {
        var from = AppSettings.CreateDefault();
        from.Input.ShapeGroupGapPoints = 81;
        var to = AppSettings.CreateDefault();

        ConditionSet.From(from).ApplyTo(to);

        Assert.Equal(81, to.Input.ShapeGroupGapPoints);
        Assert.False(ConditionSet.From(from).SameAs(ConditionSet.From(AppSettings.CreateDefault())));
        Assert.Equal(24, new ConditionSet().ShapeGroupGapPoints); // a working set saved before the gap was one of them
    }

    [Fact]
    public void TheConditions_GoIntoOtherSettings_AndLeaveTheInputsTheOutputAndTheConnectionsAlone()
    {
        var from = AppSettings.CreateDefault();
        from.Images.Dpi = 222;
        from.Steps.Step3 = true;
        from.Output.ExcelPerSheet = true;
        from.Input.Types = [DocumentKind.Xlsx];
        from.Vlm.Selected = "gone";
        var to = AppSettings.CreateDefault();
        to.Input.Paths = ["C:\\a"];
        to.Output.Folder = "C:\\out";
        to.Vlm.Connections.Add(new ConnectionProfile { Name = "here", BaseUrl = "http://localhost/v1", Model = "m" });
        to.Vlm.Selected = "here";

        Assert.False(ConditionSet.From(from).ApplyTo(to)); // the connection "gone" is not there: the one chosen stays

        Assert.Equal(222, to.Images.Dpi);
        Assert.True(to.Steps.Step3 && to.Output.ExcelPerSheet);
        Assert.Equal([DocumentKind.Xlsx], to.Input.Types);
        Assert.Equal(["C:\\a"], to.Input.Paths);
        Assert.Equal("C:\\out", to.Output.Folder);
        Assert.Equal("here", to.Vlm.Selected);
        Assert.True(ConditionSet.From(from).SameAs(ConditionSet.From(to)) is false); // only the connection differs, and Step3 is on
        Assert.Equal(["接続 gone"], ConditionSet.From(from).Differences(ConditionSet.From(to)));
    }

    [Fact]
    public void TheDifferences_NameTheItemWithTheValueOfTheAddedOne()
    {
        var a = ConditionSet.From(AppSettings.CreateDefault());
        var b = a.Clone();
        b.Dpi = 200;
        b.Step3 = true;
        Assert.Equal(["DPI 150", "Step3 オフ"], a.Differences(b));
        Assert.Empty(a.Differences(a.Clone()));
    }

    [Fact]
    public void TheRecordOfADocument_GivesItsOwnSteps_AlsoAStepAddedLater()
    {
        var job = new JobState { Options = new OptionsSnapshot { Dpi = 180, Step2 = true, Step3 = false, Preset = "rag (modified)" } };
        var document = new DocumentRecord { Step1 = StepStatus.Done, Step2 = StepStatus.Done, Step3 = StepStatus.Done, Chunks = true };

        var conditions = ConditionSet.FromRecord(job, document);

        Assert.True(conditions.Step3); // added to this document, though the job has it off
        Assert.Equal(180, conditions.Dpi);
        Assert.True(conditions.Chunks);
        Assert.Equal(string.Empty, conditions.Preset); // changed after the preset was chosen
    }

    [Fact]
    public void TheMemory_KeepsTheNewestOutputOfAnInput_AndAtMost200()
    {
        var history = new HistorySettings();
        history.Remember("C:\\in\\A\\", "C:\\out1", DateTimeOffset.Now);
        history.Remember("c:\\in\\a", "C:\\out2", DateTimeOffset.Now);
        Assert.Single(history.Outputs);
        Assert.Equal("C:\\out2", history.OutputOf("C:\\IN\\A"));

        for (var i = 0; i < 250; i++)
        {
            history.Remember($"C:\\in\\{i}", "C:\\out", DateTimeOffset.Now);
        }

        Assert.Equal(HistorySettings.MaxOutputs, history.Outputs.Count);
        Assert.Null(history.OutputOf("C:\\in\\A"));
        Assert.Equal("C:\\out", history.OutputOf("C:\\in\\249"));
    }

    [Fact]
    public void ConditionsShown_AreNotSaved_UntilOneIsChanged_AndComeBackWhenReverted()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        using var store = new SettingsStore(path, TimeSpan.FromMilliseconds(1));
        store.Load();
        var shown = ConditionSet.From(store.Current);
        shown.Dpi = 300;

        store.ShowConditions(shown);
        store.Update(s => s.Output.Folder = dir.Combine("out")); // not a condition
        store.Flush();
        Assert.Equal(300, store.Current.Images.Dpi);
        Assert.True(store.IsHoldingConditions);
        var saved = new SettingsStore(path);
        Assert.Equal(SettingsLimits.DefaultDpi, saved.Load().Settings.Images.Dpi);
        Assert.Equal(dir.Combine("out"), saved.Current.Output.Folder);

        Assert.True(store.RevertConditions());
        Assert.Equal(SettingsLimits.DefaultDpi, store.Current.Images.Dpi);

        store.ShowConditions(shown);
        store.Update(s => s.Output.Chunks = true); // the user changes a condition: everything on the screen is saved
        store.Flush();
        Assert.False(store.IsHoldingConditions);
        var after = new SettingsStore(path).Load().Settings;
        Assert.Equal(300, after.Images.Dpi);
        Assert.True(after.Output.Chunks);
        Assert.False(store.RevertConditions());
    }

    [Fact]
    public void TheFirst_IsTheFirstInputWithARecordHere_ElseTheFirstWithARememberedOutputThatHasIt()
    {
        using var dir = new TempDir();
        var a = Source(dir, "A", "a.docx");
        var b = Source(dir, "B", "b.docx");
        var here = dir.Combine("here");
        var there = dir.Combine("there");
        FakeRecords.Write(there, [a], o => o.Dpi = 222);
        FakeRecords.Write(here, [b], o => o.Dpi = 111);
        var history = new HistorySettings();
        history.Remember(dir.Combine("in", "A"), there, DateTimeOffset.Now);

        var first = PreviousState.FindFirst(here, [dir.Combine("in", "A"), dir.Combine("in", "B")], history, out _);
        Assert.Equal((1, false), (first!.Index, first.Remembered)); // B has records here: here wins

        first = PreviousState.FindFirst(dir.Combine("empty"), [dir.Combine("in", "A"), dir.Combine("in", "B")], history, out _);
        Assert.Equal((0, true, there), (first!.Index, first.Remembered, first.Output));
        Assert.Equal(222, PreviousState.ConditionsOf(there, dir.Combine("in", "A"))!.Conditions.Dpi);

        history.Remember(dir.Combine("in", "A"), dir.Combine("gone"), DateTimeOffset.Now);
        Assert.Null(PreviousState.FindFirst(dir.Combine("empty"), [dir.Combine("in", "A")], history, out var missing));
        Assert.Equal(dir.Combine("gone"), missing);
    }

    [Fact]
    public void TheNewestConversionOfTheFirst_GivesTheConditions_AndTheOthersAreCounted()
    {
        using var dir = new TempDir();
        var a1 = Source(dir, "A", "a1.docx");
        var a2 = Source(dir, "A", "a2.docx");
        var output = dir.Combine("out");
        FakeRecords.Write(output, [a1], o => o.Dpi = 100, DateTime.UtcNow.AddHours(-1));
        FakeRecords.Write(output, [a2], o => o.Dpi = 200, DateTime.UtcNow);

        var recorded = PreviousState.ConditionsOf(output, dir.Combine("in", "A"))!;

        Assert.Equal(200, recorded.Conditions.Dpi);
        Assert.Equal(1, recorded.OthersDiffer);
    }
}
