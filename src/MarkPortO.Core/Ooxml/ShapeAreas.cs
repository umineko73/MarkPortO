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

namespace MarkPortO.Core.Ooxml;

/// <summary>Where a drawn shape is on the image of its page (FR-136).</summary>
public static class ShapeAreas
{
    /// <summary>The size of an A3 page in points: the paper that a part of a sheet is printed on (<see cref="ExcelTempCopy"/>).</summary>
    public const double A3LongPoints = 420 / 25.4 * 72;

    public const double A3ShortPoints = 297 / 25.4 * 72;

    /// <summary>The margin of the printed part of a sheet, in points (<see cref="ExcelTempCopy"/>: a quarter of an inch).</summary>
    public const double SheetMarginPoints = 18;

    /// <summary>The room around a cut shape of a sheet, in points: the place is worked out, so it can be a little off.</summary>
    public const double SheetRoomPoints = 6;

    /// <summary>
    /// The place of a box (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>, in any one unit) on a page of
    /// <paramref name="pageWidth"/> by <paramref name="pageHeight"/>, in fractions of the page: turned by <paramref name="degrees"/> about its centre,
    /// with <paramref name="room"/> around it, and kept on the page. Null when nothing of it is on the page.
    /// </summary>
    public static ShapeArea? Of(double x, double y, double width, double height, double degrees, double room, double pageWidth, double pageHeight, int? page = null)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return null;
        }

        // The box that holds the turned shape.
        var radians = degrees * Math.PI / 180;
        var turnedWidth = Math.Abs(width * Math.Cos(radians)) + Math.Abs(height * Math.Sin(radians));
        var turnedHeight = Math.Abs(width * Math.Sin(radians)) + Math.Abs(height * Math.Cos(radians));
        var centreX = x + width / 2;
        var centreY = y + height / 2;
        var left = Math.Max(0, centreX - turnedWidth / 2 - room);
        var top = Math.Max(0, centreY - turnedHeight / 2 - room);
        var right = Math.Min(pageWidth, centreX + turnedWidth / 2 + room);
        var bottom = Math.Min(pageHeight, centreY + turnedHeight / 2 + room);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new ShapeArea(left / pageWidth, top / pageHeight, (right - left) / pageWidth, (bottom - top) / pageHeight, page);
    }

    /// <summary>
    /// How much a part of a sheet is shrunk to fit its page (<see cref="ExcelTempCopy"/>): its columns on the width of the page, and for
    /// <see cref="Settings.ExcelRange.Sheet"/> also its rows on the height. It is never enlarged. Excel shrinks by a whole percent.
    /// </summary>
    public static double SheetScale(double partWidth, double partHeight, double pageWidth, double pageHeight, bool fitHeight)
    {
        var scale = 1.0;
        if (partWidth > 0)
        {
            scale = Math.Min(scale, (pageWidth - 2 * SheetMarginPoints) / partWidth);
        }

        if (fitHeight && partHeight > 0)
        {
            scale = Math.Min(scale, (pageHeight - 2 * SheetMarginPoints) / partHeight);
        }

        // A little is added so that an exact percent (0.5 as 0.49999…) is not taken for the one below it.
        return Math.Floor(scale * 100 + 1e-6) / 100;
    }
}
