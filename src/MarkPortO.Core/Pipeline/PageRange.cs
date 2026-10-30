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

using System.Globalization;
using System.Text;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Pipeline;

/// <summary>
/// The pages that the user asked for (FR-097): page numbers from 1, separated by commas. <c>3</c>, <c>3-5</c>, <c>8-</c> (from page 8 to the last)
/// and <c>-4</c> (up to page 4) can be mixed: <c>1,3-5,8-</c>. An empty text means every page.
/// </summary>
public sealed class PageRange
{
    private readonly List<(int From, int To)> _parts;

    private PageRange(List<(int From, int To)> parts) => _parts = parts;

    /// <summary>The highest page number that is named; an open end (<c>8-</c>) counts as its start.</summary>
    public int Highest => _parts.Max(p => p.To == int.MaxValue ? p.From : p.To);

    public bool Contains(int page) => _parts.Any(p => page >= p.From && page <= p.To);

    /// <summary>Reads the text. Returns <c>null</c> for an empty text (every page).</summary>
    /// <exception cref="FormatException">The text is not a page range; the message says why.</exception>
    public static PageRange? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = new List<(int, int)>();
        foreach (var raw in Normalise(text).Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                throw Bad(text);
            }

            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                var page = Number(part, text);
                parts.Add((page, page));
            }
            else
            {
                var from = dash == 0 ? 1 : Number(part[..dash].Trim(), text);
                var tail = part[(dash + 1)..].Trim();
                var to = tail.Length == 0 ? int.MaxValue : Number(tail, text);
                if (dash == 0 && tail.Length == 0)
                {
                    throw Bad(text);
                }

                if (from > to)
                {
                    throw new FormatException(Loc.T("The page range '{0}' goes backwards: {1}.", text, part));
                }

                parts.Add((from, to));
            }
        }

        return new PageRange(parts);
    }

    /// <summary>The text with full-width digits, commas and hyphens made plain.</summary>
    private static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                >= '０' and <= '９' => (char)('0' + (c - '０')),
                '，' or '、' => ',',
                '－' or '−' or 'ー' or '～' or '~' => '-',
                _ => c,
            });
        }

        return builder.ToString();
    }

    private static int Number(string digits, string whole)
    {
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            throw Bad(whole);
        }

        return number;
    }

    private static FormatException Bad(string text) =>
        new(Loc.T("'{0}' is not a page range. Write page numbers from 1 like 3, 3-5, 8- or -4, separated by commas.", text));
}
