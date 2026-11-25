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
using static MarkPortO.App.Tests.PresetViewModelTests;

namespace MarkPortO.App.Tests;

/// <summary>FR-157: the list shows the files of the inputs with their last record; the buttons follow it.</summary>
public sealed class FileListViewModelTests
{
    private static void WaitFor(Screen screen, Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "the list was not filled in time");
            screen.Pump();
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void InputsNeverConverted_AreListedAsNotConverted_AndOnlyStartIsOffered() => Sta.Run(screen =>
    {
        var folder = Path.Combine(Path.GetDirectoryName(screen.Store.FilePath)!, "in");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.docx"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "b.xlsx"), [1]);

        screen.Model.InputPaths.Add(folder);
        WaitFor(screen, () => screen.Model.Files.Count == 2);

        Assert.All(screen.Model.Files, f => Assert.Equal(FileState.NotConverted, f.Entry?.State));
        Assert.True(screen.Model.StartCommand.CanExecute(null));
        Assert.False(screen.Model.RedoAllCommand.CanExecute(null));
        Assert.False(screen.Model.ResumeCommand.CanExecute(null));
        Assert.False(screen.Model.StartChangedCommand.CanExecute(null));

        screen.Model.InputPaths.Clear();
        WaitFor(screen, () => screen.Model.Files.Count == 0);
    });
}
