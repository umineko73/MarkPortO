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

using MarkPortO.Core.Pdf;
using MarkPortO.Core.Tests.Fixtures;
using UglyToad.PdfPig.Content;

namespace MarkPortO.Core.Tests.Pdf;

public class PdfRenderingTests
{
    [Fact]
    public async Task GetPageCount_ReturnsTheNumberOfPages()
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("a.pdf"), PageSize.A4, "one", "two", "three");

        Assert.Equal(3, await PdfRenderer.GetPageCountAsync(pdf));
    }

    [Fact]
    public async Task RenderPage_WritesAPngWithTheSizeOfTheDpi()
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("a.pdf"), PageSize.A4, "hello");
        var document = await PdfRenderer.OpenAsync(pdf);

        var image = await PdfRenderer.RenderPageAsync(document, 0, 96, dir.Combine("p96.png"));

        // A4 is 8.27 x 11.69 inches.
        Assert.InRange(image.Width, 790, 800);
        Assert.InRange(image.Height, 1118, 1126);
        Assert.Equal((image.Width, image.Height), PdfFactory.ReadPngSize(image.Path));
    }

    [Fact]
    public async Task RenderPage_HigherDpiGivesProportionallyMorePixels()
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("a.pdf"), PageSize.A4, "hello");
        var document = await PdfRenderer.OpenAsync(pdf);

        var low = await PdfRenderer.RenderPageAsync(document, 0, 72, dir.Combine("p72.png"));
        var high = await PdfRenderer.RenderPageAsync(document, 0, 144, dir.Combine("p144.png"));
        var higher = await PdfRenderer.RenderPageAsync(document, 0, 300, dir.Combine("p300.png"));

        Assert.InRange((double)high.Width / low.Width, 1.99, 2.01);
        Assert.InRange((double)high.Height / low.Height, 1.99, 2.01);
        Assert.True(higher.Width * (long)higher.Height > high.Width * (long)high.Height);
        Assert.Equal((higher.Width, higher.Height), PdfFactory.ReadPngSize(higher.Path)); // SC-016
    }

    [Fact]
    public async Task RenderPage_WhenTheImageWouldExceedTheLimit_FailsWithoutWritingAFile()
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("a.pdf"), PageSize.A3, "big");
        var document = await PdfRenderer.OpenAsync(pdf);
        var target = dir.Combine("big.png");

        var error = await Assert.ThrowsAsync<PageTooLargeException>(() =>
            PdfRenderer.RenderPageAsync(document, 0, 600, target, maxPixels: 10_000_000));

        Assert.True(error.Pixels > error.MaxPixels);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task RenderPage_HonoursCancellation()
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("a.pdf"), PageSize.A4, "x");
        var document = await PdfRenderer.OpenAsync(pdf);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PdfRenderer.RenderPageAsync(document, 0, 96, dir.Combine("x.png"), cancellationToken: cts.Token));
    }

    [Fact]
    public void PdfTextExtractor_ReturnsTheTextOfEachPage()
    {
        using var dir = new TempDir();
        var pdf = PdfFactory.Create(dir.Combine("a.pdf"), PageSize.A4, "first page text", "second page text");

        var texts = PdfTextExtractor.GetPageTexts(pdf);

        Assert.Equal(2, texts.Count);
        Assert.Contains("first page text", texts[0]);
        Assert.Contains("second page text", texts[1]);
        Assert.DoesNotContain("second", texts[0]);
    }
}
