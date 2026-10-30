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
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MarkPortO.Core.Vlm;

/// <summary>
/// The header, the footer and the page number that are printed on every page of a Word document (FR-100). The VLM sees them in the image and
/// writes them at the top and the bottom of the result of each page, where they stand between the parts of a table that runs over several
/// pages, so that the parts are not joined (FR-089). The texts are known from the header and footer parts of the file; the lines of the result
/// that are one of them (a wrong character or a page number that differs does not matter) are taken out of the top and the bottom of the result.
/// </summary>
public static partial class PageFurniture
{
    /// <summary>How many lines from the top and from the bottom of a result are looked at.</summary>
    private const int EdgeLines = 3;

    private const double MinimumSimilarity = 0.7;

    /// <summary>The texts of the headers and the footers of the file, in the form that <see cref="Strip"/> compares (see <see cref="Normalise"/>).</summary>
    public static IReadOnlyList<string> Read(string docxPath)
    {
        var texts = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = WordprocessingDocument.Open(docxPath, isEditable: false);
            var main = document.MainDocumentPart;
            if (main is null)
            {
                return [];
            }

            var paragraphs = main.HeaderParts.SelectMany(p => p.Header?.Descendants<Paragraph>() ?? [])
                .Concat(main.FooterParts.SelectMany(p => p.Footer?.Descendants<Paragraph>() ?? []));
            foreach (var paragraph in paragraphs)
            {
                var normalised = Normalise(string.Concat(paragraph.Descendants<Text>().Select(t => t.Text)));
                if (normalised.Length > 0)
                {
                    texts.Add(normalised);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or ArgumentException or FormatException or UnauthorizedAccessException)
        {
            return []; // a file that cannot be read here is reported by the step that needs it
        }

        return [.. texts];
    }

    /// <summary>
    /// Takes the lines out of the first and the last <see cref="EdgeLines"/> lines of the result that are a header, a footer or a page
    /// number. A line of a table is never taken. A result that would be empty is left as it is.
    /// </summary>
    public static string Strip(string text, IReadOnlyList<string> furniture)
    {
        if (furniture.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var filled = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim().Length > 0).ToList();
        var candidates = filled.Take(EdgeLines).Concat(filled.Skip(Math.Max(0, filled.Count - EdgeLines))).Distinct();
        var lonelyNumbers = furniture.Contains("#", StringComparer.Ordinal);
        var drop = new HashSet<int>(candidates.Where(i => !lines[i].TrimStart().StartsWith('|') && IsFurniture(Normalise(lines[i]), furniture, lonelyNumbers)));
        if (drop.Count == 0 || drop.Count == filled.Count)
        {
            return text;
        }

        var kept = Enumerable.Range(0, lines.Length).Where(i => !drop.Contains(i)).Select(i => lines[i]).ToList();

        // A rule (---) that stood between the page and its header or footer is left at the end: it goes too.
        while (kept.Count > 0 && (kept[^1].Trim().Length == 0 || IsRule(kept[^1])))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        while (kept.Count > 0 && (kept[0].Trim().Length == 0 || IsRule(kept[0])))
        {
            kept.RemoveAt(0);
        }

        return string.Join('\n', kept);
    }

    private static bool IsRule(string line)
    {
        var text = line.Trim();
        return text.Length >= 3 && text.All(c => c is '-' or '*' or '_');
    }

    /// <summary>
    /// A line in the form that is compared: without the marks of Markdown, in one width, in lower case, without the white space, and with every
    /// number written as <c>#</c> (a page number is another number on every page).
    /// </summary>
    public static string Normalise(string line)
    {
        var text = line.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        text = Marks().Replace(text, string.Empty);
        text = Space().Replace(text, string.Empty);
        return Digits().Replace(text, "#");
    }

    private static bool IsFurniture(string line, IReadOnlyList<string> furniture, bool lonelyNumbers)
    {
        if (line.Length == 0)
        {
            return false;
        }

        if (line == "#")
        {
            return lonelyNumbers;
        }

        return PageNumber().IsMatch(line) || furniture.Any(f => Similar(line, f));
    }

    private static bool Similar(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        var longest = Math.Max(a.Length, b.Length);
        if (Math.Min(a.Length, b.Length) < 4)
        {
            return false; // a short text is the same, or it is another text
        }

        return 1.0 - (double)Distance(a, b) / longest >= MinimumSimilarity;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            previous = current;
        }

        return previous[b.Length];
    }

    // A page number in the usual forms, with the numbers already written as #: ページ#, page#, p.#, -#-, #/#, page#of#.
    [GeneratedRegex(@"^(?:-#-|[-–—]?(?:ページ|page|p\.?)#(?:/#|of#|/#ページ)?[-–—]?|#/#)$")]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"[#*>_`|~]")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
