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

using MarkPortO.Core.Jobs.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MarkPortO.Core.Pdf;

/// <summary>
/// Finds the lines of text of a PDF page and where each one is (FR-041). The letters that stand on the same baseline are
/// one line, so the cells of a table row are one line. The positions are fractions of the height of the page.
/// </summary>
public static class PdfLineExtractor
{
    /// <summary>The lines of a page (0-based), from the top. Throws when the PDF cannot be read.</summary>
    public static IReadOnlyList<LineSpan> GetLines(string pdfPath, int pageIndex)
    {
        using var document = PdfDocument.Open(pdfPath);
        return GetLines(document, pageIndex);
    }

    /// <summary>The lines of a page of a document that is already open.</summary>
    public static IReadOnlyList<LineSpan> GetLines(PdfDocument document, int pageIndex)
    {
        var page = document.GetPage(pageIndex + 1);
        return Cluster(page);
    }

    /// <summary>Like <see cref="GetLines(string, int)"/>, but <c>null</c> when the PDF cannot be read (the page is then read whole).</summary>
    public static IReadOnlyList<LineSpan>? TryGetLines(string pdfPath, int pageIndex)
    {
        try
        {
            return GetLines(pdfPath, pageIndex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static List<LineSpan> Cluster(Page page)
    {
        var height = page.Height;
        var letters = page.Letters
            .Where(l => !string.IsNullOrWhiteSpace(l.Value))
            .OrderByDescending(l => l.StartBaseLine.Y)
            .ToList();
        if (letters.Count == 0 || height <= 0)
        {
            return [];
        }

        var groups = new List<List<Letter>>();
        foreach (var letter in letters)
        {
            var group = groups.Count > 0 ? groups[^1] : null;
            if (group is not null && Math.Abs(group.Average(l => l.StartBaseLine.Y) - letter.StartBaseLine.Y) <= Tolerance(letter))
            {
                group.Add(letter);
            }
            else
            {
                groups.Add([letter]);
            }
        }

        var spans = groups
            .Select(g => new LineSpan(
                Math.Clamp(1.0 - ((double)g.Max(l => l.BoundingBox.Top) / height), 0.0, 1.0),
                Math.Clamp(1.0 - ((double)g.Min(l => l.BoundingBox.Bottom) / height), 0.0, 1.0)))
            .OrderBy(s => s.Top)
            .ToList();

        // Neighbouring lines whose glyphs reach into each other (tall letters over low ones) meet in the middle.
        for (var i = 1; i < spans.Count; i++)
        {
            if (spans[i].Top < spans[i - 1].Bottom)
            {
                var middle = (spans[i].Top + spans[i - 1].Bottom) / 2;
                spans[i - 1] = spans[i - 1] with { Bottom = middle };
                spans[i] = spans[i] with { Top = middle };
            }
        }

        return spans;
    }

    private static double Tolerance(Letter letter) => Math.Max(1.0, 0.3 * (double)letter.PointSize);
}
