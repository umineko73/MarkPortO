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

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// "Group nearby shapes" (FR-136), the one rule for a slide, a Word page and a sheet: shapes whose heights meet, within the gap
/// (<see cref="PictureCollector.GroupGapPoints"/>), are cut out as one picture. On the same row they are together however far apart they are
/// across (shapes in a row that were cut out in a column would change the look of the page); above and below one another, only when they are near.
/// </summary>
public static class ShapeGrouping
{
    /// <summary>Two boxes (their tops and bottoms, in one unit, as <paramref name="gap"/>) are together: their heights meet, within the gap.</summary>
    public static bool Together(double topA, double bottomA, double topB, double bottomB, double gap) =>
        topA - gap <= bottomB && topB - gap <= bottomA;

    /// <summary>
    /// The groups of boxes that are together (<see cref="Together"/>), also through others: each group is the indexes of its boxes in order, and the
    /// groups are in the order of their first boxes. A box that is <paramref name="alone"/> (a background) is a group of its own.
    /// </summary>
    public static List<List<int>> Groups(IReadOnlyList<(double Top, double Bottom)> boxes, double gap, Func<int, bool>? alone = null)
    {
        var parent = Enumerable.Range(0, boxes.Count).ToArray();
        int Root(int i)
        {
            while (parent[i] != i)
            {
                i = parent[i] = parent[parent[i]];
            }

            return i;
        }

        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                if (alone?.Invoke(i) != true && alone?.Invoke(j) != true && Together(boxes[i].Top, boxes[i].Bottom, boxes[j].Top, boxes[j].Bottom, gap))
                {
                    parent[Root(j)] = Root(i);
                }
            }
        }

        return [.. Enumerable.Range(0, boxes.Count).GroupBy(Root).Select(g => g.ToList())];
    }
}
