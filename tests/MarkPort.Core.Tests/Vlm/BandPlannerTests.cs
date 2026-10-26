// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Vlm;

namespace MarkPort.Core.Tests.Vlm;

/// <summary>FR-041: cutting a page into bands of at most so many lines, only between lines.</summary>
public class BandPlannerTests
{
    /// <summary>Lines of height 0.01 every 0.02 from the top of the page; the lines in <paramref name="touching"/> touch the line before them.</summary>
    private static List<LineSpan> Lines(int count, params int[] touching)
    {
        var list = new List<LineSpan>();
        for (var i = 0; i < count; i++)
        {
            var top = 0.05 + (i * 0.02);
            list.Add(touching.Contains(i)
                ? new LineSpan(list[i - 1].Bottom, list[i - 1].Bottom + 0.01)
                : new LineSpan(top, top + 0.01));
        }

        return list;
    }

    [Fact]
    public void WithLimit0_ThePageIsNeverSplit()
    {
        Assert.Empty(BandPlanner.Plan(Lines(44), 0));
    }

    [Fact]
    public void AtOrBelowTheLimit_ThePageIsNotSplit()
    {
        Assert.Empty(BandPlanner.Plan(Lines(15), 15));
        Assert.Empty(BandPlanner.Plan(Lines(3), 15));
    }

    [Fact]
    public void WithoutLineInformation_ThePageIsNotSplit()
    {
        Assert.Empty(BandPlanner.Plan(null, 15));
        Assert.Empty(BandPlanner.Plan([], 15));
    }

    [Fact]
    public void FortyFourLinesWithLimit15_AreThreeBands_AndEveryLineIsInOne()
    {
        var bands = BandPlanner.Plan(Lines(44), 15);

        Assert.Equal(3, bands.Count);
        Assert.All(bands, b => Assert.InRange(b.LineCount, 1, 15));
        Assert.Equal(44, bands.Sum(b => b.LineCount));
        Assert.Equal(0, bands[0].FirstLine);
        for (var i = 1; i < bands.Count; i++)
        {
            Assert.Equal(bands[i - 1].FirstLine + bands[i - 1].LineCount, bands[i].FirstLine);
        }
    }

    [Fact]
    public void TheBandsAreBalanced_NotALargeBandAndATinyOne()
    {
        var bands = BandPlanner.Plan(Lines(31), 15);

        Assert.Equal(3, bands.Count);
        Assert.Equal([11, 11, 9], bands.Select(b => b.LineCount));
    }

    [Fact]
    public void ABand_ContainsItsLinesWhole_AndNoPartOfAnotherLine()
    {
        var lines = Lines(44);
        var bands = BandPlanner.Plan(lines, 15);

        foreach (var band in bands)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var inside = i >= band.FirstLine && i < band.FirstLine + band.LineCount;
                if (inside)
                {
                    Assert.True(band.Top <= lines[i].Top && band.Bottom >= lines[i].Bottom, $"line {i} is cut");
                }
                else
                {
                    Assert.True(band.Bottom <= lines[i].Top || band.Top >= lines[i].Bottom, $"line {i} reaches into the band");
                }
            }
        }
    }

    [Fact]
    public void TheFirstBandStartsAtTheTop_AndTheLastEndsAtTheBottom()
    {
        var bands = BandPlanner.Plan(Lines(44), 15);

        Assert.Equal(0.0, bands[0].Top);
        Assert.Equal(1.0, bands[^1].Bottom);
    }

    [Fact]
    public void ACutIsNeverMadeWhereTwoLinesTouch()
    {
        // Lines 14 and 15 (0-based) touch: the balanced cut after line 15 is moved to a place with a gap.
        var lines = Lines(44, 15);
        var bands = BandPlanner.Plan(lines, 15);

        Assert.All(bands, b => Assert.InRange(b.LineCount, 1, 15));
        foreach (var band in bands.Skip(1))
        {
            Assert.NotEqual(15, band.FirstLine);
        }
    }

    [Fact]
    public void WhenNoLineBreakHasAGap_ThePageIsNotSplit()
    {
        var lines = Lines(20, Enumerable.Range(1, 19).ToArray());

        Assert.Empty(BandPlanner.Plan(lines, 5));
    }

    [Fact]
    public void WhenTheOnlyGapIsFarAway_TheBandMayBeLongerThanTheLimit()
    {
        // The only possible cut is after line 20: a band of 20 is better than not splitting at all.
        var lines = Lines(40, Enumerable.Range(1, 39).Where(i => i != 20).ToArray());

        var bands = BandPlanner.Plan(lines, 15);

        Assert.Equal(2, bands.Count);
        Assert.Equal([20, 20], bands.Select(b => b.LineCount));
    }
}
