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

using MarkPortO.Core.Ooxml;
using UglyToad.PdfPig.Content;

namespace MarkPortO.Core.Pdf;

/// <summary>
/// Moves the worked-out place of a drawn shape of a sheet onto the shape as it is drawn in the PDF of its page (FR-136). The place comes from the
/// widths of the columns and the heights of the rows, which the engine can draw a little differently; the PDF has the lines and fills of the shape.
/// </summary>
public static class PdfShapeSnap
{
    /// <summary>How far around the worked-out place the drawing of the shape is looked for: this part of its size on each side, and at least <see cref="MinReachPoints"/>.</summary>
    public const double Reach = 0.25;

    public const double MinReachPoints = 12;

    /// <summary>The drawing that is found must cover this part of the worked-out place, or the place is kept as it is.</summary>
    public const double MinCover = 0.3;

    /// <summary>
    /// The place of the shape on the page, moved onto the paths of the page that lie wholly inside the place with <see cref="Reach"/> around it
    /// and touch the place itself, with <paramref name="roomPoints"/> around them. The place as it is when no such paths are found.
    /// </summary>
    public static ShapeArea Snap(Page page, ShapeArea area, double roomPoints)
    {
        double width = page.Width, height = page.Height;
        if (width <= 0 || height <= 0)
        {
            return area;
        }

        // The place in points from the top left, without its room (the room is put back around what is found).
        var left = area.Left * width + roomPoints;
        var top = area.Top * height + roomPoints;
        var right = (area.Left + area.Width) * width - roomPoints;
        var bottom = (area.Top + area.Height) * height - roomPoints;
        if (right <= left || bottom <= top)
        {
            return area;
        }

        var reachX = Math.Max(MinReachPoints, (right - left) * Reach);
        var reachY = Math.Max(MinReachPoints, (bottom - top) * Reach);
        double? foundLeft = null, foundTop = null, foundRight = null, foundBottom = null;
        foreach (var path in page.Paths)
        {
            if (path.IsClipping || (!path.IsFilled && !path.IsStroked) || path.GetBoundingRectangle() is not { } box)
            {
                continue;
            }

            // The PDF counts from the bottom; the place counts from the top.
            var boxLeft = box.Left;
            var boxRight = box.Right;
            var boxTop = height - box.Top;
            var boxBottom = height - box.Bottom;
            var inside = boxLeft >= left - reachX && boxRight <= right + reachX && boxTop >= top - reachY && boxBottom <= bottom + reachY;
            var touches = boxLeft <= right && boxRight >= left && boxTop <= bottom && boxBottom >= top;
            if (!inside || !touches)
            {
                continue;
            }

            foundLeft = Math.Min(foundLeft ?? boxLeft, boxLeft);
            foundTop = Math.Min(foundTop ?? boxTop, boxTop);
            foundRight = Math.Max(foundRight ?? boxRight, boxRight);
            foundBottom = Math.Max(foundBottom ?? boxBottom, boxBottom);
        }

        if (foundLeft is null || foundTop is null || foundRight is null || foundBottom is null)
        {
            return area;
        }

        var coverWidth = Math.Min(right, foundRight.Value) - Math.Max(left, foundLeft.Value);
        var coverHeight = Math.Min(bottom, foundBottom.Value) - Math.Max(top, foundTop.Value);
        if (coverWidth <= 0 || coverHeight <= 0 || coverWidth * coverHeight < MinCover * (right - left) * (bottom - top))
        {
            return area;
        }

        return ShapeAreas.Of(foundLeft.Value, foundTop.Value, foundRight.Value - foundLeft.Value, foundBottom.Value - foundTop.Value, 0, roomPoints, width, height, area.Page) ?? area;
    }
}
