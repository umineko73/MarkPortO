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
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Markdown;

/// <summary>
/// Leaves out the tables that a small VLM writes again after the last table of a page (FR-124): it does not stop where the image ends, and goes on
/// with the tables it has already written, often with a cell more or less in a row. A table is a repeat when a table of the same page before it has
/// the same cells in the same rows (the empty cells do not count). The title line just before a repeated table is left out too, when the same line
/// is on the page before. The result of the VLM in the job is not changed: this is done when the Markdown is written, so it can be switched on and off.
/// </summary>
internal static class VlmRepeatedTables
{
    /// <summary>A table of fewer data rows than this is never taken as a repeat: a small table can be written twice on purpose.</summary>
    private const int MinimumDataRows = 2;

    /// <summary>The results of the pages without the repeated tables; only the pages that changed are in it.</summary>
    public static IReadOnlyDictionary<int, string> Apply(DocumentRecord document, IReadOnlyDictionary<int, string> vlmTexts, string language)
    {
        var mark = "> " + Loc.M("A table that repeats an earlier table of this page is left out here.", language);
        var result = new Dictionary<int, string>(vlmTexts);
        foreach (var page in document.Pages)
        {
            if (page.IsDeleted || page.Step3 != PageStep3Status.Done || page.VlmText is null)
            {
                continue;
            }

            var text = vlmTexts.TryGetValue(page.Index, out var joined) ? joined : page.VlmText.Trim();
            var done = Remove(text, mark);
            if (done != text)
            {
                result[page.Index] = done;
            }
        }

        return result;
    }

    /// <summary>The text without the tables that repeat a table before them.</summary>
    internal static string Remove(string text, string mark)
    {
        var lines = text.Split('\n');
        var output = new List<string>();
        var seenTables = new List<string>();
        var changed = false;
        var i = 0;
        while (i < lines.Length)
        {
            if (!IsTableLine(lines[i]))
            {
                output.Add(lines[i]);
                i++;
                continue;
            }

            var start = i;
            while (i < lines.Length && IsTableLine(lines[i]))
            {
                i++;
            }

            var table = lines[start..i];
            var key = KeyOf(table);
            if (key is not null && seenTables.Contains(key))
            {
                DropTitleBefore(output, mark);
                output.Add(mark);
                changed = true;
                continue;
            }

            if (key is not null)
            {
                seenTables.Add(key);
            }

            output.AddRange(table);
        }

        if (!changed)
        {
            return text;
        }

        // The marks of the left-out tables are one for each run of them; empty lines are tidied.
        var tidy = new List<string>();
        foreach (var line in output)
        {
            if (line == mark && tidy.LastOrDefault(l => l.Length > 0) == mark)
            {
                continue;
            }

            if (line.Length == 0 && tidy.Count > 0 && tidy[^1].Length == 0)
            {
                continue;
            }

            tidy.Add(line);
        }

        return string.Join('\n', tidy).Trim('\n');
    }

    // The line before a repeated table, when the page has the same line before it: it is the title of the repeat.
    private static void DropTitleBefore(List<string> output, string mark)
    {
        var at = output.Count - 1;
        while (at >= 0 && output[at].Length == 0)
        {
            at--;
        }

        if (at < 0 || output[at] == mark || IsTableLine(output[at]) || Normalise(output[at]) is not { Length: > 0 } title)
        {
            return;
        }

        if (output.Take(at).Any(line => !IsTableLine(line) && Normalise(line) == title))
        {
            output.RemoveRange(at, output.Count - at);
        }
    }

    private static string Normalise(string line) => line.Trim().TrimStart('#').Trim();

    private static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

    /// <summary>The non-empty cells of every row, as one text; <c>null</c> for a table of too few rows to take as a repeat.</summary>
    private static string? KeyOf(string[] table)
    {
        var rows = new List<string>();
        foreach (var line in table)
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (cells.Count == 0 || cells.All(c => c.All(ch => ch is '-' or ':')))
            {
                continue; // the separator row and an empty row
            }

            rows.Add(string.Join('\u001f', cells));
        }

        return rows.Count > MinimumDataRows ? string.Join('\u001e', rows) : null;
    }
}
