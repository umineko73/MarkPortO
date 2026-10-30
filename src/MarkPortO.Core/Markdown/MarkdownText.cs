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

namespace MarkPortO.Core.Markdown;

/// <summary>Small helpers that keep text from a document from being read as Markdown or as MarkPortO's own markers.</summary>
public static partial class MarkdownText
{
    /// <summary>
    /// Escapes text so it is shown as written: MarkPortO's HTML comment markers are neutralised
    /// (<c>&lt;!--</c> becomes <c>&amp;lt;!--</c>), and a line that would start a heading, quote, list, or table is
    /// prefixed with a backslash. Inline characters such as <c>*</c> are left alone on purpose.
    /// </summary>
    public static string Escape(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var neutral = text.Replace("<!--", "&lt;!--", StringComparison.Ordinal);
        var lines = neutral.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = OrderedStart().Replace(BlockStart().Replace(lines[i], "$1\\$2"), "$1\\$2");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Makes only the HTML comment opener harmless. Used for text that is already Markdown (the answer of the VLM),
    /// where headings and lists must stay as they are.
    /// </summary>
    public static string NeutraliseMarkers(string text) => text.Replace("<!--", "&lt;!--", StringComparison.Ordinal);

    /// <summary>Text of one table cell: no pipe, no line break (<c>|</c> becomes <c>\|</c>, a line break becomes <c>&lt;br&gt;</c>).</summary>
    public static string EscapeCell(string text)
    {
        var cell = Escape(text)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Trim();

        // A cell that is only "<" or "^" would be read as a mark of a merged cell (FR-059).
        return cell is "<" or "^" ? "\\" + cell : cell;
    }

    /// <summary>
    /// The cell of a table that is hidden by a merged cell, in the first row of the merged range (FR-059): it is merged
    /// with the cell on its left. A Markdown table cannot be merged, so the readers put this one character in such a
    /// cell; <see cref="ResolveMarks"/> writes it as <c>&lt;</c> (the notePM style).
    /// </summary>
    public const string MergedLeft = "\uE000";

    /// <summary>The cell hidden by a merged cell, in the second row or below of the merged range: merged with the cell above (<c>^</c>).</summary>
    public const string MergedUp = "\uE001";

    public static bool HasMarks(string markdown) =>
        markdown.Contains(MergedLeft, StringComparison.Ordinal) || markdown.Contains(MergedUp, StringComparison.Ordinal) || TextEmphasis.HasMarks(markdown);

    /// <summary>
    /// Writes the marks of merged cells as <c>&lt;</c> and <c>^</c>, and the marks of bold and italic as <c>**</c> and <c>*</c> (FR-101).
    /// The marks do not depend on the language.
    /// </summary>
    public static string ResolveMarks(string markdown) =>
        HasMarks(markdown)
            ? TextEmphasis.Resolve(markdown.Replace(MergedLeft, "<", StringComparison.Ordinal).Replace(MergedUp, "^", StringComparison.Ordinal))
            : markdown;

    /// <summary>
    /// Builds a Markdown pipe table. A Markdown table needs a header row: with <paramref name="hasHeader"/> false the
    /// header is empty and the first row stays a row of data (FR-060). Rows may differ in length.
    /// </summary>
    public static string Table(IReadOnlyList<IReadOnlyList<string>> rows, bool hasHeader = true)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = rows.Max(r => r.Count);
        if (columns == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        void Separator()
        {
            builder.Append('|');
            for (var c = 0; c < columns; c++)
            {
                builder.Append("---|");
            }

            builder.Append('\n');
        }

        if (!hasHeader)
        {
            builder.Append('|');
            for (var c = 0; c < columns; c++)
            {
                builder.Append("  |");
            }

            builder.Append('\n');
            Separator();
        }

        string[]? previous = null;
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = new string[columns];
            for (var c = 0; c < columns; c++)
            {
                var raw = c >= rows[r].Count ? string.Empty : rows[r][c];
                cells[c] = raw is MergedLeft or MergedUp
                    ? raw
                    : EscapeCell(raw.Replace(MergedLeft, string.Empty, StringComparison.Ordinal).Replace(MergedUp, string.Empty, StringComparison.Ordinal));
            }

            // The header row of a notePM table cannot be merged: the value is repeated where a merge touches it (FR-059).
            if (hasHeader && r <= 1)
            {
                for (var c = 0; c < columns; c++)
                {
                    if (r == 0 && cells[c] == MergedLeft && c > 0)
                    {
                        cells[c] = cells[c - 1];
                    }
                    else if (r == 1 && cells[c] == MergedUp && previous is not null)
                    {
                        cells[c] = previous[c];
                    }
                }
            }

            builder.Append('|');
            foreach (var cell in cells)
            {
                builder.Append(' ').Append(cell).Append(" |");
            }

            previous = cells;
            builder.Append('\n');
            if (r == 0 && hasHeader)
            {
                Separator();
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    // A line that starts a block in Markdown: #, >, -, +, *, or a table bar, after up to three spaces. The marker itself gets the backslash.
    [GeneratedRegex(@"^(\s{0,3})(#{1,6}(?=\s|$)|>|[-+*](?=\s)|\|)")]
    private static partial Regex BlockStart();

    // "1." or "1)" after up to three spaces: the backslash goes before the dot (a backslash before a digit is not an escape).
    [GeneratedRegex(@"^(\s{0,3}\d{1,9})([.)])(?=\s)")]
    private static partial Regex OrderedStart();
}
