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

using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using MarkPortO.Core.IO;

namespace MarkPortO.Core.Pdf;

/// <summary>A page was rendered, or could not be because it would be too large.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record RenderedImage(string Path, int Width, int Height);

/// <summary>The page would need more pixels than allowed at the chosen DPI.</summary>
public sealed class PageTooLargeException(long pixels, long maxPixels)
    : Exception($"The page image would be {pixels:N0} pixels; the limit is {maxPixels:N0}. Lower the DPI.")
{
    public long Pixels { get; } = pixels;

    public long MaxPixels { get; } = maxPixels;
}

/// <summary>Renders the pages of a PDF to PNG files with the Windows PDF renderer (research R1).</summary>
public static class PdfRenderer
{
    /// <summary>Upper limit of pixels per page image (width x height).</summary>
    public const long DefaultMaxPixels = 100_000_000;

    /// <summary>The Windows renderer measures a page in 1/96 inch units.</summary>
    private const double DeviceUnitsPerInch = 96.0;

    public static async Task<PdfDocument> OpenAsync(string pdfPath)
    {
        var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(pdfPath)).AsTask().ConfigureAwait(false);
        return await PdfDocument.LoadFromFileAsync(file).AsTask().ConfigureAwait(false);
    }

    public static async Task<int> GetPageCountAsync(string pdfPath)
    {
        var document = await OpenAsync(pdfPath).ConfigureAwait(false);
        return (int)document.PageCount;
    }

    /// <summary>Size in pixels that page <paramref name="pageIndex"/> (0-based) has at <paramref name="dpi"/>.</summary>
    public static (int Width, int Height) GetPixelSize(PdfDocument document, int pageIndex, int dpi)
    {
        using var page = document.GetPage((uint)pageIndex);
        return ToPixels(page.Size.Width, page.Size.Height, dpi);
    }

    /// <summary>
    /// Renders one page (0-based <paramref name="pageIndex"/>) to a PNG file, written atomically.
    /// </summary>
    /// <exception cref="PageTooLargeException">The image would be larger than <paramref name="maxPixels"/>.</exception>
    public static async Task<RenderedImage> RenderPageAsync(
        PdfDocument document,
        int pageIndex,
        int dpi,
        string outputPngPath,
        long maxPixels = DefaultMaxPixels,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var page = document.GetPage((uint)pageIndex);
        var (width, height) = ToPixels(page.Size.Width, page.Size.Height, dpi);
        var pixels = (long)width * height;
        if (pixels > maxPixels)
        {
            throw new PageTooLargeException(pixels, maxPixels);
        }

        // The Windows renderer multiplies the size it is given by the display scale of the PC (125% makes the image 1.25 times
        // larger), so the size is divided by the scale first. The scale is measured on the first image and kept.
        var (bytes, requested) = await RenderAsync(page, width, height, cancellationToken).ConfigureAwait(false);
        var (actualWidth, actualHeight) = PngSize(bytes);
        var measured = requested > 0 ? actualWidth / (double)requested : 1.0;
        if (Math.Abs(measured - _renderScale) > 0.02 && measured > 0)
        {
            _renderScale = measured;
            (bytes, _) = await RenderAsync(page, width, height, cancellationToken).ConfigureAwait(false);
            (actualWidth, actualHeight) = PngSize(bytes);
        }

        // The size is exactly the one that the DPI gives (the scale and the rounding can leave a pixel or two).
        if (actualWidth != width || actualHeight != height)
        {
            bytes = Resize(bytes, width, height);
            (actualWidth, actualHeight) = (width, height);
        }

        AtomicFile.WriteAllBytes(outputPngPath, bytes);
        return new RenderedImage(outputPngPath, actualWidth, actualHeight);
    }

    // What the renderer makes of a size: 1.0 on a display at 100%, 1.25 at 125%.
    private static double _renderScale = 1.0;

    private static async Task<(byte[] Bytes, int RequestedWidth)> RenderAsync(PdfPage page, int width, int height, CancellationToken cancellationToken)
    {
        var scale = _renderScale;
        var requestedWidth = (uint)Math.Max(1, Math.Round(width / scale));
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = requestedWidth,
            DestinationHeight = (uint)Math.Max(1, Math.Round(height / scale)),
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken).ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return (bytes, (int)requestedWidth);
    }

    private static byte[] Resize(byte[] png, int width, int height)
    {
        using var input = new System.IO.MemoryStream(png);
        using var source = new System.Drawing.Bitmap(input);
        using var target = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(target))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        using var output = new System.IO.MemoryStream();
        target.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return output.ToArray();
    }

    // Width and height from the IHDR chunk of a PNG.
    private static (int Width, int Height) PngSize(byte[] bytes) =>
        bytes.Length < 24 ? (0, 0) : (ReadInt(bytes, 16), ReadInt(bytes, 20));

    private static int ReadInt(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static (int Width, int Height) ToPixels(double widthDevice, double heightDevice, int dpi)
    {
        var scale = dpi / DeviceUnitsPerInch;
        return (Math.Max(1, (int)Math.Round(widthDevice * scale)), Math.Max(1, (int)Math.Round(heightDevice * scale)));
    }
}
