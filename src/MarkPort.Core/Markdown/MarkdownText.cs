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

using System.Text;
using System.Text.RegularExpressions;

namespace MarkPort.Core.Markdown;

/// <summary>Small helpers that keep text from a document from being read as Markdown or as MarkPort's own markers.</summary>
public static partial class MarkdownText
{
    /// <summary>
    /// Escapes text so it is shown as written: MarkPort's HTML comment markers are neutralised
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

    /// <summary>Builds a Markdown pipe table; the first row is the header. Rows may differ in length.</summary>
    public static string Table(IReadOnlyList<IReadOnlyList<string>> rows)
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
        for (var r = 0; r < rows.Count; r++)
        {
            builder.Append('|');
            for (var c = 0; c < columns; c++)
            {
                var cell = c < rows[r].Count ? EscapeCell(rows[r][c]) : string.Empty;
                builder.Append(' ').Append(cell).Append(" |");
            }

            builder.Append('\n');
            if (r == 0)
            {
                builder.Append('|');
                for (var c = 0; c < columns; c++)
                {
                    builder.Append("---|");
                }

                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    // A line that starts a block in Markdown: #, >, -, +, *, or "1." / "1)" after optional spaces.
    [GeneratedRegex(@"^(\s{0,3}(?:#{1,6}(?=\s|$)|>|[-+*](?=\s)|\d{1,9}[.)](?=\s)|\|))")]
    private static partial Regex BlockStart();
}
