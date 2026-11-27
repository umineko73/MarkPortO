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

using System.Windows;
using static MarkPortO.App.Tests.PresetViewModelTests;

namespace MarkPortO.App.Tests;

/// <summary>The message in the middle of the window while the screen waits before it can be used again.</summary>
public sealed class WaitMessageViewModelTests
{
    private static void Pump(Screen screen, TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            screen.Pump();
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void ALongWait_ShowsItsMessage_UntilItEnds() => Sta.Run(screen =>
    {
        var done = new TaskCompletionSource();
        var wait = screen.Model.WhileWaitingAsync(() => "Reading", () => done.Task);

        Pump(screen, MarkPortO.App.ViewModels.MainViewModel.WaitShownAfter * 2);
        Assert.Equal(Visibility.Visible, screen.Model.WaitVisibility);
        Assert.Equal("Reading", screen.Model.WaitMessage);

        done.SetResult();
        Pump(screen, TimeSpan.FromMilliseconds(100));
        Assert.True(wait.IsCompleted);
        Assert.Equal(Visibility.Collapsed, screen.Model.WaitVisibility);
    });

    [Fact]
    public void AShortWait_IsNotShown_NorByTheDelayOfItForALaterOne() => Sta.Run(screen =>
    {
        screen.Model.WhileWaitingAsync(() => "Reading", () => Task.CompletedTask).GetAwaiter().GetResult();
        Pump(screen, MarkPortO.App.ViewModels.MainViewModel.WaitShownAfter / 2);
        var later = new TaskCompletionSource();
        _ = screen.Model.WhileWaitingAsync(() => "Checking", () => later.Task);

        Pump(screen, MarkPortO.App.ViewModels.MainViewModel.WaitShownAfter * 0.7); // the first delay is over, the second is not
        Assert.Equal(Visibility.Collapsed, screen.Model.WaitVisibility);

        later.SetResult();
        Pump(screen, MarkPortO.App.ViewModels.MainViewModel.WaitShownAfter * 1.5);
        Assert.Equal(Visibility.Collapsed, screen.Model.WaitVisibility);
    });

    [Fact]
    public void InputsDroppedWhileOthersAreChecked_AreAddedAfterThem() => Sta.Run(screen => // MP-07
    {
        var root = System.IO.Path.GetDirectoryName(screen.Store.FilePath)!;
        string Folder(string name) => System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "in", name)).FullName;
        var (a, b) = (Folder("A"), Folder("B"));

        screen.Model.DropInputs([a]); // checked away from the screen
        screen.Model.DropInputs([b]); // dropped before the first is checked

        var until = DateTime.UtcNow.AddSeconds(10);
        while (screen.Model.InputPaths.Count < 2 && DateTime.UtcNow < until)
        {
            screen.Pump();
            Thread.Sleep(20);
        }

        Assert.Equal([a, b], screen.Model.InputPaths);
    });

    [Fact]
    public void WhileTheMessageIsShown_TheWindowTakesNoKeys() => Sta.Run(screen => // MP-07
    {
        Assert.True(screen.Model.CanUse);
        var done = new TaskCompletionSource();
        _ = screen.Model.WhileWaitingAsync(() => "Reading", () => done.Task);

        Pump(screen, MarkPortO.App.ViewModels.MainViewModel.WaitShownAfter * 2);
        Assert.False(screen.Model.CanUse);

        done.SetResult();
        Pump(screen, TimeSpan.FromMilliseconds(100));
        Assert.True(screen.Model.CanUse);
    });
}
