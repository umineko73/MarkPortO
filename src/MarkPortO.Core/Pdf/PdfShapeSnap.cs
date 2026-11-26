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
using UglyToad.PdfPig.Core;

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

    /// <summary>How far from the place down the page where a drawing that follows its paragraph is expected it may start (<see cref="FindDown"/>).</summary>
    public const double ExpectedReachPoints = 36;

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

    /// <summary>Two crossing lines of a "×" each reach over this part of the page across and down at least (<see cref="CrossShapeDetector.CoverageThreshold"/>, less a little).</summary>
    public const double CrossLineCover = 0.6;

    /// <summary>
    /// The outline of a multiply shape reaches over this part of the page across and down at least: the engine draws the "×" of the shape inside its
    /// box, about 0.7 of it across and 0.6 down.
    /// </summary>
    public const double CrossShapeCover = 0.35;

    /// <summary>
    /// A "×" over the page is drawn on the page (FR-012), in the forms that <see cref="CrossShapeDetector"/> knows: two straight lines that cross (one
    /// down to the right, one up to the right), each over <see cref="CrossLineCover"/> of the page; or the outline of a multiply shape (12 straight
    /// sides) over <see cref="CrossShapeCover"/> of it. A logo, a frame, a background or a diagram is not one.
    /// </summary>
    public static bool HasCross(Page page)
    {
        double width = page.Width, height = page.Height;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        bool down = false, up = false;
        foreach (var path in page.Paths)
        {
            if (path.IsClipping || (!path.IsFilled && !path.IsStroked))
            {
                continue;
            }

            foreach (var subpath in path)
            {
                var commands = subpath.Commands;
                if (commands.Any(c => c is not (PdfSubpath.Move or PdfSubpath.Line or PdfSubpath.Close)) || subpath.GetBoundingRectangle() is not { } box)
                {
                    continue;
                }

                var lines = commands.OfType<PdfSubpath.Line>().ToList();
                if (lines.Count == 1 && box.Width >= CrossLineCover * width && box.Height >= CrossLineCover * height)
                {
                    var (from, to) = (lines[0].From, lines[0].To);
                    if ((to.X - from.X) * (to.Y - from.Y) < 0)
                    {
                        down = true;
                    }
                    else
                    {
                        up = true;
                    }
                }
                else if (lines.Count is >= 11 and <= 12 && box.Width >= CrossShapeCover * width && box.Height >= CrossShapeCover * height && IsMultiply(subpath, box))
                {
                    return true;
                }
            }
        }

        return down && up;
    }

    // The outline of a multiply shape: 12 corners, 4 of them inside, where the arms of the "×" meet (near the middle), and 8 at the ends of the arms
    // (near the edge of its box). A polygon of 12 sides has all its corners near the edge.
    private static bool IsMultiply(PdfSubpath subpath, PdfRectangle box)
    {
        var corners = subpath.Commands.Select(c => c switch
        {
            PdfSubpath.Move move => (PdfPoint?)move.Location,
            PdfSubpath.Line line => line.To,
            _ => null,
        }).OfType<PdfPoint>().Distinct().ToList();
        if (corners.Count != 12 || box.Width <= 0 || box.Height <= 0)
        {
            return false;
        }

        var middle = box.Centroid;
        var reach = corners.Select(p => Math.Sqrt(Math.Pow((p.X - middle.X) / (box.Width / 2), 2) + Math.Pow((p.Y - middle.Y) / (box.Height / 2), 2))).ToList();
        return reach.Count(r => r < 0.7) == 4 && reach.Count(r => r > 0.9) == 8;
    }

    // A box lies wholly in one of the areas (parts of a page of pageWidth by pageHeight points): it is a drawing found before. A drawing that only
    // reaches into one is another drawing.
    private static bool InAny(DrawnBox box, IReadOnlyList<ShapeArea>? areas, double pageWidth, double pageHeight)
    {
        if (areas is null)
        {
            return false;
        }

        const double Slack = 0.5; // points
        return areas.Any(a => box.Left >= a.Left * pageWidth - Slack && box.Right <= (a.Left + a.Width) * pageWidth + Slack
            && box.Top >= a.Top * pageHeight - Slack && box.Bottom <= (a.Top + a.Height) * pageHeight + Slack);
    }

    /// <summary>The drawn things in a place must reach over this part of it across and down (without its room), or it is not the drawing there.</summary>
    public const double MinFill = 0.5;

    /// <summary>
    /// The drawing that the document places at <paramref name="area"/> (parts of a page of <paramref name="pageWidth"/> by <paramref name="pageHeight"/>
    /// points, with <paramref name="roomPoints"/> around it) is drawn there: the drawn things that lie in it (with <see cref="Reach"/> around it), other
    /// than the drawings found before (<paramref name="taken"/>), reach over <see cref="MinFill"/> of it across and down. A small logo in a corner of
    /// the place does not.
    /// </summary>
    public static bool Fills(IReadOnlyList<DrawnBox> boxes, double pageWidth, double pageHeight, ShapeArea area, double roomPoints, IReadOnlyList<ShapeArea>? taken = null)
    {
        // The room was cut off at the edge of the page (ShapeAreas.Of): it is not taken off that side again.
        const double Edge = 1e-6;
        var left = area.Left * pageWidth + (area.Left > Edge ? roomPoints : 0);
        var top = area.Top * pageHeight + (area.Top > Edge ? roomPoints : 0);
        var right = (area.Left + area.Width) * pageWidth - (area.Left + area.Width < 1 - Edge ? roomPoints : 0);
        var bottom = (area.Top + area.Height) * pageHeight - (area.Top + area.Height < 1 - Edge ? roomPoints : 0);
        if (right < left)
        {
            (left, right) = ((left + right) / 2, (left + right) / 2);
        }

        if (bottom < top)
        {
            (top, bottom) = ((top + bottom) / 2, (top + bottom) / 2);
        }

        var reachX = Math.Max(MinReachPoints, (right - left) * Reach);
        var reachY = Math.Max(MinReachPoints, (bottom - top) * Reach);
        double? foundLeft = null, foundTop = null, foundRight = null, foundBottom = null;
        foreach (var box in boxes)
        {
            var inside = box.Left >= left - reachX && box.Right <= right + reachX && box.Top >= top - reachY && box.Bottom <= bottom + reachY;
            var touches = box.Left <= right && box.Right >= left && box.Top <= bottom && box.Bottom >= top;
            if (!inside || !touches || InAny(box, taken, pageWidth, pageHeight))
            {
                continue;
            }

            foundLeft = Math.Min(foundLeft ?? box.Left, box.Left);
            foundTop = Math.Min(foundTop ?? box.Top, box.Top);
            foundRight = Math.Max(foundRight ?? box.Right, box.Right);
            foundBottom = Math.Max(foundBottom ?? box.Bottom, box.Bottom);
        }

        if (foundLeft is null || foundTop is null || foundRight is null || foundBottom is null)
        {
            return false;
        }

        // A side of a place that is a line (no size) is filled by anything on it.
        bool Over(double from, double to, double placeFrom, double placeTo) =>
            placeTo - placeFrom < 1 || Math.Min(to, placeTo) - Math.Max(from, placeFrom) >= MinFill * (placeTo - placeFrom);
        return Over(foundLeft.Value, foundRight.Value, left, right) && Over(foundTop.Value, foundBottom.Value, top, bottom);
    }

    /// <summary>
    /// Finds a drawing on a page (of <paramref name="pageWidth"/> by <paramref name="pageHeight"/> points) from its size (<paramref name="width"/> by
    /// <paramref name="height"/>, parts of the page) and, when it is known, its place across (<paramref name="left"/>; else anywhere between
    /// <paramref name="bandLeft"/> and <paramref name="bandRight"/>). The drawn things that fit across, and are not in <paramref name="taken"/> (the
    /// drawings found before), are taken in windows of the height of the drawing that start at the top of one of them. When <paramref name="expectedTop"/>
    /// is known, the drawing is the window of about its size (half of it each way at least) that starts nearest to it, within
    /// <see cref="ExpectedReachPoints"/> (or half the height of the drawing); when it
    /// is not, the windows that have at least half of the most that a window has must all be in one place, which is the drawing. When the place across is
    /// not known, what is found must not be much wider than the drawing (two drawings side by side are not one). Its area, with
    /// <paramref name="roomPoints"/> around it, or null when it is not found for sure.
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
        // And the size of what lies in it, across and down.
        var weights = new double[boxes.Count];
        var across = new double[boxes.Count];
        var down = new double[boxes.Count];
        for (var i = 0; i < boxes.Count; i++)
        {
            var end = boxes[i].Top + h + reachY;
            double windowLeft = double.MaxValue, windowRight = double.MinValue, windowBottom = boxes[i].Top;
            for (var j = i; j < boxes.Count && boxes[j].Top <= end; j++)
            {
                if (boxes[j].Bottom <= end)
                {
                    weights[i] += Weight(boxes[j]);
                    windowLeft = Math.Min(windowLeft, boxes[j].Left);
                    windowRight = Math.Max(windowRight, boxes[j].Right);
                    windowBottom = Math.Max(windowBottom, boxes[j].Bottom);
                }
            }

            across[i] = Math.Max(0, windowRight - windowLeft);
            down[i] = windowBottom - boxes[i].Top;
        }

        // A window of the drawing has about its size: at least half of it each way (a side of no size, a line, is any size).
        var w = width * pageWidth;
        bool OfTheSize(int i) => (h < 1 || down[i] >= h / 2) && (w < 1 || across[i] >= w / 2);

        var start = -1;
        if (expectedTop is { } expectedPart)
        {
            var y = expectedPart * pageHeight;
            var near = Math.Max(ExpectedReachPoints, h / 2);
            for (var i = 0; i < boxes.Count; i++)
            {
                if (weights[i] > 0 && OfTheSize(i) && Math.Abs(boxes[i].Top - y) <= near && (start < 0 || Math.Abs(boxes[i].Top - y) < Math.Abs(boxes[start].Top - y)))
                {
                    start = i;
                }
            }
        }
        else
        {
            var most = weights.Max();
            for (var i = 0; i < boxes.Count; i++)
            {
                if (weights[i] >= most / 2)
                {
                    if (start < 0)
                    {
                        start = i;
                    }
                    else if (boxes[i].Top > boxes[start].Top + h)
                    {
                        return null; // drawings of the size in two places: which one it is, is not known
                    }
                }
            }
        }

        if (start < 0)
        {
            return null;
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
        else if (foundRight - foundLeft > width * pageWidth + 2 * reachX)
        {
            return null; // more than the drawing across: drawings side by side, and which one it is, is not known
        }

        return ShapeAreas.Of(foundLeft, top, foundRight - foundLeft, foundBottom - top, 0, roomPoints, pageWidth, pageHeight);
    }
}

/// <summary>A drawn thing of a PDF page (<see cref="PdfShapeSnap.Drawn"/>), in points from the top left of the page.</summary>
public readonly record struct DrawnBox(double Left, double Top, double Right, double Bottom);
