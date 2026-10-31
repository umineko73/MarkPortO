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
/// VLM result are replaced by the tables of the page (<see cref="Step1TablesMode.Replace"/>), or left out with a quote that says that the table of
/// the document is the one to use (<see cref="Step1TablesMode.Omit"/>). The result of the VLM itself is not changed in the job, so the choice can
/// be made again without reading the pages again.
/// </summary>
internal static partial class Step1Tables
{
    /// <summary>The result of the pages that changed, by the number of the page, on top of <paramref name="joined"/> (the result of <see cref="VlmTableJoiner"/>).</summary>
    public static Dictionary<int, string> Apply(DocumentRecord document, IReadOnlyList<int> effectivePages, IReadOnlyDictionary<int, string> joined, string language)
    {
        // The tables that Step1 took, for each page, as they stand in the Markdown of the document (a heading before a table is not a part of it).
        var tablesOfPage = new Dictionary<int, List<string>>();
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (document.Blocks[i].Kind is BlockKind.Table or BlockKind.SheetTable)
            {
                var lines = MarkdownText.ResolveMarks(document.Blocks[i].Markdown).Split('\n').SkipWhile(l => !l.TrimStart().StartsWith('|')).ToList();
                if (lines.Count > 0)
                {
                    if (!tablesOfPage.TryGetValue(effectivePages[i], out var list))
                    {
                        tablesOfPage[effectivePages[i]] = list = [];
                    }

                    list.Add(string.Join('\n', lines).TrimEnd());
                }
            }
        }

        var note = "> " + Loc.M("The tables of this page are not repeated here; the tables of the text of the document are the ones to use.", language);
        var result = new Dictionary<int, string>(joined);
        foreach (var page in document.Pages)
        {
            if (!tablesOfPage.TryGetValue(page.Index, out var tables) || page.Step3 != PageStep3Status.Done || page.VlmText is null)
            {
                continue;
            }

            var text = joined.TryGetValue(page.Index, out var changed) ? changed : page.VlmText.Trim();
            var done = document.Step1Tables == Step1TablesMode.Replace ? Replace(text, tables) : Omit(text, note);
            if (done != text)
            {
                result[page.Index] = done;
            }
        }

        return result;
    }

    /// <summary>Every run of table lines in <paramref name="text"/> becomes <paramref name="note"/> (once for the text).</summary>
    internal static string Omit(string text, string note)
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

    /// <summary>
    /// The runs of table lines in <paramref name="text"/> are replaced by <paramref name="tables"/>: one by one when there are as many runs as
    /// tables; otherwise all the tables stand where the first run was, and the other runs are left out. Text with no table is not changed.
    /// </summary>
    internal static string Replace(string text, IReadOnlyList<string> tables)
    {
        var lines = text.Split('\n');
        var output = new List<string>();
        var runs = 0;
        for (var i = 0; i < lines.Length; i += Math.Max(1, TableEnd(lines, i) - i))
        {
            if (TableEnd(lines, i) > i)
            {
                runs++;
            }
        }

        if (runs == 0)
        {
            return text;
        }

        var run = 0;
        var at = 0;
        while (at < lines.Length)
        {
            var end = TableEnd(lines, at);
            if (end > at)
            {
                if (runs == tables.Count)
                {
                    output.Add(string.Empty);
                    output.Add(tables[run]);
                    output.Add(string.Empty);
                }
                else if (run == 0)
                {
                    output.Add(string.Empty);
                    output.Add(string.Join("\n\n", tables));
                    output.Add(string.Empty);
                }

                run++;
                at = end;
                continue;
            }

            output.Add(lines[at]);
            at++;
        }

        return Tidy(output);
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
