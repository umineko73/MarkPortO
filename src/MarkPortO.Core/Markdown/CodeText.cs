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

namespace MarkPortO.Core.Markdown;

/// <summary>
/// Code in a document: a run in a code style or in a fixed-width font is written between backticks, and paragraphs of code are written as a fenced
/// code block (FR-140). Like bold and italic (<see cref="TextEmphasis"/>), the readers put one private character at each end of the text of a
/// run, so that two runs next to each other are one span; <see cref="MarkdownText.ResolveMarks"/> writes the backticks.
/// </summary>
public static class CodeText
{
    private const char Newline = (char)10;

    /// <summary>The place of a backtick fence of a span of code (the same character opens and closes a span).</summary>
    public const char CodeMark = (char)0xE004;

    public static bool HasMarks(string text) => text.Contains(CodeMark);

    /// <summary>The text between the marks. The white space at its two ends stays outside; a text of nothing but white space is left as it is.</summary>
    public static string Wrap(string text)
    {
        var core = text.Trim();
        if (core.Length == 0)
        {
            return text;
        }

        var start = text.IndexOf(core[0], StringComparison.Ordinal);
        return text[..start] + CodeMark + core + CodeMark + text[(start + core.Length)..];
    }

    /// <summary>Two spans next to each other (or with only white space between them) are one.</summary>
    public static string Merge(string text)
    {
        if (!HasMarks(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == CodeMark)
            {
                // An end mark, white space, a start mark: the three go (the white space stays, inside the one span).
                var j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j]) && text[j] != Newline)
                {
                    j++;
                }

                if (j < text.Length && text[j] == CodeMark && builder.Length > 0 && IsOpen(builder))
                {
                    builder.Append(text, i + 1, j - i - 1);
                    i = j + 1;
                    continue;
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    // The marks come in pairs: a mark is an end mark when an odd number of marks is before it.
    private static bool IsOpen(StringBuilder builder)
    {
        var count = 0;
        for (var k = 0; k < builder.Length; k++)
        {
            if (builder[k] == CodeMark)
            {
                count++;
            }
        }

        return count % 2 == 1;
    }

    /// <summary>The marks written as backticks. A mark without its partner goes.</summary>
    public static string Resolve(string text)
    {
        if (!HasMarks(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 8);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != CodeMark)
            {
                builder.Append(text[i]);
                i++;
                continue;
            }

            var end = text.IndexOf(CodeMark, i + 1);
            if (end < 0)
            {
                i++; // no partner
                continue;
            }

            builder.Append(Span(text.Substring(i + 1, end - i - 1)));
            i = end + 1;
        }

        return builder.ToString();
    }

    /// <summary>Text as a span of code: the fence is longer than any run of backticks in the text (and a space keeps a backtick at the edge from joining it).</summary>
    public static string Span(string code)
    {
        var fence = new string('`', LongestRun(code) + 1);
        var pad = code.StartsWith('`') || code.EndsWith('`') ? " " : string.Empty;
        return fence + pad + code + pad + fence;
    }

    /// <summary>
    /// The lines as a fenced code block. The fence is longer than any line of backticks in the code; the comment opener is made harmless (the same
    /// as everywhere in the Markdown: it would look like a marker of MarkPortO).
    /// </summary>
    public static string Block(IEnumerable<string> lines)
    {
        var all = lines.Select(l => l.TrimEnd().Replace("<!--", "&lt;!--", StringComparison.Ordinal)).ToList();
        while (all.Count > 0 && all[^1].Length == 0)
        {
            all.RemoveAt(all.Count - 1);
        }

        while (all.Count > 0 && all[0].Length == 0)
        {
            all.RemoveAt(0);
        }

        var fence = new string('`', Math.Max(3, all.Select(LongestRun).DefaultIfEmpty(0).Max() + 1));
        return fence + Newline + string.Join(Newline, all) + Newline + fence;
    }

    /// <summary>The Markdown is one fenced code block, as <see cref="Block"/> writes it.</summary>
    public static bool IsBlock(string markdown)
    {
        var text = markdown.Trim();
        return text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.Contains(Newline);
    }

    /// <summary>The lines inside a code block that <see cref="Block"/> wrote (without its fences).</summary>
    public static List<string> LinesOf(string markdown)
    {
        var lines = markdown.Trim().Split(Newline);
        return lines.Length < 2 ? [] : [.. lines[1..^1]];
    }

    /// <summary>Two code blocks as one: the lines of the second after those of the first, in a fence long enough for both.</summary>
    public static string Join(string first, string second) => Block(LinesOf(first).Concat(LinesOf(second)));

    private static int LongestRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    /// <summary>A fixed-width font: one whose name says so (Consolas, Courier New, Cascadia Mono, Menlo, ...).</summary>
    public static bool IsFixedWidthFont(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var part in new[] { "mono", "courier", "consolas", "typewriter", "console", "menlo", "fixedsys", "terminal" })
        {
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A style that the document uses for code: the name or the id says so (Verbatim Char, Source Code, HTML Code, HTML Preformatted, Code, コード, ...).</summary>
    public static bool IsCodeStyleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var compact = name.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var known in new[] { "VerbatimChar", "SourceCode", "HTMLCode", "HTMLPreformatted", "HTMLTypewriter", "CodeChar", "CodeBlock", "InlineCode", "コード", "ソースコード" })
        {
            if (compact.Equals(known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return compact.Equals("Code", StringComparison.OrdinalIgnoreCase);
    }
}
