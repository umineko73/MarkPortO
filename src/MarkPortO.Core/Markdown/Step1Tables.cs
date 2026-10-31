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
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Markdown;

/// <summary>
/// The tables of Step1 instead of the tables of the VLM (FR-105). A small model breaks a table that it reads from an image (cells that are cut,
/// rows that are written twice), while the table that Step1 took from the file is exact. On a page whose text has a table, the tables of the
/// VLM result are left out, and a quote says that the table of the document is the one to use. The result of the VLM itself is not changed
/// in the job, so the choice can be made again without reading the pages again.
/// </summary>
internal static partial class Step1Tables
{
    /// <summary>The result of the pages that changed, by the number of the page, on top of <paramref name="joined"/> (the result of <see cref="VlmTableJoiner"/>).</summary>
    public static Dictionary<int, string> Apply(DocumentRecord document, IReadOnlyList<int> effectivePages, IReadOnlyDictionary<int, string> joined, string language)
    {
        var pagesWithTable = new HashSet<int>();
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (document.Blocks[i].Kind is BlockKind.Table or BlockKind.SheetTable)
            {
                pagesWithTable.Add(effectivePages[i]);
            }
        }

        var result = new Dictionary<int, string>(joined);
        foreach (var page in document.Pages)
        {
            if (!pagesWithTable.Contains(page.Index) || page.Step3 != PageStep3Status.Done || page.VlmText is null)
            {
                continue;
            }

            var text = joined.TryGetValue(page.Index, out var changed) ? changed : page.VlmText.Trim();
            var replaced = Replace(text, "> " + Loc.M("The tables of this page are not repeated here; the tables of the text of the document are the ones to use.", language));
            if (replaced != text)
            {
                result[page.Index] = replaced;
            }
        }

        return result;
    }

    /// <summary>Every run of table lines in <paramref name="text"/> becomes <paramref name="note"/> (once for the text).</summary>
    internal static string Replace(string text, string note)
    {
        var lines = text.Split('\n');
        var output = new List<string>();
        var noted = false;
        var i = 0;
        while (i < lines.Length)
        {
            var end = TableEnd(lines, i);
            if (end > i)
            {
                if (!noted)
                {
                    output.Add(note);
                    noted = true;
                }

                i = end;
                continue;
            }

            output.Add(lines[i]);
            i++;
        }

        return noted ? Tidy(output) : text;
    }

    /// <summary>Where the table that starts at <paramref name="start"/> ends (the index after its last line); <paramref name="start"/> when there is none.</summary>
    private static int TableEnd(string[] lines, int start)
    {
        // A table: lines that start with a pipe, or lines with a pipe in them when one of them is the separator row (the VLM often leaves the first pipe out).
        var end = start;
        var hasSeparator = false;
        var startsWithPipe = true;
        while (end < lines.Length && lines[end].Contains('|', StringComparison.Ordinal) && lines[end].Trim().Length > 0)
        {
            hasSeparator |= Separator().IsMatch(lines[end]);
            startsWithPipe &= lines[end].TrimStart().StartsWith('|');
            end++;
        }

        var count = end - start;
        return count >= 2 && (startsWithPipe || hasSeparator) ? end : start;
    }

    // The note stands alone: a blank line on both sides, and no blank line at the start or the end.
    private static string Tidy(List<string> lines)
    {
        var text = string.Join('\n', lines);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim('\n');
    }

    [GeneratedRegex(@"^\s*\|?(\s*:?-{3,}:?\s*\|)*\s*:?-{3,}:?\s*\|?\s*$")]
    private static partial Regex Separator();
}
