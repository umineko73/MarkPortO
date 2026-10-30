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

using MarkPortO.Core.Localization;
using System.Text;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Decides on which page each paragraph and table of a docx starts, by finding the blocks one after another
/// in the text of the PDF that was made from the same document (research R3, FR-007a).
/// The PDF is also the source of the page images, so text and images cannot drift apart.
/// </summary>
/// <remarks>
/// Blocks are looked for strictly forward: the search for the next block starts where the previous one was found.
/// A block that cannot be found is not given a guessed page; it gets a warning and keeps no page number.
/// </remarks>
public static class DocxPageAssigner
{
    public const string UnresolvedCode = "PageUnresolved";

    /// <summary>Longest start of a block that is searched for.</summary>
    public const int LongNeedle = 24;

    /// <summary>Shorter start used when the long one is not found.</summary>
    public const int ShortNeedle = 12;

    /// <summary>A block with less text than this is too short to be found reliably; it is left without a warning.</summary>
    public const int MinimumNeedle = 3;

    /// <summary>A missing block is reported only if it has at least this much text (shorter ones are noise).</summary>
    private const int ReportThreshold = 6;

    private const int MinimumWindow = 2000;

    /// <summary>How far after the row before it the next row of a table is looked for (the rows of a table follow one another closely).</summary>
    private const int MinimumRowWindow = 1000;

    /// <summary>A paragraph with less text than this is not looked at for a page break.</summary>
    private const int MinimumSplitLength = 30;

    /// <summary>How far from the end of a page (and from the start of the next one) text of a header or a footer may stand between the two parts of a paragraph.</summary>
    private const int PageEdgeWindow = 400;

    /// <summary>The part of a paragraph after a page break has at least this much text.</summary>
    private const int MinimumTail = 6;

    /// <summary>
    /// Sets <see cref="Block.PageIndex"/> (1-based PDF page) on every block that could be found, and returns
    /// a warning for each block that could not.
    /// </summary>
    /// <param name="blocks">The blocks in document order.</param>
    /// <param name="pageTexts">The text of each PDF page, in page order.</param>
    public static IReadOnlyList<Warning> Assign(IReadOnlyList<DocxBlock> blocks, IReadOnlyList<string> pageTexts)
    {
        var warnings = new List<Warning>();

        var all = new StringBuilder();
        var pageStarts = new int[pageTexts.Count];
        var maxPageLength = 0;
        for (var i = 0; i < pageTexts.Count; i++)
        {
            pageStarts[i] = all.Length;
            var normalized = Normalize(pageTexts[i]);
            maxPageLength = Math.Max(maxPageLength, normalized.Length);
            all.Append(normalized);
        }

        var haystack = all.ToString();
        var window = Math.Max(MinimumWindow, maxPageLength * 2);
        var rowWindow = Math.Max(MinimumRowWindow, maxPageLength / 2);
        var cursor = 0;

        foreach (var item in blocks)
        {
            var candidates = StartCandidates(item);
            if (candidates.Count == 0)
            {
                item.Block.PageIndex = null;
                continue;
            }

            var text = candidates[0];
            var position = -1;
            foreach (var candidate in candidates)
            {
                position = Find(haystack, candidate, cursor, window);
                if (position < 0 && item.Floating)
                {
                    // It may be listed before the place of the cursor, in the same page: look from the start of that page.
                    var pageStart = pageStarts[Math.Min(PageOf(pageStarts, Math.Max(0, cursor - 1)), pageStarts.Length - 1)];
                    position = Find(haystack, candidate, pageStart, window);
                }

                if (position >= 0)
                {
                    text = candidate;
                    break;
                }
            }

            if (position < 0)
            {
                item.Block.PageIndex = null;
                if (text.Length >= ReportThreshold)
                {
                    var snippet = item.SearchText.Length <= 30 ? item.SearchText : item.SearchText[..30] + "...";
                    warnings.Add(new Warning(
                        UnresolvedCode,
                        Loc.T("Could not find the page of this text in the PDF: \"{0}\". It is placed between its neighbours.", snippet),
                        PageIndex: null));
                }

                continue;
            }

            item.Block.PageIndex = PageOf(pageStarts, position) + 1;
            if (item.Floating)
            {
                continue; // where it is listed says nothing about where the text that follows it is
            }

            // If the whole block is found in one piece, skip past all of it; otherwise past the part that was matched.
            cursor = haystack.AsSpan(position).StartsWith(text.AsSpan(), StringComparison.Ordinal)
                ? position + text.Length
                : position + Math.Min(text.Length, LongNeedle);

            // A paragraph that runs over a page break is cut there: each page has the part that is on it (FR-091).
            if (item.Splittable && item.Table is null && text.Length >= MinimumSplitLength
                && FindSegments(haystack, text, pageStarts, position) is { Count: > 1 } segments)
            {
                item.Segments = segments;
                cursor = Math.Max(cursor, segments[^1].HaystackEnd);
            }

            // The rows of a table are placed one by one: a long table runs over several pages.
            if (item.Table is { PlainRows.Count: > 1 } table)
            {
                cursor = Math.Max(cursor, AssignRows(item, table, haystack, pageStarts, cursor, rowWindow));
            }
        }

        return warnings;
    }

    /// <summary>
    /// The texts that a block is looked for by, best first. A paragraph has its own text. A table has its first cell, and when that is
    /// too short to be found (a header such as "No" or "項目"), the whole first row and its longest cells: a table is not left without a page.
    /// </summary>
    private static List<string> StartCandidates(DocxBlock item)
    {
        var candidates = new List<string>();
        void Add(string text)
        {
            var normalized = Normalize(text);
            if (normalized.Length >= MinimumNeedle && !candidates.Contains(normalized))
            {
                candidates.Add(normalized);
            }
        }

        Add(item.SearchText);
        if (item.Table is { PlainRows.Count: > 0 } table)
        {
            var first = table.PlainRows[0];
            Add(string.Concat(first));
            foreach (var cell in first.OrderByDescending(c => c.Length).Take(2))
            {
                Add(cell);
            }
        }

        return candidates;
    }

    /// <summary>
    /// The parts of a paragraph that start at <paramref name="position"/> of the text of the PDF, one for each page that it is on: the text goes on at the
    /// start of the next page, after the text of a header if there is one. Null when the text does not go on the way a page break would make it.
    /// </summary>
    private static List<ParagraphSegment>? FindSegments(string haystack, string text, int[] pageStarts, int position)
    {
        var result = new List<ParagraphSegment>();
        var offset = 0;
        var current = position;
        for (var step = 0; step < 12 && offset < text.Length; step++)
        {
            var page = PageOf(pageStarts, current);
            var pageEnd = page + 1 < pageStarts.Length ? pageStarts[page + 1] : haystack.Length;
            var matched = CommonPrefix(haystack, current, text, offset);
            if (matched == 0 && result.Count > 0)
            {
                // After a page break the page may begin with the text of a header: the next text of the paragraph is looked for near the start.
                var needle = text.Substring(offset, Math.Min(LongNeedle, text.Length - offset));
                var found = needle.Length >= MinimumTail ? haystack.IndexOf(needle, current, StringComparison.Ordinal) : -1;
                if (found < 0 || found - current > PageEdgeWindow)
                {
                    return null;
                }

                current = found;
                page = PageOf(pageStarts, current);
                pageEnd = page + 1 < pageStarts.Length ? pageStarts[page + 1] : haystack.Length;
                matched = CommonPrefix(haystack, current, text, offset);
            }

            if (matched == 0)
            {
                return null;
            }

            if (offset + matched == text.Length && current + matched <= pageEnd)
            {
                result.Add(new ParagraphSegment(offset, text.Length, page + 1, current + matched));
                return result;
            }

            var mismatch = current + matched;
            if (mismatch <= pageEnd && pageEnd - mismatch > PageEdgeWindow)
            {
                return null; // it stops in the middle of a page: not a page break
            }

            // The part on this page ends at the end of the page, or where the text stops being the same (the footer).
            var end = mismatch <= pageEnd ? mismatch : pageEnd;
            result.Add(new ParagraphSegment(offset, offset + (end - current), page + 1, end));
            offset += end - current;
            current = pageEnd;
        }

        return null;
    }

    private static int CommonPrefix(string haystack, int haystackStart, string text, int textStart)
    {
        var count = 0;
        while (haystackStart + count < haystack.Length && textStart + count < text.Length && haystack[haystackStart + count] == text[textStart + count])
        {
            count++;
        }

        return count;
    }

    /// <summary>The place in <paramref name="plain"/> where <paramref name="normalizedCount"/> characters of its normalized text have been read.</summary>
    private static int OriginalIndex(string plain, int normalizedCount)
    {
        if (normalizedCount <= 0)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < plain.Length;)
        {
            var length = char.IsHighSurrogate(plain[i]) && i + 1 < plain.Length ? 2 : 1;
            count += Normalize(plain.Substring(i, length)).Length;
            i += length;
            if (count >= normalizedCount)
            {
                return i;
            }
        }

        return plain.Length;
    }

    /// <summary>
    /// Finds the page of each row of a table: the rows are looked for one after another, by the text of the whole row, or by its
    /// longest cells when a PDF lists the cells of a row in another order. A row that is not found stays on the page of the row before it.
    /// Sets <see cref="DocxBlock.RowPages"/> and returns the position in the text after the last row that was found.
    /// </summary>
    private static int AssignRows(DocxBlock item, DocxTable table, string haystack, int[] pageStarts, int start, int window)
    {
        var pages = new int[table.PlainRows.Count];
        var page = item.Block.PageIndex ?? 1;
        pages[0] = page;
        var cursor = start;
        for (var row = 1; row < pages.Length; row++)
        {
            var cells = table.PlainRows[row].Select(Normalize).Where(c => c.Length >= MinimumNeedle).ToList();
            var candidates = new List<string>();
            var whole = string.Concat(table.PlainRows[row].Select(Normalize));
            if (whole.Length >= MinimumNeedle)
            {
                candidates.Add(whole);
            }

            candidates.AddRange(cells.OrderByDescending(c => c.Length).Take(2));

            var found = -1;
            var matched = 0;
            foreach (var candidate in candidates.Distinct())
            {
                found = Find(haystack, candidate, cursor, window);
                if (found >= 0)
                {
                    matched = Math.Min(candidate.Length, LongNeedle);
                    break;
                }
            }

            if (found < 0)
            {
                pages[row] = page;
                continue;
            }

            page = Math.Max(page, PageOf(pageStarts, found) + 1);
            pages[row] = page;
            cursor = found + matched;
        }

        item.RowPages = pages;
        return cursor;
    }

    /// <summary>
    /// Cuts every table that runs over several pages into one table for each page (the rows that are on the page), so that
    /// the text of a page is the text that is on the page. When Word repeats the header row on every page, each part starts with it.
    /// </summary>
    public static List<DocxBlock> ExpandTables(IReadOnlyList<DocxBlock> blocks)
    {
        var result = new List<DocxBlock>(blocks.Count);
        foreach (var item in blocks)
        {
            if (item.Segments is { Count: > 1 } segments)
            {
                // A paragraph over a page break: one block for each page, each with its own part of the text.
                var plain = item.SearchText;
                for (var part = 0; part < segments.Count; part++)
                {
                    var from = OriginalIndex(plain, segments[part].Start);
                    var to = OriginalIndex(plain, segments[part].End);
                    var piece = plain[from..to].Trim();
                    if (piece.Length == 0)
                    {
                        continue;
                    }

                    result.Add(new DocxBlock(
                        new Block
                        {
                            Kind = BlockKind.Paragraph,
                            Markdown = MarkdownText.Escape(piece).Replace("\n", "  \n", StringComparison.Ordinal),
                            PageIndex = segments[part].Page,
                            JoinedToPage = part == 0 ? null : segments[0].Page,
                        },
                        piece));
                }

                continue;
            }

            if (item.Table is not { } table || item.RowPages is not { } pages || pages.Distinct().Count() < 2)
            {
                result.Add(item);
                continue;
            }

            var start = 0;
            for (var row = 1; row <= pages.Count; row++)
            {
                if (row < pages.Count && pages[row] == pages[start])
                {
                    continue;
                }

                var rows = table.Rows.Skip(start).Take(row - start).ToList();
                var hasHeader = table.HasHeader;
                if (start > 0)
                {
                    hasHeader = table.RepeatsHeader;
                    if (hasHeader)
                    {
                        rows.Insert(0, table.Rows[0]);
                    }
                }

                var searchText = start == 0 ? item.SearchText : string.Concat(table.PlainRows[start]);
                result.Add(new DocxBlock(
                    new Block { Kind = BlockKind.Table, Markdown = MarkdownText.Table(rows, hasHeader), PageIndex = pages[start], JoinedToPage = start == 0 ? null : pages[0] },
                    searchText));
                start = row;
            }
        }

        return result;
    }

    /// <summary>
    /// The page every block belongs to for placing images: a block without a page of its own stays on the page
    /// of the block before it (page 1 before any page is known). Does not change the blocks.
    /// </summary>
    public static IReadOnlyList<int> EffectivePages(IReadOnlyList<Block> blocks, int pageCount)
    {
        var result = new int[blocks.Count];
        var current = 1;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].PageIndex is { } page)
            {
                current = Math.Clamp(page, 1, Math.Max(1, pageCount));
            }

            result[i] = current;
        }

        return result;
    }

    /// <summary>Removes white space and folds full-width/half-width and similar forms, so PDF text and document text compare equal.</summary>
    public static string Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (ch is TextEmphasis.BoldMark or TextEmphasis.ItalicMark)
            {
                continue; // the marks of bold and italic are not in the text of a PDF
            }

            if (char.IsWhiteSpace(ch) || ch is '​' or '‌' or '‍' or '﻿' or '­')
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    internal static int Find(string haystack, string text, int cursor, int window)
    {
        var limit = Math.Min(haystack.Length, cursor + window);
        foreach (var length in new[] { Math.Min(text.Length, LongNeedle), Math.Min(text.Length, ShortNeedle) }.Distinct())
        {
            var needle = text[..length];
            var index = haystack.IndexOf(needle, cursor, StringComparison.Ordinal);
            if (index >= 0 && index <= limit)
            {
                return index;
            }
        }

        return -1;
    }

    internal static int PageOf(int[] pageStarts, int position)
    {
        var page = Array.BinarySearch(pageStarts, position);
        if (page < 0)
        {
            page = ~page - 1;
        }

        // Several empty pages share one start: the page that holds the position is the last one that starts there.
        while (page + 1 < pageStarts.Length && pageStarts[page + 1] <= position)
        {
            page++;
        }

        return Math.Max(0, page);
    }
}
