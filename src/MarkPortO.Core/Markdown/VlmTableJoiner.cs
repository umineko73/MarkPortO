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

using System.Text;
using System.Text.RegularExpressions;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Markdown;

/// <summary>
/// Joins a table that runs over several pages in the results of the VLM (FR-089). The VLM reads one page at a time, so the rows of the later
/// pages come as a table of their own (often with the header row written again, or without any header, which is no table at all), and the marks of the
/// pages (the page, the VLM block, the usage) stand between the parts. Here the rows of the later pages are put under the table of the page where it
/// starts, so that the marks come after the whole table, and the later page says where its rows went.
/// </summary>
/// <remarks>
/// Which pages go on with a table is known from the text that Step1 took (FR-078: the part of a table on a later page). Only a page whose result
/// starts with table lines, after a page whose result ends with table lines, with the same number of columns, is joined.
/// </remarks>
internal static partial class VlmTableJoiner
{
    /// <summary>
    /// The result of the pages that changed (the page that holds the whole table, and the pages that gave rows to it), by the number of the page.
    /// A page that is not in it keeps its result as it is.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Join(DocumentRecord document, IReadOnlyList<int> effectivePages, string language)
    {
        var continuation = new HashSet<int>();
        var paragraphContinuation = new HashSet<int>();
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (document.Blocks[i] is { Kind: BlockKind.Table or BlockKind.SheetTable, JoinedToPage: not null })
            {
                continuation.Add(effectivePages[i]);
            }
            else if (document.Blocks[i] is { Kind: BlockKind.Paragraph, JoinedToPage: not null })
            {
                paragraphContinuation.Add(effectivePages[i]);
            }
        }

        var changed = new Dictionary<int, string>();
        if (continuation.Count == 0 && paragraphContinuation.Count == 0)
        {
            return changed;
        }

        string TextOf(PageRecord page) => changed.TryGetValue(page.Index, out var text) ? text : page.VlmText!.Trim();

        int? owner = null;      // the page whose result ends with the table that may go on
        var lastPage = -1;
        foreach (var page in document.Pages.OrderBy(p => p.Index))
        {
            if (page.IsDeleted || page.IsBlank || page.IsOutOfRange || page.Step3 != PageStep3Status.Done || page.VlmText is null)
            {
                owner = null;
                lastPage = page.Index;
                continue;
            }

            var lines = page.VlmText.Trim().Split('\n');
            var leading = LeadingTableLines(lines);
            var joined = false;
            if (owner is { } ownerPage && page.Index == lastPage + 1 && document.Pages.First(p => p.Index == ownerPage) is { } holder)
            {
                string? note = null;
                string? rest = null;
                string merged = string.Empty;
                if (continuation.Contains(page.Index) && leading > 0 && TryJoin(TextOf(holder), lines[..leading], out merged))
                {
                    rest = string.Join('\n', lines[leading..]).Trim('\n', '\r', ' ');
                    note = "> " + Loc.M("The rows of this table are joined to the table on page {0}.", language, ownerPage);
                }
                else if (paragraphContinuation.Contains(page.Index) && ParagraphJoin.TryJoin(TextOf(holder), page.VlmText.Trim(), out merged, out var remainder))
                {
                    rest = remainder;
                    note = "> " + Loc.M("The rest of this paragraph is joined to the paragraph on page {0}.", language, ownerPage);
                }

                if (note is not null && rest is not null)
                {
                    changed[ownerPage] = merged;
                    changed[page.Index] = rest.Length == 0 ? note : note + "\n\n" + rest;
                    joined = true;

                    // It goes on to the next page only if this page had nothing but the rest of it.
                    if (rest.Length > 0)
                    {
                        owner = page.Index;
                    }
                }
            }

            if (!joined)
            {
                owner = TextOf(page).Length > 0 ? page.Index : null;
            }

            lastPage = page.Index;
        }

        return changed;
    }

    /// <summary>The rows of <paramref name="lines"/> (the start of the result of the next page) are put under the table at the end of <paramref name="ownerText"/>.</summary>
    internal static bool TryJoin(string ownerText, string[] lines, out string merged)
    {
        merged = ownerText;
        var owner = ownerText.Split('\n');
        var end = owner.Length;
        while (end > 0 && owner[end - 1].Trim().Length == 0)
        {
            end--;
        }

        var start = end;
        while (start > 0 && IsTableLine(owner[start - 1]))
        {
            start--;
        }

        if (start == end)
        {
            return false; // the result of the page before does not end with a table
        }

        var table = owner[start..end];
        var ownerHeader = table.Length >= 2 && IsSeparator(table[1]) ? Cells(table[0]) : null;
        var columns = Cells(table[0]).Count;

        // The lines of the next page: a header (written again, or the first row that the VLM took for the header), the separator, the rows.
        var rows = new List<string>();
        var hasSeparator = lines.Length >= 2 && IsSeparator(lines[1]);
        if (hasSeparator)
        {
            var first = Cells(lines[0]);
            if (first.Count != columns)
            {
                return false;
            }

            var isRepeatedHeader = first.All(c => c.Length == 0) || (ownerHeader is not null && SameHeader(first, ownerHeader));
            if (!isRepeatedHeader)
            {
                rows.Add(lines[0]); // it was a row of the table, not a header
            }

            rows.AddRange(lines[2..].Where(l => !IsSeparator(l)));
        }
        else
        {
            if (Cells(lines.First(l => !IsSeparator(l))).Count != columns)
            {
                return false;
            }

            rows.AddRange(lines.Where(l => !IsSeparator(l)));
            if (ownerHeader is not null && rows.Count > 0 && SameHeader(Cells(rows[0]), ownerHeader))
            {
                rows.RemoveAt(0); // the header written again, without a separator
            }
        }

        if (rows.Count == 0 || rows.Any(r => Cells(r).Count != columns))
        {
            return false;
        }

        var builder = new StringBuilder();
        builder.AppendJoin('\n', owner[..end]).Append('\n').AppendJoin('\n', rows);
        if (end < owner.Length)
        {
            builder.Append('\n').AppendJoin('\n', owner[end..]);
        }

        merged = builder.ToString();
        return true;
    }

    private static int LeadingTableLines(string[] lines)
    {
        var count = 0;
        while (count < lines.Length && IsTableLine(lines[count]))
        {
            count++;
        }

        return count;
    }

    private static bool EndsWithTable(string text)
    {
        var lines = text.TrimEnd().Split('\n');
        return lines.Length > 0 && IsTableLine(lines[^1]);
    }

    private static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

    private static bool IsSeparator(string line) => Separator().IsMatch(line);

    private static List<string> Cells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }

        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal))
        {
            text = text[..^1];
        }

        return [.. UnescapedPipe().Split(text).Select(c => c.Trim())];
    }

    private static bool SameRow(List<string> a, List<string> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => Normalize(pair.First) == Normalize(pair.Second));

    /// <summary>
    /// A header that is written again: the same cells, except one when there are three columns or more. The VLM reads the header of a page from its
    /// image, where a header of two rows or of joined cells ("数量" under "設置") is another text than in the first page.
    /// </summary>
    private static bool SameHeader(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var same = a.Zip(b).Count(pair => pair.First.Length > 0 && Normalize(pair.First) == Normalize(pair.Second));
        return a.Count >= 3 ? same >= a.Count - 1 && same >= 2 : same == a.Count;
    }

    private static string Normalize(string cell) =>
        Regex.Replace(cell.Normalize(NormalizationForm.FormKC), @"\s+", string.Empty);

    [GeneratedRegex(@"^\s*\|?(\s*:?-{3,}:?\s*\|)*\s*:?-{3,}:?\s*\|?\s*$")]
    private static partial Regex Separator();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex UnescapedPipe();
}
