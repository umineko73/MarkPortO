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

namespace MarkPortO.Core.Markdown;

/// <summary>Text with a strikethrough is written between two tildes, <c>~~text~~</c> (FR-092).</summary>
public static class Strikethrough
{
    public const string Mark = "~~";

    /// <summary>
    /// The text between <c>~~</c>. The spaces at its two ends stay outside (Markdown does not take <c>~~ text~~</c> for a strikethrough); a text of
    /// nothing but spaces is left as it is.
    /// </summary>
    public static string Wrap(string text)
    {
        var core = text.Trim();
        if (core.Length == 0)
        {
            return text;
        }

        var start = text.IndexOf(core[0], StringComparison.Ordinal);
        var end = start + core.Length;
        return text[..start] + Mark + core + Mark + text[end..];
    }

    /// <summary>
    /// Two runs with a strikethrough next to each other (or with only spaces between them) are one strikethrough: <c>~~a~~~~b~~</c> becomes
    /// <c>~~ab~~</c>, and <c>~~a~~ ~~b~~</c> becomes <c>~~a b~~</c>.
    /// </summary>
    public static string Merge(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"~~(\s*)~~", "$1");

    /// <summary>The text as the PDF has it, without the marks of a strikethrough (to look for it in the text of a page).</summary>
    public static string Remove(string text) => text.Replace(Mark, string.Empty, StringComparison.Ordinal);
}
