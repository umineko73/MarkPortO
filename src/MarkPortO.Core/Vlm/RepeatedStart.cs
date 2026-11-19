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

namespace MarkPortO.Core.Vlm;

/// <summary>
/// A small model sometimes writes the page twice: what it reads in the image, and then the "extracted text" that it was given as a help (FR-100).
/// The second copy starts as the first one does, so a result whose first two lines come again later is cut at the second start. The first
/// copy is kept: it comes from the image.
/// </summary>
public static class RepeatedStart
{
    private const int MinimumLength = 4;

    public static string Trim(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var filled = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim().Length > 0).ToList();
        if (filled.Count < 4)
        {
            return text;
        }

        var first = PageFurniture.Normalise(lines[filled[0]]);
        var second = PageFurniture.Normalise(lines[filled[1]]);

        // The start has to be text (a heading, a caption, a paragraph): the header row of a table may well come again in a page with two tables.
        if (first.Length < MinimumLength || lines[filled[0]].TrimStart().StartsWith('|') || second.Length == 0)
        {
            return text;
        }

        for (var k = 2; k + 1 < filled.Count; k++)
        {
            if (PageFurniture.Normalise(lines[filled[k]]) != first || PageFurniture.Normalise(lines[filled[k + 1]]) != second)
            {
                continue;
            }

            var end = filled[k];
            while (end > 0 && (lines[end - 1].Trim().Length == 0 || IsRule(lines[end - 1])))
            {
                end--;
            }

            return string.Join('\n', lines[..end]).Trim('\n', '\r', ' ');
        }

        return text;
    }

    /// <summary>
    /// A part of the page that the model wrote again further down, in another form: a heading and the line after it come again (the level of the heading
    /// may differ), and what follows is text that was already written above (a code block written again as a list, a table as lines). The page is cut
    /// at the second heading. When what follows has new text, nothing is cut: a document may well have the same heading twice.
    /// </summary>
    public static string TrimSection(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var filled = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim().Length > 0).ToList();
        var keys = filled.Select(i => Key(lines[i])).ToList();
        for (var j = 2; j + 1 < filled.Count; j++)
        {
            if (!lines[filled[j]].TrimStart().StartsWith('#') || keys[j].Length < MinimumLength)
            {
                continue;
            }

            var earlier = Enumerable.Range(0, j - 1).Any(i => keys[i] == keys[j] && keys[i + 1] == keys[j + 1] && lines[filled[i]].TrimStart().StartsWith('#'));
            if (!earlier)
            {
                continue;
            }

            // The rest, after the two lines that come again, has to be text that is already above.
            var before = string.Concat(keys.Take(j));
            var tail = keys.Skip(j + 2).Where(k => k.Length >= MinimumLength).ToList();
            if (tail.Count < 3 || tail.Count(k => before.Contains(k, StringComparison.Ordinal)) < tail.Count * 0.8)
            {
                continue;
            }

            var end = filled[j];
            while (end > 0 && (lines[end - 1].Trim().Length == 0 || IsRule(lines[end - 1])))
            {
                end--;
            }

            return string.Join('\n', lines[..end]).Trim('\n', '\r', ' ');
        }

        return text;
    }

    /// <summary>A line for comparing what it says: without the marks of Markdown (heading, list, table, code), the lines of a tree and the spaces.</summary>
    private static string Key(string line)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in line.Normalize(System.Text.NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(c) || c is '#' or '*' or '`' or '|' or '>' or '-' or '+' or (>= '─' and <= '╿'))
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsRule(string line)
    {
        var text = line.Trim();
        return text.Length >= 3 && text.All(c => c is '-' or '*' or '_');
    }
}
