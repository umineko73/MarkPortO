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

namespace MarkPortO.Core.Vlm;

/// <summary>Cuts a band out of a page image (FR-041), and makes an image smaller before it is sent (FR-064).</summary>
public static class BandImage
{
    /// <summary>
    /// The PNG, scaled down so that its longer side is at most <paramref name="maxSide"/> pixels. An image that is already
    /// small enough, or a <paramref name="maxSide"/> of 0, is returned as it is. The image is never made larger.
    /// </summary>
    public static byte[] Shrink(byte[] png, int maxSide)
    {
        if (maxSide <= 0)
        {
            return png;
        }

        using var source = new Bitmap(new MemoryStream(png));
        var longer = Math.Max(source.Width, source.Height);
        if (longer <= maxSide)
        {
            return png;
        }

        var scale = (double)maxSide / longer;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var small = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        using var stream = new MemoryStream();
        small.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>The PNG of the strip of the page that the band covers. The width is the whole width of the page.</summary>
    public static byte[] Crop(byte[] png, Band band)
    {
        using var source = new Bitmap(new MemoryStream(png));
        var top = Math.Clamp((int)Math.Floor(band.Top * source.Height), 0, source.Height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(band.Bottom * source.Height), top + 1, source.Height);

        using var cut = source.Clone(new Rectangle(0, top, source.Width, bottom - top), source.PixelFormat);
        using var stream = new MemoryStream();
        cut.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
