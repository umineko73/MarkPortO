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

namespace MarkPortO.Core.Markdown;

/// <summary>
/// Puts the two parts of a paragraph that runs over a page break back together, in the Markdown of the document and in the results of the VLM (FR-091).
/// </summary>
internal static partial class ParagraphJoin
{
    /// <summary>What stands between the two parts: nothing where the text is Japanese (or Chinese, Korean), a space otherwise.</summary>
    public static string Separator(string head, string tail)
    {
        var before = head.TrimEnd();
        var after = tail.TrimStart();
        if (before.Length == 0 || after.Length == 0)
        {
            return string.Empty;
        }

        return IsCjk(before[^1]) || IsCjk(after[0]) ? string.Empty : " ";
    }

    private static bool IsCjk(char c) =>
        c is >= '　' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯';

    /// <summary>
    /// The first paragraph of the result of the next page is put at the end of the last paragraph of the result of the page before. Both must be
    /// plain paragraphs (not a heading, a list, a table, a quote or a code block). <paramref name="rest"/> is what is left of the next page.
    /// </summary>
    public static bool TryJoin(string ownerText, string nextText, out string merged, out string rest)
    {
        merged = ownerText;
        rest = nextText;

        var owner = ownerText.TrimEnd().Split('\n');
        var lastStart = owner.Length;
        while (lastStart > 0 && owner[lastStart - 1].Trim().Length > 0)
        {
            lastStart--;
        }

        if (lastStart == owner.Length || !IsPlain(owner[lastStart]))
        {
            return false; // the result of the page before does not end with a paragraph
        }

        var next = nextText.Split('\n');
        var first = 0;
        while (first < next.Length && next[first].Trim().Length == 0)
        {
            first++;
        }

        var end = first;
        while (end < next.Length && next[end].Trim().Length > 0)
        {
            end++;
        }

        if (first == next.Length || !IsPlain(next[first]) || next[first..end].Any(l => !IsPlain(l)))
        {
            return false; // the result of the next page does not start with a paragraph
        }

        var paragraph = string.Join('\n', next[first..end]).Trim();
        merged = ownerText.TrimEnd() + Separator(owner[^1], paragraph) + paragraph;
        rest = string.Join('\n', next[end..]).Trim('\n', '\r', ' ');
        return true;
    }

    // A line of a paragraph: not the start of a heading, a list, a table, a quote, a code block, a comment or a rule.
    private static bool IsPlain(string line) => !NotPlain().IsMatch(line);

    [GeneratedRegex(@"^\s*(#{1,6}\s|[|>]|[-+*]\s|\d{1,9}[.)]\s|```|~~~|<!--|---+\s*$|\*\*\*+\s*$)")]
    private static partial Regex NotPlain();
}
