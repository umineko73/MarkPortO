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
/// How the rows of a printed sheet really lie on a page of its PDF (FR-136). The heights in the file are not always those that Excel prints with
/// (a row of the default height is drawn a little taller), and the difference grows down the sheet. The rows whose text is found once on the page
/// tell where they are: <c>printed = Scale × worked-out + Offset</c>.
/// </summary>
public sealed record PdfRowFit(double Scale, double Offset, int Rows)
{
    /// <summary>How far the scale may be from 1 before the fit is not believed.</summary>
    public const double MaxScaleChange = 0.2;

    public double Map(double workedOut) => Scale * workedOut + Offset;

    /// <summary>
    /// The fit of the rows of a page: each row is its text (one word without white space) and the worked-out place of its bottom, in points from
    /// the top of the page (Excel puts the text at the bottom of the cell). A row whose text is on the page once (and is the text of no other row)
    /// is used. With one row the offset only is fitted; with none, or a fit that is not believable, null.
    /// </summary>
    public static PdfRowFit? Fit(Page page, IReadOnlyList<(string Text, double Bottom)> rows)
    {
        var texts = rows.Select(r => Normalize(r.Text)).ToList();
        var unique = texts.GroupBy(t => t, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() == 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        if (unique.Count == 0)
        {
            return null;
        }

        var words = page.GetWords().GroupBy(w => Normalize(w.Text), StringComparer.Ordinal).Where(g => unique.Contains(g.Key) && g.Count() == 1)
            .ToDictionary(g => g.Key, g => page.Height - g.Single().BoundingBox.Bottom, StringComparer.Ordinal);
        var points = rows.Select((r, i) => (Worked: r.Bottom, Text: texts[i]))
            .Where(p => words.ContainsKey(p.Text))
            .Select(p => (p.Worked, Printed: words[p.Text]))
            .ToList();
        if (points.Count == 0)
        {
            return null;
        }

        var spread = points.Max(p => p.Worked) - points.Min(p => p.Worked);
        if (points.Count == 1 || spread < 36)
        {
            return new PdfRowFit(1, points.Average(p => p.Printed - p.Worked), points.Count);
        }

        var meanX = points.Average(p => p.Worked);
        var meanY = points.Average(p => p.Printed);
        var scale = points.Sum(p => (p.Worked - meanX) * (p.Printed - meanY)) / points.Sum(p => (p.Worked - meanX) * (p.Worked - meanX));
        if (double.IsNaN(scale) || Math.Abs(scale - 1) > MaxScaleChange)
        {
            return new PdfRowFit(1, points.Average(p => p.Printed - p.Worked), points.Count);
        }

        return new PdfRowFit(scale, meanY - scale * meanX, points.Count);
    }

    // The same text in the file and in the PDF: full-width and half-width letters folded, no white space.
    private static string Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormKC);
        return folded.Any(char.IsWhiteSpace) || folded.Length > 40 ? string.Empty : folded;
    }
}
