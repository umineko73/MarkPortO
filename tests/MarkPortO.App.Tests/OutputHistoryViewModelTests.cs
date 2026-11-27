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
}
