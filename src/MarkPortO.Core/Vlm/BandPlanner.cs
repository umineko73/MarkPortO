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

using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Vlm;

/// <summary>A strip of a page that is read in one request (FR-041). The top and bottom are fractions of the height of the page.</summary>
/// <param name="FirstLine">Index of the first text line in the strip.</param>
/// <param name="LineCount">How many text lines the strip holds.</param>
public sealed record Band(double Top, double Bottom, int FirstLine, int LineCount);

/// <summary>
/// Cuts a page with many lines into bands of at most so many lines (FR-041). A cut is made only in the empty space
/// between two lines, so no line is split. The bands have about the same number of lines.
/// </summary>
public static class BandPlanner
{
    /// <summary>The least empty space (a fraction of the page height) in which a cut may be made.</summary>
    public const double MinGap = 0.002;

    /// <summary>
    /// The bands, from the top. Empty when the page is not to be split: no limit, no line information, not more
    /// lines than the limit, or no place to cut.
    /// </summary>
    public static IReadOnlyList<Band> Plan(IReadOnlyList<LineSpan>? lines, int maxLines)
    {
        if (lines is null || maxLines <= 0 || lines.Count <= maxLines)
        {
            return [];
        }

        var count = lines.Count;
        var canCut = new bool[count]; // canCut[k]: a cut may be made before line k
        for (var k = 1; k < count; k++)
        {
            canCut[k] = lines[k].Top - lines[k - 1].Bottom >= MinGap;
        }

        var wanted = (int)Math.Ceiling(count / (double)maxLines);
        var target = (int)Math.Ceiling(count / (double)wanted);

        var starts = new List<int> { 0 };
        var position = 0;
        while (count - position > maxLines)
        {
            var cut = FindCut(canCut, position, target, maxLines, count);
            if (cut < 0)
            {
                break;
            }

            starts.Add(cut);
            position = cut;
        }

        if (starts.Count == 1)
        {
            return [];
        }

        var bands = new List<Band>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var first = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : count;
            var top = i == 0 ? 0.0 : Edge(lines, first, upper: true);
            var bottom = i + 1 == starts.Count ? 1.0 : Edge(lines, end, upper: false);
            bands.Add(new Band(top, bottom, first, end - first));
        }

        return bands;
    }

    /// <summary>
    /// Where to cut the band that starts at <paramref name="position"/>: the allowed place nearest to the wanted size,
    /// within the limit; else the nearest one after it; else none.
    /// </summary>
    private static int FindCut(bool[] canCut, int position, int target, int maxLines, int count)
    {
        var wanted = position + target;
        var limit = Math.Min(position + maxLines, count - 1);

        var best = -1;
        for (var k = position + 1; k <= limit; k++)
        {
            if (canCut[k] && (best < 0 || Math.Abs(k - wanted) < Math.Abs(best - wanted)))
            {
                best = k;
            }
        }

        if (best >= 0)
        {
            return best;
        }

        for (var k = limit + 1; k < count; k++)
        {
            if (canCut[k])
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>
    /// The edge of a band at the cut before line <paramref name="line"/>: the middle of the space between the lines,
    /// moved by a third of the space into the neighbouring band, so that a rule drawn there is in both bands.
    /// </summary>
    private static double Edge(IReadOnlyList<LineSpan> lines, int line, bool upper)
    {
        var gap = lines[line].Top - lines[line - 1].Bottom;
        var middle = lines[line - 1].Bottom + (gap / 2);
        return upper ? middle - (gap / 3) : middle + (gap / 3);
    }
}
