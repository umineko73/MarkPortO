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
/// Bold text is written between <c>**</c>, italic text between <c>*</c> (FR-101). The readers do not write the marks themselves: they put one private
/// character at each end of the text, so that two runs next to each other are one span, and so that the places that look for the text of a page
/// (in the text of the PDF) can leave the marks out. The characters become <c>**</c> and <c>*</c> where the Markdown is written
/// (<see cref="MarkdownText.ResolveMarks"/>).
/// </summary>
public static partial class TextEmphasis
{
    /// <summary>The place of a <c>**</c> (the same character opens and closes a span).</summary>
    public const char BoldMark = '';

    /// <summary>The place of a <c>*</c>.</summary>
    public const char ItalicMark = '';

    /// <summary>The text between the marks of bold. The white space at its two ends stays outside (Markdown does not take <c>** text**</c> for bold).</summary>
    public static string Bold(string text) => Wrap(text, BoldMark);

    public static string Italic(string text) => Wrap(text, ItalicMark);

    private static string Wrap(string text, char mark)
    {
        var core = text.Trim();
        if (core.Length == 0)
        {
            return text;
        }

        var start = text.IndexOf(core[0], StringComparison.Ordinal);
        return text[..start] + mark + core + mark + text[(start + core.Length)..];
    }

    public static bool HasMarks(string text) => text.Contains(BoldMark) || text.Contains(ItalicMark);

    /// <summary>Two spans next to each other (or with only white space between them) are one: the end of the first and the start of the second go.</summary>
    public static string Merge(string text)
    {
        if (!HasMarks(text))
        {
            return text;
        }

        // Spans inside spans (italic in bold) come together only after the outer ones are joined: go on until nothing changes.
        string before;
        do
        {
            before = text;
            text = Adjacent().Replace(text, "$2");
        }
        while (text != before);

        return text;
    }

    /// <summary>The marks written as <c>**</c> and <c>*</c>, line by line: a span that does not end in its line (a cut paragraph) loses its mark.</summary>
    public static string Resolve(string text)
    {
        if (!HasMarks(text))
        {
            return text;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = ResolveLine(ResolveLine(lines[i], BoldMark, "**"), ItalicMark, "*");
        }

        return string.Join('\n', lines);
    }

    private static string ResolveLine(string line, char mark, string written)
    {
        var positions = new List<int>();
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == mark)
            {
                positions.Add(i);
            }
        }

        if (positions.Count == 0)
        {
            return line;
        }

        // The marks come in pairs. The last of an odd number has no partner, and a pair that Markdown would not take for a span (see CanOpen) goes.
        var dropped = new HashSet<int>();
        for (var k = 0; k < positions.Count; k += 2)
        {
            if (k + 1 >= positions.Count)
            {
                dropped.Add(positions[k]);
                break;
            }

            if (!CanOpen(line, positions[k]) || !CanClose(line, positions[k + 1]))
            {
                dropped.Add(positions[k]);
                dropped.Add(positions[k + 1]);
            }
        }

        var builder = new StringBuilder(line.Length + positions.Count);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != mark)
            {
                builder.Append(line[i]);
            }
            else if (!dropped.Contains(i))
            {
                builder.Append(written);
            }
        }

        return builder.ToString();
    }

    // CommonMark: a delimiter run that starts a span is "left-flanking": the character after it is not white space, and is not punctuation unless the
    // character before it is white space or punctuation. One that ends a span is "right-flanking", the same the other way round. So **「重要」**です
    // (punctuation inside, a letter outside) is not bold in a Markdown viewer: the marks are left out there. The marks of the other kind
    // (italic in bold) are looked through.
    private static bool CanOpen(string line, int at)
    {
        var after = Visible(line, at + 1, +1);
        var before = Visible(line, at - 1, -1);
        if (after is null || char.IsWhiteSpace(after.Value))
        {
            return false;
        }

        return !IsPunctuation(after.Value) || before is null || char.IsWhiteSpace(before.Value) || IsPunctuation(before.Value);
    }

    private static bool CanClose(string line, int at)
    {
        var before = Visible(line, at - 1, -1);
        var after = Visible(line, at + 1, +1);
        if (before is null || char.IsWhiteSpace(before.Value))
        {
            return false;
        }

        return !IsPunctuation(before.Value) || after is null || char.IsWhiteSpace(after.Value) || IsPunctuation(after.Value);
    }

    private static char? Visible(string line, int from, int step)
    {
        for (var i = from; i >= 0 && i < line.Length; i += step)
        {
            if (line[i] != BoldMark && line[i] != ItalicMark)
            {
                return line[i];
            }
        }

        return null;
    }

    private static bool IsPunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    /// <summary>The text without the marks (to look for it in the text of a PDF).</summary>
    public static string Remove(string text) => text.Replace(BoldMark.ToString(), string.Empty, StringComparison.Ordinal).Replace(ItalicMark.ToString(), string.Empty, StringComparison.Ordinal);

    [GeneratedRegex("([])(\\s*)\\1")]
    private static partial Regex Adjacent();
}
