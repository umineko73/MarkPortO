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
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>FR-106: the columns and the rows that only go on with merged cells are left out of a table laid out on a grid (Excel 方眼紙).</summary>
public class XlsxGridFoldTests
{
    private static string Read(SheetSpec spec)
    {
        using var dir = new TempDir();
        var path = dir.Combine("g.xlsx");
        XlsxFactory.Create(path, spec);
        return MarkdownText.ResolveMarks(XlsxReader.Read(path, false).Single().ToMarkdown());
    }

    [Fact]
    public void AGridOfMergedCells_IsFoldedToTheColumnsThatCellsStartIn()
    {
        // Eight narrow columns: No (A:B), Item (C:F), Qty (G:H). The last row has empty cells that are not merged.
        var markdown = Read(new SheetSpec("Grid",
        [
            ["No", "", "Item", "", "", "", "Qty", ""],
            ["1", "", "Paper", "", "", "", "10", ""],
            ["2", "", "Pen", "", "", "", "30", ""],
            ["", "", "", "", "", "", "Sum", ""],
        ], Merges: ["A1:B1", "C1:F1", "G1:H1", "A2:B2", "C2:F2", "G2:H2", "A3:B3", "C3:F3", "G3:H3", "G4:H4"]));

        Assert.Equal("|  |  |  |\n|---|---|---|\n| No | Item | Qty |\n| 1 | Paper | 10 |\n| 2 | Pen | 30 |\n|  |  | Sum |", markdown);
    }

    [Fact]
    public void ARowThatOnlyGoesOnWithAMergedCellAbove_IsLeftOut()
    {
        var markdown = Read(new SheetSpec("Grid",
        [
            ["Note", "", "x", ""],
            ["", "", "", ""],
            ["a", "", "b", ""],
        ], Merges: ["A1:B2", "C1:D1", "A3:B3", "C3:D3"]));

        Assert.DoesNotContain("^", markdown);
        Assert.Contains("| Note |", markdown);
    }

    [Fact]
    public void ATableWithOrdinaryMergedCells_IsNotChanged()
    {
        var markdown = Read(new SheetSpec("Data", [["Title", "", ""], ["A", "b", "c"], ["", "d", ""]], Merges: ["A1:B1", "A2:A3"]));

        Assert.Equal("|  |  |  |\n|---|---|---|\n| Title | < |  |\n| A | b | c |\n| ^ | d |  |", markdown);
    }

    [Fact]
    public void AColumnWithAValueInOneRow_Stays()
    {
        var markdown = Read(new SheetSpec("Data", [["a", "", "c"], ["d", "x", "f"]], Merges: ["A1:B1"]));

        Assert.Equal("|  |  |  |\n|---|---|---|\n| a | < | c |\n| d | x | f |", markdown);
    }
}
