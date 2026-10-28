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

namespace MarkPortO.Core.Vlm;

/// <summary>
/// Takes off the code block that a VLM sometimes puts around the Markdown it wrote (FR-067): a <c>```markdown</c> block,
/// or a block without a language that holds only table rows. Blocks of another language (<c>mermaid</c>, code) stay.
/// </summary>
public static partial class AnswerFences
{
    [GeneratedRegex(@"^\s*(`{3,})\s*([^\s`]*)\s*$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*(`{3,})\s*$")]
    private static partial Regex Closing();

    public static string Strip(string answer)
    {
        if (!answer.Contains("```", StringComparison.Ordinal))
        {
            return answer;
        }

        var lines = answer.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var remove = new bool[lines.Length];
        var i = 0;
        while (i < lines.Length)
        {
            var open = Fence().Match(lines[i]);
            if (!open.Success)
            {
                i++;
                continue;
            }

            var length = open.Groups[1].Length;
            var language = open.Groups[2].Value;
            var isMarkdown = language.Equals("markdown", StringComparison.OrdinalIgnoreCase) || language.Equals("md", StringComparison.OrdinalIgnoreCase);

            // A markdown block may hold a block of its own (```mermaid ... ```): count those, so that the first closing line is not taken for the end.
            var depth = 0;
            var end = -1;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var inner = Fence().Match(lines[j]);
                if (inner.Success && inner.Groups[2].Length > 0 && isMarkdown)
                {
                    depth++;
                }
                else if (Closing().Match(lines[j]) is { Success: true } closing && closing.Groups[1].Length >= length)
                {
                    if (isMarkdown && depth > 0)
                    {
                        depth--;
                        continue;
                    }

                    end = j;
                    break;
                }
            }

            if (end < 0)
            {
                // The answer was cut: only an opening line of a markdown block is taken off.
                if (isMarkdown)
                {
                    remove[i] = true;
                }

                break;
            }

            if (isMarkdown || (language.Length == 0 && OnlyTableRows(lines, i + 1, end)))
            {
                remove[i] = true;
                remove[end] = true;
            }

            i = end + 1;
        }

        return remove.Contains(true)
            ? string.Join('\n', lines.Where((_, index) => !remove[index])).Trim('\n')
            : answer;
    }

    private static bool OnlyTableRows(string[] lines, int from, int to)
    {
        var rows = 0;
        for (var i = from; i < to; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith('|'))
            {
                return false;
            }

            rows++;
        }

        return rows > 0;
    }
}
