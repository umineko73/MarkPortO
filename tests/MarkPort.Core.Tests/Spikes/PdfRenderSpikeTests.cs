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

using System.Diagnostics;
using MarkPort.Core.Pdf;
using MarkPort.Core.Tests.Fixtures;
using UglyToad.PdfPig.Content;
using Xunit.Abstractions;

namespace MarkPort.Core.Tests.Spikes;

/// <summary>
/// Spike S3 (research.md): how large can a page image get with the Windows PDF renderer, and what does it cost?
/// The measured values are printed so they can be recorded in research.md.
/// </summary>
public class PdfRenderSpikeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(72)]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(600)]
    public async Task A3Page_RendersAtEveryDpiWithinTheDefaultLimit(int dpi)
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("a3.pdf"), PageSize.A3, "A3 page for the render spike");
        var document = await PdfRenderer.OpenAsync(pdf);

        var process = Process.GetCurrentProcess();
        GC.Collect();
        var beforeWorkingSet = process.WorkingSet64;
        var watch = Stopwatch.StartNew();

        var image = await PdfRenderer.RenderPageAsync(document, 0, dpi, dir.Combine($"a3-{dpi}.png"));

        watch.Stop();
        process.Refresh();
        var fileSize = new FileInfo(image.Path).Length;
        output.WriteLine(
            $"A3 @ {dpi} DPI: {image.Width}x{image.Height} px ({(long)image.Width * image.Height:N0} px), " +
            $"{watch.ElapsedMilliseconds} ms, PNG {fileSize / 1024.0:N0} KiB, " +
            $"working set +{(process.WorkingSet64 - beforeWorkingSet) / (1024.0 * 1024):N0} MiB");

        Assert.Equal((image.Width, image.Height), PdfFactory.ReadPngSize(image.Path));
        Assert.True((long)image.Width * image.Height <= PdfRenderer.DefaultMaxPixels);
    }

    [Fact]
    public async Task VeryLargePage_IsRefusedByTheLimitInsteadOfExhaustingMemory()
    {
        using var dir = new TempDir();
        // A0 is 33.1 x 46.8 inches: at 600 DPI it is about 553 million pixels.
        var pdf = PdfFactory.Create(dir.Combine("a0.pdf"), PageSize.A0, "huge");
        var document = await PdfRenderer.OpenAsync(pdf);

        var (width, height) = PdfRenderer.GetPixelSize(document, 0, 600);
        output.WriteLine($"A0 @ 600 DPI would be {width}x{height} px ({(long)width * height:N0} px)");

        await Assert.ThrowsAsync<PageTooLargeException>(() =>
            PdfRenderer.RenderPageAsync(document, 0, 600, dir.Combine("a0.png")));
    }
}
