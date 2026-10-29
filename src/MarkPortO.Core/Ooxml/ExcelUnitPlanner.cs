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

namespace MarkPortO.Core.Ooxml;

/// <summary>One image of a sheet: some columns of the used range, all rows (rows may continue on more pages).</summary>
/// <param name="SheetName">The worksheet.</param>
/// <param name="Part">1-based number of this part.</param>
/// <param name="PartCount">How many parts the sheet is split into (1 when all columns fit).</param>
/// <param name="FromColumnIndex">0-based index of the first column, counted from the first column of the used range.</param>
/// <param name="ToColumnIndex">0-based index of the last column (inclusive).</param>
/// <param name="ColumnRange">The columns as letters, for example <c>A:P</c>.</param>
public sealed record ExcelUnit(string SheetName, int Part, int PartCount, int FromColumnIndex, int ToColumnIndex, string ColumnRange);

/// <summary>
/// Decides how a sheet is cut into images so that all columns of a part fit on one page width
/// without shrinking below <see cref="MinScale"/> (FR-010b, FR-011, research R4). A page that is shrunk below
/// <see cref="ComfortScale"/> is rendered with a higher DPI, so that its text keeps the size it has at <see cref="ComfortScale"/> (FR-079).
/// </summary>
public static class ExcelUnitPlanner
{
    /// <summary>The page is shrunk to at most this fraction (a wider sheet is cut into parts).</summary>
    public const double MinScale = 0.2;

    /// <summary>Up to this shrinking the text of the image is as large as the DPI says; below it the DPI is raised (<see cref="DpiFactor"/>).</summary>
    public const double ComfortScale = 0.6;

    /// <summary>A3 landscape is 420 mm = 1190.55 points wide.</summary>
    public const double PaperWidthPoints = 1190.55;

    /// <summary>Left and right margin of 0.25 inch each.</summary>
    public const double MarginPoints = 18;

    public const double PrintableWidthPoints = PaperWidthPoints - (2 * MarginPoints);

    /// <summary>The widest group of columns (in points) that still fits one page at <see cref="MinScale"/>.</summary>
    public const double MaxGroupWidthPoints = PrintableWidthPoints / MinScale;

    /// <summary>
    /// How many times the DPI of the page image of a part is raised: a part that Excel shrinks below <see cref="ComfortScale"/> to fit the
    /// page width gets <c>ComfortScale / scale</c> times the DPI, so that the text in the image is as large as before (1 for a part that is shrunk less).
    /// </summary>
    public static double DpiFactor(SheetContent sheet, ExcelUnit unit)
    {
        var width = 0.0;
        for (var i = unit.FromColumnIndex; i <= unit.ToColumnIndex && i < sheet.ColumnWidthsPoints.Length; i++)
        {
            width += sheet.ColumnWidthsPoints[i];
        }

        var scale = width <= 0 ? 1.0 : Math.Min(1.0, PrintableWidthPoints / width);
        return scale >= ComfortScale ? 1.0 : ComfortScale / scale;
    }

    /// <summary>
    /// The images of a sheet. An empty sheet has none. Columns are grouped from the left, as many as fit;
    /// a single column wider than the limit gets a group of its own (it is shrunk more than the minimum).
    /// </summary>
    public static IReadOnlyList<ExcelUnit> Plan(SheetContent sheet, ExcelRange range = ExcelRange.Columns)
    {
        if (sheet.IsEmpty)
        {
            return [];
        }

        // Only the columns mode cuts the sheet by its columns; the others print the sheet as one unit and let the page setup cut it.
        if (range != ExcelRange.Columns)
        {
            var last = sheet.ColumnWidthsPoints.Length - 1;
            return [new ExcelUnit(sheet.Name, 1, 1, 0, last, sheet.ColumnRange(0, last))];
        }

        var groups = new List<(int From, int To)>();
        var start = 0;
        var width = 0.0;
        for (var i = 0; i < sheet.ColumnWidthsPoints.Length; i++)
        {
            var columnWidth = sheet.ColumnWidthsPoints[i];
            if (i > start && width + columnWidth > MaxGroupWidthPoints)
            {
                groups.Add((start, i - 1));
                start = i;
                width = 0;
            }

            width += columnWidth;
        }

        groups.Add((start, sheet.ColumnWidthsPoints.Length - 1));

        return
        [
            .. groups.Select((g, index) =>
                new ExcelUnit(sheet.Name, index + 1, groups.Count, g.From, g.To, sheet.ColumnRange(g.From, g.To))),
        ];
    }
}
