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
    /// <summary>
    /// A table that runs over pages, as the prompt of one page tells it: the rows of the later pages that were put into the text of this page
    /// (<paramref name="CarriedRows"/>, after the <paramref name="HeadRows"/> rows of this page), and the page whose table the rows at the top of this page belong to.
    /// </summary>
    public sealed record TableContinuation(int HeadRows, int CarriedRows, int? WrittenBeforeFromPage, bool CarriesCode = false, int? CodeWrittenBeforeFromPage = null);

    /// <summary>In the prompt of the user, this is replaced by the text of the page that Step1 took from the document (FR-061).</summary>
    public const string PageTextPlaceholder = "{pagetext}";

    /// <summary>The longest text of a page that is put into a prompt, in characters (FR-061).</summary>
    public const int MaxPageTextCharacters = 20000;

    public static bool UsesPageText(string prompt) => prompt.Contains(PageTextPlaceholder, StringComparison.Ordinal);

    /// <summary>
    /// The prompt of the user and the supplement. <paramref name="bands"/> above 1 tells which part of the page the image is.
    /// <paramref name="pageText"/> is put in place of <see cref="PageTextPlaceholder"/>; without it the placeholder is left as it is.
    /// </summary>
    /// <summary>
    /// The prompt with a sentence of its own put in front of the paragraph that holds <c>{pagetext}</c> (or at the end when it has none), as
    /// <see cref="Build"/> does with the supplement of a page. <c>{pagetext}</c> is left for the caller.
    /// </summary>
    public static string WithSupplement(string prompt, string supplement)
    {
        var at = prompt.IndexOf(PageTextPlaceholder, StringComparison.Ordinal);
        var cut = -1;
        if (at >= 0)
        {
            var breaks = System.Text.RegularExpressions.Regex.Matches(prompt[..at], @"\r?\n[ \t]*\r?\n");
            cut = breaks.Count == 0 ? -1 : breaks[^1].Index;
        }

        return cut < 0 ? prompt + " " + supplement : prompt[..cut] + "\n\n" + supplement + prompt[cut..];
    }

    public static string Build(string prompt, string language, int band = 1, int bands = 1, string? pageText = null, TableContinuation? table = null)
    {
        // A page that carries the rows of the next pages writes rows that are not in its image, so the words about "only the rows in the image" are left out.
        var supplement = table is { CarriedRows: > 0 }
            ? string.Empty
            : Loc.M("If a table in the image has no header row, do not make one; write only the rows that are in the image.", language);
        if (table is { CarriedRows: > 0 })
        {
            supplement += Loc.M("The table at the end of this page goes on to the next page. The extracted text has the whole table: the first {0} rows are in this image, and the {1} rows after them come from the next page and are not in the image. Write all of them as one table, using the rows of the extracted text as they are.", language, table.HeadRows, table.CarriedRows);
        }

        if (table is { WrittenBeforeFromPage: { } from })
        {
            supplement += (supplement.Length > 0 ? " " : string.Empty)
                + Loc.M("The rows at the top of this image go on a table of page {0}, where they are already written. Do not write them again; write only the rest of this page.", language, from);
        }

        if (table is { CarriesCode: true })
        {
            supplement += (supplement.Length > 0 ? " " : string.Empty)
                + Loc.M("The code block at the end of this page goes on to the next page. The extracted text has the whole code block, also the lines that are not in the image. Write all of it as one code block.", language);
        }

        if (table is { CodeWrittenBeforeFromPage: { } codeFrom })
        {
            supplement += (supplement.Length > 0 ? " " : string.Empty)
                + Loc.M("The lines at the top of this image go on a code block of page {0}, where they are already written. Do not write them again; write only the rest of this page.", language, codeFrom);
        }

        if (bands > 1)
        {
            supplement += " " + Loc.M("This image is part {0} of {1} of one page, cut from the top. Write only what is in this image.", language, band, bands);
        }

        // The words that never change come first and the text of the page last, so that a server that caches the start
        // of a prompt can reuse it for every page. The supplement goes in front of the paragraph that holds {pagetext}.
        var at = prompt.IndexOf(PageTextPlaceholder, StringComparison.Ordinal);
        // A text box writes line breaks as CR LF, so a blank line is any two line breaks.
        var cut = -1;
        if (at >= 0)
        {
            var breaks = System.Text.RegularExpressions.Regex.Matches(prompt[..at], @"\r?\n[ \t]*\r?\n");
            cut = breaks.Count == 0 ? -1 : breaks[^1].Index;
        }

        // Before the paragraph of the page text it is a paragraph of its own: put on the last line, it would read as a part of a rule or of a list.
        var text = cut < 0
            ? prompt + " " + supplement
            : prompt[..cut] + "\n\n" + supplement + prompt[cut..];
        return pageText is null ? text : text.Replace(PageTextPlaceholder, pageText, StringComparison.Ordinal);
    }
}
