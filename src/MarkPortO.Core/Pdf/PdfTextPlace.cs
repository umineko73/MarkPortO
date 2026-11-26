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
using UglyToad.PdfPig.Content;

namespace MarkPortO.Core.Pdf;

/// <summary>
/// Where a text of the document is on a PDF page, by its letters (spaces left out): how far down the page a paragraph is, so that a drawing anchored in
/// it can be found next to it (FR-136).
/// </summary>
public sealed class PdfTextPlace
{
    /// <summary>The letters of the start (or the end) of a text that are looked for: enough to tell one paragraph from another.</summary>
    public const int Letters = 16;

    private readonly string _text;
    private readonly List<(double Top, double Bottom)> _places;
    private readonly double _height;

    public PdfTextPlace(Page page)
    {
        _height = page.Height;
        var text = new StringBuilder();
        _places = [];
        foreach (var letter in page.Letters)
        {
            foreach (var c in letter.Value)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                text.Append(c);
                var box = letter.BoundingBox;
                _places.Add((_height - box.Top, _height - box.Bottom));
            }
        }

        _text = text.ToString();
    }

    /// <summary>How far down the page (a part of its height) the start of <paramref name="text"/> is: the top of its first letter; null when it is not found.</summary>
    public double? TopOf(string text)
    {
        var key = Key(text, fromEnd: false);
        var at = key.Length == 0 ? -1 : _text.IndexOf(key, StringComparison.Ordinal);
        return at < 0 || _height <= 0 ? null : _places[at].Top / _height;
    }

    /// <summary>How far down the page (a part of its height) the end of <paramref name="text"/> is: the bottom of its last letter; null when it is not found.</summary>
    public double? BottomOfEnd(string text)
    {
        var key = Key(text, fromEnd: true);
        var at = key.Length == 0 ? -1 : _text.LastIndexOf(key, StringComparison.Ordinal);
        return at < 0 || _height <= 0 ? null : _places[at + key.Length - 1].Bottom / _height;
    }

    // The first (or the last) letters of a text, without spaces.
    private static string Key(string text, bool fromEnd)
    {
        var letters = new string([.. text.Where(c => !char.IsWhiteSpace(c))]);
        return letters.Length <= Letters ? letters : fromEnd ? letters[^Letters..] : letters[..Letters];
    }
}
