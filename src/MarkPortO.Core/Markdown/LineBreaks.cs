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

/// <summary>
/// A line break inside a paragraph is only a space in Markdown, so a viewer shows the lines as one. In the page of the preview (the Markdown itself is not changed) a line that goes on with another plain line gets a <c>&lt;br&gt;</c> at its end, as a line break in a table cell does.
/// </summary>
internal static partial class LineBreaks
{
    public const string Tag = "<br>";

    [GeneratedRegex(@"^\s*(#{1,6}(\s|$)|>|[-*+]\s|\d{1,9}[.)]\s|\||<|```|~~~|=+\s*$|-{3,}\s*$)")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"(?:<br\s*/?>|[ \t]{2,})$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingBreak();

    public static string Mark(string markdown)
    {
        if (!markdown.Contains('\n', StringComparison.Ordinal))
        {
            return markdown;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inFence = false;
        var plain = new bool[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            // A line that starts with a space or a tab may go on with a list item or a code block: it is left as it is.
            plain[i] = !inFence && lines[i].Trim().Length > 0 && !char.IsWhiteSpace(lines[i][0]) && !BlockStart().IsMatch(lines[i]);
        }

        var builder = new StringBuilder(markdown.Length + 16);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (plain[i] && i + 1 < lines.Length && plain[i + 1])
            {
                line = TrailingBreak().Replace(line, string.Empty).TrimEnd() + Tag;
            }

            builder.Append(line);
            if (i + 1 < lines.Length)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }
}
