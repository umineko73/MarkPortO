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

using System.Globalization;

namespace MarkPort.Core.Localization;

/// <summary>
/// Where a page is in its source: page 3 of a Word document, slide 3, the sheet "Sales". It is saved as a code
/// (<c>page:3</c>, <c>slide:3</c>, <c>sheet:Sales</c>), so it can be shown in any language, whichever language the
/// job was started in.
/// </summary>
public static class PageLabel
{
    public static string Page(int number) => "page:" + number.ToString(CultureInfo.InvariantCulture);

    public static string Slide(int number) => "slide:" + number.ToString(CultureInfo.InvariantCulture);

    public static string Sheet(string name) => "sheet:" + name;

    /// <summary>The label in words, in a language: <c>3ページ目</c> / <c>Page 3</c>.</summary>
    public static string Text(string code, string language) => Text(code, CultureInfo.GetCultureInfo(language));

    /// <summary>The label in words, in a culture.</summary>
    public static string Text(string code, CultureInfo culture)
    {
        var colon = code.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return code; // a label saved as plain text
        }

        var value = code[(colon + 1)..];
        return code[..colon] switch
        {
            "page" => Loc.M("Page {0}", culture, value),
            "slide" => Loc.M("Slide {0}", culture, value),
            "sheet" => Loc.M("Sheet \"{0}\"", culture, value),
            _ => code,
        };
    }
}
