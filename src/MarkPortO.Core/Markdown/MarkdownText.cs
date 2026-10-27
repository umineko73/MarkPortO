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
            lines[i] = BlockStart().Replace(lines[i], "\\$1");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Makes only the HTML comment opener harmless. Used for text that is already Markdown (the answer of the VLM),
    /// where headings and lists must stay as they are.
    /// </summary>
    public static string NeutraliseMarkers(string text) => text.Replace("<!--", "&lt;!--", StringComparison.Ordinal);

    /// <summary>Text of one table cell: no pipe, no line break (<c>|</c> becomes <c>\|</c>, a line break becomes <c>&lt;br&gt;</c>).</summary>
    public static string EscapeCell(string text) =>
        Escape(text)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Trim();

    /// <summary>
    /// The cell of a table that is hidden by a merged cell (FR-059). A Markdown table cannot be merged, so the readers
    /// put this one character in such a cell; the renderer writes it as <c>[merged]</c> in the language of the job.
    /// </summary>
    public const string MergedCell = "\uE000";

    /// <summary>Writes <see cref="MergedCell"/> as the mark of the language (<c>[結合]</c>, <c>[merged]</c>).</summary>
    public static string ResolveMarks(string markdown, string language) =>
        markdown.Contains(MergedCell, StringComparison.Ordinal)
            ? markdown.Replace(MergedCell, Localization.Loc.M("[merged]", language), StringComparison.Ordinal)
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

        for (var r = 0; r < rows.Count; r++)
        {
            builder.Append('|');
            for (var c = 0; c < columns; c++)
            {
                var cell = c >= rows[r].Count ? string.Empty
                    : rows[r][c] == MergedCell ? MergedCell
                    : EscapeCell(rows[r][c].Replace(MergedCell, string.Empty, StringComparison.Ordinal));
                builder.Append(' ').Append(cell).Append(" |");
            }

            builder.Append('\n');
            if (r == 0 && hasHeader)
            {
                Separator();
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    // A line that starts a block in Markdown: #, >, -, +, *, or "1." / "1)" after optional spaces.
    [GeneratedRegex(@"^(\s{0,3}(?:#{1,6}(?=\s|$)|>|[-+*](?=\s)|\d{1,9}[.)](?=\s)|\|))")]
    private static partial Regex BlockStart();
}
