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

using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using MarkPort.Core.IO;

namespace MarkPort.Core.Pdf;

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

        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)width,
            DestinationHeight = (uint)height,
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream))
        {
            await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken).ConfigureAwait(false);
            reader.ReadBytes(bytes);
        }

        AtomicFile.WriteAllBytes(outputPngPath, bytes);
        return new RenderedImage(outputPngPath, width, height);
    }

    private static (int Width, int Height) ToPixels(double widthDevice, double heightDevice, int dpi)
    {
        var scale = dpi / DeviceUnitsPerInch;
        return (Math.Max(1, (int)Math.Round(widthDevice * scale)), Math.Max(1, (int)Math.Round(heightDevice * scale)));
    }
}
