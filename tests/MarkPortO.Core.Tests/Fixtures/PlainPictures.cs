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


using System.Net;
using System.Text.RegularExpressions;

namespace MarkPortO.Core.Tests.Fixtures;

/// <summary>A picture of a document is an <c>&lt;img&gt;</c> with its width when the size is known (FR-148); this writes it as <c>![text](file)</c> again, for the tests of where and which.</summary>
internal static partial class PlainPictures
{
    public static string Of(string markdown) =>
        Img().Replace(markdown, match => "![" + WebUtility.HtmlDecode(match.Groups[2].Value) + "](" + match.Groups[1].Value + ")");

    [GeneratedRegex("""<img src="([^"]*)" alt="([^"]*)" width="\d+">""")]
    private static partial Regex Img();
}
