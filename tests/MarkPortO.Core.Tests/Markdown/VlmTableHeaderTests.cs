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

using MarkPortO.Core.Markdown;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>The header row that the VLM writes again on a later page is not a row of the table, even when one cell of it is read differently (FR-089).</summary>
public class VlmTableHeaderTests
{
    private const string Owner = "| 番号 | 機器名 | 数量 | 場所 | 備考 |\n|---|---|---|---|---|\n| 1 | A | 2 | 1階 | |\n| 2 | B | 3 | 2階 | |";

    [Fact]
    public void AHeaderWithOneCellReadDifferently_IsNotAddedAsARow()
    {
        string[] next = ["| 番号 | 機器名 | 設置数量 | 場所 | 備考 |", "|---|---|---|---|---|", "| 3 | C | 1 | 3階 | |"];

        Assert.True(VlmTableJoiner.TryJoin(Owner, next, out var merged));
        Assert.Equal(Owner + "\n| 3 | C | 1 | 3階 | |", merged);
    }

    [Fact]
    public void ARowOfTheTable_IsStillARow_WhenTheLinesCarryNoHeader()
    {
        string[] next = ["| 3 | C | 1 | 3階 | |", "| 4 | D | 1 | 3階 | |"];

        Assert.True(VlmTableJoiner.TryJoin(Owner, next, out var merged));
        Assert.Equal(Owner + "\n| 3 | C | 1 | 3階 | |\n| 4 | D | 1 | 3階 | |", merged);
    }

    [Fact]
    public void ARowThatTheVlmTookForAHeader_IsKept_WhenItIsNotTheHeader()
    {
        string[] next = ["| 3 | C | 1 | 3階 | |", "|---|---|---|---|---|", "| 4 | D | 1 | 3階 | |"];

        Assert.True(VlmTableJoiner.TryJoin(Owner, next, out var merged));
        Assert.Contains("| 3 | C | 1 | 3階 | |", merged);
        Assert.Equal(2, merged.Split('\n').Count(l => l.StartsWith("| 3 ", StringComparison.Ordinal) || l.StartsWith("| 4 ", StringComparison.Ordinal)));
    }
}
