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

    /// <summary>
    /// The drawn things of a page (the paths that are filled or stroked, and the pictures), as boxes in points from the top left. A box that covers
    /// nearly the whole page (a page border, a background) is left out.
    /// </summary>
    public static IReadOnlyList<DrawnBox> Drawn(Page page)
    {
        double width = page.Width, height = page.Height;
        var boxes = new List<DrawnBox>();
        void Add(UglyToad.PdfPig.Core.PdfRectangle box)
        {
            if (box.Width >= 0.9 * width && box.Height >= 0.9 * height)
            {
                return;
            }

            boxes.Add(new DrawnBox(box.Left, height - box.Top, box.Right, height - box.Bottom));
        }

        foreach (var path in page.Paths)
        {
            if (!path.IsClipping && (path.IsFilled || path.IsStroked) && path.GetBoundingRectangle() is { } box)
            {
                Add(box);
            }
        }

        foreach (var image in page.GetImages())
        {
            Add(image.BoundingBox);
        }

        return boxes;
    }

    // The middle of a box is in one of the areas (parts of a page of pageWidth by pageHeight points).
    private static bool InAny(DrawnBox box, IReadOnlyList<ShapeArea>? areas, double pageWidth, double pageHeight)
    {
        if (areas is null)
        {
            return false;
        }

        var x = (box.Left + box.Right) / 2 / pageWidth;
        var y = (box.Top + box.Bottom) / 2 / pageHeight;
        return areas.Any(a => x >= a.Left && x <= a.Left + a.Width && y >= a.Top && y <= a.Top + a.Height);
    }

    /// <summary>
    /// Something is drawn in <paramref name="area"/> (parts of a page of <paramref name="pageWidth"/> by <paramref name="pageHeight"/> points) that is not
    /// one of the drawings found before (<paramref name="taken"/>): the page of a drawing that the document places on its page.
    /// </summary>
    public static bool HasDrawing(IReadOnlyList<DrawnBox> boxes, double pageWidth, double pageHeight, ShapeArea area, IReadOnlyList<ShapeArea>? taken = null) =>
        boxes.Any(box => box.Left < (area.Left + area.Width) * pageWidth && box.Right > area.Left * pageWidth
            && box.Top < (area.Top + area.Height) * pageHeight && box.Bottom > area.Top * pageHeight
            && !InAny(box, taken, pageWidth, pageHeight));

    /// <summary>
    /// Finds a drawing on a page (of <paramref name="pageWidth"/> by <paramref name="pageHeight"/> points) from its size (<paramref name="width"/> by
    /// <paramref name="height"/>, parts of the page) and, when it is known, its place across (<paramref name="left"/>; else anywhere between
    /// <paramref name="bandLeft"/> and <paramref name="bandRight"/>). The drawn things that fit across, and are not in <paramref name="taken"/> (the
    /// drawings found before), are taken in windows of the height of the drawing that start at the top of one of them. Of the windows that have at least
    /// half of the most that a window has, the one that starts nearest to <paramref name="expectedTop"/> is the drawing (the first, when that is not
    /// known). Its area, with <paramref name="roomPoints"/> around it, or null when nothing is drawn there.
    /// </summary>
    public static ShapeArea? FindDown(
        IReadOnlyList<DrawnBox> drawn,
        double pageWidth,
        double pageHeight,
        double? left,
        double width,
        double height,
        double bandLeft,
        double bandRight,
        double? expectedTop,
        double roomPoints,
        IReadOnlyList<ShapeArea>? taken = null)
    {
        if (pageWidth <= 0 || pageHeight <= 0 || width <= 0 || height <= 0)
        {
            return null;
        }

        var h = height * pageHeight;
        var reachX = Math.Max(MinReachPoints, width * pageWidth * Reach);
        var reachY = Math.Max(MinReachPoints, h * Reach);
        var acrossLeft = (left ?? bandLeft) * pageWidth - reachX;
        var acrossRight = (left is { } l ? l + width : bandRight) * pageWidth + reachX;
        var boxes = drawn
            .Where(b => b.Left >= acrossLeft && b.Right <= acrossRight && b.Bottom - b.Top <= h + reachY && !InAny(b, taken, pageWidth, pageHeight))
            .OrderBy(b => b.Top)
            .ToList();
        if (boxes.Count == 0)
        {
            return null;
        }

        // The weight of the window that starts at each box: what lies wholly in it, a box by its size, a line (no area) by its length.
        // The boxes are in order of their tops, so a window is the boxes from its start until one starts below its end.
        static double Weight(DrawnBox b) => (b.Right - b.Left + 1) * (b.Bottom - b.Top + 1);
        var weights = new double[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
        {
            var end = boxes[i].Top + h + reachY;
            for (var j = i; j < boxes.Count && boxes[j].Top <= end; j++)
            {
                if (boxes[j].Bottom <= end)
                {
                    weights[i] += Weight(boxes[j]);
                }
            }
        }

        var most = weights.Max();
        var expected = expectedTop * pageHeight;
        var start = -1;
        for (var i = 0; i < boxes.Count; i++)
        {
            if (weights[i] >= most / 2 && (start < 0 || expected is { } y && Math.Abs(boxes[i].Top - y) < Math.Abs(boxes[start].Top - y)))
            {
                start = i;
            }
        }

        var top = boxes[start].Top;
        var bottomLimit = top + h + reachY;
        double foundLeft = double.MaxValue, foundRight = double.MinValue, foundBottom = top + h;
        for (var j = start; j < boxes.Count && boxes[j].Top <= bottomLimit; j++)
        {
            if (boxes[j].Bottom <= bottomLimit)
            {
                foundLeft = Math.Min(foundLeft, boxes[j].Left);
                foundRight = Math.Max(foundRight, boxes[j].Right);
                foundBottom = Math.Max(foundBottom, boxes[j].Bottom);
            }
        }

        if (left is { } known)
        {
            foundLeft = Math.Min(foundLeft, known * pageWidth);
            foundRight = Math.Max(foundRight, (known + width) * pageWidth);
        }

        return ShapeAreas.Of(foundLeft, top, foundRight - foundLeft, foundBottom - top, 0, roomPoints, pageWidth, pageHeight);
    }
}

/// <summary>A drawn thing of a PDF page (<see cref="PdfShapeSnap.Drawn"/>), in points from the top left of the page.</summary>
public readonly record struct DrawnBox(double Left, double Top, double Right, double Bottom);
