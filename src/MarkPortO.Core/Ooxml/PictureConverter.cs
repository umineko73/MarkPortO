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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;

namespace MarkPortO.Core.Ooxml;

/// <summary>
/// Turns a picture that a browser does not show into a PNG (FR-134): an EMF or a WMF (also compressed: EMZ, WMZ) is drawn with GDI+ at the
/// size that it says, on white; a BMP or a TIFF is written again as a PNG.
/// </summary>
public static class PictureConverter
{
    /// <summary>The longest side of a drawn metafile, in pixels.</summary>
    public const int MaxSide = 4000;

    private const double HundredthsOfAMillimetrePerInch = 2540;

    /// <summary>The content types that are turned into a PNG.</summary>
    public static bool CanConvert(string contentType) => KindOf(contentType) is not null;

    /// <summary>The PNG of the picture, drawn at <paramref name="dpi"/> when it is a metafile; throws when the picture cannot be read.</summary>
    public static byte[] ToPng(byte[] data, string contentType, int dpi)
    {
        var kind = KindOf(contentType) ?? throw new NotSupportedException(contentType);
        if (kind is "emz" or "wmz" && data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            // Compressed with gzip; a file that only has the name of a compressed one is drawn as it is.
            using var zipped = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            zipped.CopyTo(plain);
            data = plain.ToArray();
        }

        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        if (kind is "bitmap")
        {
            using var image = Image.FromStream(input);
            image.Save(output, ImageFormat.Png);
            return output.ToArray();
        }

        using var metafile = new Metafile(input);

        // The physical size of a metafile is in hundredths of a millimetre.
        var size = metafile.PhysicalDimension;
        var width = size.Width / HundredthsOfAMillimetrePerInch * dpi;
        var height = size.Height / HundredthsOfAMillimetrePerInch * dpi;
        if (width < 1 || height < 1)
        {
            throw new InvalidDataException("The metafile has no size.");
        }

        var scale = Math.Min(1.0, MaxSide / Math.Max(width, height));
        var pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(height * scale));
        using var bitmap = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White); // a drawing on a clear background has lines that a dark view does not show
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(metafile, new Rectangle(0, 0, pixelWidth, pixelHeight));
        }

        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static string? KindOf(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/x-emf" or "image/emf" or "image/x-wmf" or "image/wmf" => "metafile",
        "image/x-emz" => "emz",
        "image/x-wmz" => "wmz",
        "image/bmp" or "image/x-bmp" or "image/tiff" or "image/x-tiff" => "bitmap",
        _ => null,
    };
}
