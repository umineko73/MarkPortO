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

using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Vlm;

/// <summary>
/// The fixed words that follow the prompt of the user when a page is sent to the VLM (FR-042). They are written in
/// the language of the conversion and cannot be changed in the settings.
/// </summary>
public static class PromptSupplement
{
    /// <summary>In the prompt of the user, this is replaced by the text of the page that Step1 took from the document (FR-061).</summary>
    public const string PageTextPlaceholder = "{pagetext}";

    /// <summary>The longest text of a page that is put into a prompt, in characters (FR-061).</summary>
    public const int MaxPageTextCharacters = 20000;

    public static bool UsesPageText(string prompt) => prompt.Contains(PageTextPlaceholder, StringComparison.Ordinal);

    /// <summary>
    /// The prompt of the user and the supplement. <paramref name="bands"/> above 1 tells which part of the page the image is.
    /// <paramref name="pageText"/> is put in place of <see cref="PageTextPlaceholder"/>; without it the placeholder is left as it is.
    /// </summary>
    public static string Build(string prompt, string language, int band = 1, int bands = 1, string? pageText = null)
    {
        if (pageText is not null)
        {
            prompt = prompt.Replace(PageTextPlaceholder, pageText, StringComparison.Ordinal);
        }

        var text = prompt + " " + Loc.M("If a table in the image has no header row, do not make one; write only the rows that are in the image.", language);
        if (bands > 1)
        {
            text += " " + Loc.M("This image is part {0} of {1} of one page, cut from the top. Write only what is in this image.", language, band, bands);
        }

        return text;
    }
}
