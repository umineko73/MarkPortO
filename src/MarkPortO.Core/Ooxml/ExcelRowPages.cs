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
using MarkPortO.Core.Markdown;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Finds the page (of the PDF that Excel or LibreOffice printed) that each row of a sheet is on, by looking for the text of the rows one after another in
/// the text of the PDF, the way <see cref="DocxPageAssigner"/> does it for the rows of a table of a docx (FR-090). A long sheet is printed on
/// several pages, one below the other; the text that Step1 takes is cut at the same places.
/// </summary>
public static class ExcelRowPages
{
    private const int MinimumNeedle = 3;
    private const int MinimumWindow = 1000;

    /// <summary>
    /// The page (0-based, in <paramref name="pageTexts"/>) of each row of the sheet. A row without text, or one that is not found, is on the page of the
    /// row before it.
    /// </summary>
    public static int[] Assign(SheetContent sheet, int fromIndex, int toIndex, IReadOnlyList<string> pageTexts)
    {
        var pages = new int[sheet.Rows.Length];
        var all = new StringBuilder();
        var pageStarts = new int[pageTexts.Count];
        var longest = 0;
        for (var i = 0; i < pageTexts.Count; i++)
        {
            pageStarts[i] = all.Length;
            var text = DocxPageAssigner.Normalize(pageTexts[i]);
            longest = Math.Max(longest, text.Length);
            all.Append(text);
        }

        if (pageTexts.Count < 2)
        {
            return pages;
        }

        var haystack = all.ToString();
        var window = Math.Max(MinimumWindow, longest);
        var cursor = 0;
        var page = 0;
        for (var r = 0; r < sheet.Rows.Length; r++)
        {
            var cells = Enumerable.Range(fromIndex, toIndex - fromIndex + 1)
                .Where(c => c < sheet.Rows[r].Length && !sheet.HiddenColumns.Contains(c))
                .Select(c => DocxPageAssigner.Normalize(Strikethrough.Remove(sheet.Rows[r][c])))
                .Where(text => text.Length > 0)
                .ToList();
            if (sheet.HiddenRows.Contains(r) || cells.Count == 0)
            {
                pages[r] = page;
                continue;
            }

            // The text of the whole row, or when the PDF lists the cells in another order, its longest cells.
            var candidates = new List<string> { string.Concat(cells) };
            candidates.AddRange(cells.OrderByDescending(text => text.Length).Take(2));
            var found = -1;
            var matched = 0;
            foreach (var candidate in candidates.Where(c => c.Length >= MinimumNeedle).Distinct())
            {
                found = DocxPageAssigner.Find(haystack, candidate, cursor, window);
                if (found >= 0)
                {
                    matched = Math.Min(candidate.Length, DocxPageAssigner.LongNeedle);
                    break;
                }
            }

            if (found >= 0)
            {
                page = Math.Max(page, DocxPageAssigner.PageOf(pageStarts, found));
                cursor = found + matched;
            }

            pages[r] = page;
        }

        return pages;
    }
}
