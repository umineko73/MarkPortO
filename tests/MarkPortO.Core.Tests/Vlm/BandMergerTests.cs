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

using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Vlm;

/// <summary>FR-041: the answers for the bands become the answer for the page, and a table cut by a band is one table again.</summary>
public class BandMergerTests
{
    [Fact]
    public void OneAnswer_IsReturnedAsItIs()
    {
        Assert.Equal("text", BandMerger.Merge(["text"]));
    }

    [Fact]
    public void Paragraphs_AreJoinedWithABlankLine_InOrder()
    {
        Assert.Equal("first\n\nsecond\n\nthird", BandMerger.Merge(["first", "second", "third"]));
    }

    [Fact]
    public void ATableContinuedWithoutAHeader_IsJoinedDirectly()
    {
        var a = "| No | Name |\n|---|---|\n| 1 | a |\n| 2 | b |";
        var b = "| 3 | c |\n| 4 | d |";

        Assert.Equal("| No | Name |\n|---|---|\n| 1 | a |\n| 2 | b |\n| 3 | c |\n| 4 | d |", BandMerger.Merge([a, b]));
    }

    [Fact]
    public void ATableContinuedWithAnEmptyHeaderRow_DropsTheEmptyRowAndTheSeparator()
    {
        var a = "| No | Name |\n|---|---|\n| 1 | a |";
        var b = "|  |  |\n|---|---|\n| 2 | b |\n| 3 | c |";

        Assert.Equal("| No | Name |\n|---|---|\n| 1 | a |\n| 2 | b |\n| 3 | c |", BandMerger.Merge([a, b]));
    }

    [Fact]
    public void ATableContinuedWithADataRowAsHeader_KeepsThatRowAsData()
    {
        var a = "| No | Name |\n|---|---|\n| 1 | a |";
        var b = "| 2 | b |\n|---|---|\n| 3 | c |";

        Assert.Equal("| No | Name |\n|---|---|\n| 1 | a |\n| 2 | b |\n| 3 | c |", BandMerger.Merge([a, b]));
    }

    [Fact]
    public void TablesWithADifferentNumberOfColumns_AreNotMerged()
    {
        var a = "| No | Name |\n|---|---|\n| 1 | a |";
        var b = "| 2 | b | x |\n| 3 | c | y |";

        Assert.Equal(a + "\n\n" + b, BandMerger.Merge([a, b]));
    }

    [Fact]
    public void ATableAfterAParagraph_IsNotMergedWithIt()
    {
        var a = "Title";
        var b = "| 1 | a |\n| 2 | b |";

        Assert.Equal("Title\n\n| 1 | a |\n| 2 | b |", BandMerger.Merge([a, b]));
    }

    [Fact]
    public void EmptyAnswers_AreSkipped()
    {
        Assert.Equal("a\n\nb", BandMerger.Merge(["a", "  \n", "b"]));
    }

    [Fact]
    public void ThreeBands_AreMergedIntoOneTable()
    {
        var a = "| No | Name |\n|---|---|\n| 1 | a |";
        var b = "| 2 | b |";
        var c = "|  |  |\n|---|---|\n| 3 | c |";

        Assert.Equal("| No | Name |\n|---|---|\n| 1 | a |\n| 2 | b |\n| 3 | c |", BandMerger.Merge([a, b, c]));
    }
}
