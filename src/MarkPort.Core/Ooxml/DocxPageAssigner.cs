// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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

using MarkPort.Core.Localization;
using System.Text;
using MarkPort.Core.Jobs.Models;

namespace MarkPort.Core.Ooxml;

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
        var cursor = 0;

        foreach (var item in blocks)
        {
            var text = Normalize(item.SearchText);
            if (text.Length < MinimumNeedle)
            {
                item.Block.PageIndex = null;
                continue;
            }

            var position = Find(haystack, text, cursor, window);
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

            // If the whole block is found in one piece, skip past all of it; otherwise past the part that was matched.
            cursor = haystack.AsSpan(position).StartsWith(text.AsSpan(), StringComparison.Ordinal)
                ? position + text.Length
                : position + Math.Min(text.Length, LongNeedle);
        }

        return warnings;
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
            if (char.IsWhiteSpace(ch) || ch is '​' or '‌' or '‍' or '﻿' or '­')
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static int Find(string haystack, string text, int cursor, int window)
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

    private static int PageOf(int[] pageStarts, int position)
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
