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

using System.Text.RegularExpressions;

namespace MarkPortO.Core.Vlm;

/// <summary>
/// Joins the answers for the bands of a page into the answer for the page, top to bottom (FR-041). A table that a cut
/// went through becomes one table again when the columns are the same; the empty header row that a model makes for
/// the part without a header is dropped.
/// </summary>
public static partial class BandMerger
{
    public static string Merge(IReadOnlyList<string> answers)
    {
        var merged = string.Empty;
        foreach (var answer in answers)
        {
            var text = answer.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            merged = merged.Length == 0 ? text : Join(merged, text);
        }

        return merged;
    }

    private static string Join(string previous, string next)
    {
        var previousLines = previous.Split('\n');
        var nextLines = next.Split('\n').ToList();
        var last = previousLines[^1];
        var first = nextLines[0];

        if (!IsRow(last) || !IsRow(first) || Columns(last) != Columns(first))
        {
            return previous + "\n\n" + next;
        }

        // The next part starts with a header row and its separator: an empty header row is dropped with the separator;
        // a header row that has text is a row of the table, so only the separator is dropped.
        if (nextLines.Count >= 2 && IsSeparator(nextLines[1]) && Columns(nextLines[1]) == Columns(first))
        {
            if (IsEmptyRow(first))
            {
                nextLines.RemoveRange(0, 2);
            }
            else
            {
                nextLines.RemoveAt(1);
            }
        }

        return nextLines.Count == 0 ? previous : previous + "\n" + string.Join("\n", nextLines);
    }

    private static bool IsRow(string line)
    {
        var text = line.Trim();
        return text.Length >= 2 && text[0] == '|' && text[^1] == '|';
    }

    private static bool IsSeparator(string line) => SeparatorPattern().IsMatch(line.Trim());

    private static int Columns(string line) => line.Trim().Trim('|').Split('|').Length;

    private static bool IsEmptyRow(string line) => line.Trim().Trim('|').Split('|').All(cell => cell.Trim().Length == 0);

    [GeneratedRegex(@"^\|[\s:|\-]*-[\s:|\-]*\|$")]
    private static partial Regex SeparatorPattern();
}
