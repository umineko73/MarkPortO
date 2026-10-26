// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.Drawing;
using System.Drawing.Imaging;

namespace MarkPort.Core.Vlm;

/// <summary>Cuts a band out of a page image (FR-041).</summary>
public static class BandImage
{
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
