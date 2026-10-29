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

using System.Drawing;
using System.Drawing.Imaging;

namespace MarkPortO.Core.Pdf;

/// <summary>Tells whether the image of a page has nothing on it, so that the VLM is not asked to read it (FR-082).</summary>
public static class PageBlank
{
    /// <summary>The top and bottom of the page (a header, a footer, a page number) are not looked at.</summary>
    public const double MarginFraction = 0.05;

    /// <summary>A pixel is "ink" when one of its colours is darker than this (255 is white; a rendered empty page is exactly white).</summary>
    public const int InkLevel = 250;

    /// <summary>A page with fewer ink pixels than this is blank. It is small: a page with one digit on it is not a blank page.</summary>
    public const int MinimumInkPixels = 4;

    /// <param name="pngPath">The image of the page.</param>
    /// <param name="ignoreHeaderAndFooter">Leave out the top and bottom of the page (a Word page has a header and a footer; a sheet or a slide does not, and its content may start at the edge).</param>
    public static bool IsBlank(string pngPath, bool ignoreHeaderAndFooter = true)
    {
        using var bitmap = new Bitmap(pngPath);
        return IsBlank(bitmap, ignoreHeaderAndFooter);
    }

    public static bool IsBlank(Bitmap bitmap, bool ignoreHeaderAndFooter = true)
    {
        var margin = ignoreHeaderAndFooter ? (int)(bitmap.Height * MarginFraction) : 0;
        var area = new Rectangle(0, margin, bitmap.Width, Math.Max(1, bitmap.Height - (2 * margin)));
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Stride];
            var ink = 0;
            for (var y = 0; y < data.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, data.Stride);
                for (var x = 0; x < data.Width; x++)
                {
                    // BGRA. A transparent pixel is white paper.
                    var alpha = row[(x * 4) + 3];
                    if (alpha < 16)
                    {
                        continue;
                    }

                    var darkest = Math.Min(row[x * 4], Math.Min(row[(x * 4) + 1], row[(x * 4) + 2]));
                    var onWhite = ((darkest * alpha) + (255 * (255 - alpha))) / 255;
                    if (onWhite < InkLevel && ++ink >= MinimumInkPixels)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
