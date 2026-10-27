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
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Vlm;

public class BandImageTests
{
    private static byte[] Png(int width, int height, Func<int, Color> rowColor)
    {
        using var bitmap = new Bitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, rowColor(y));
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Bitmap Load(byte[] png) => new(new MemoryStream(png));

    [Fact]
    public void AnImage_IsScaledDownByItsLongerSide_KeepingTheRatio()
    {
        var page = Png(400, 800, _ => Color.White);

        using var small = Load(BandImage.Shrink(page, 200));

        Assert.Equal(100, small.Width);
        Assert.Equal(200, small.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(800)]
    [InlineData(5000)]
    public void AnImage_IsNotChanged_WhenItIsSmallEnoughOrTheLimitIsOff(int maxSide)
    {
        var page = Png(400, 800, _ => Color.White);

        Assert.Same(page, BandImage.Shrink(page, maxSide));
    }

    [Fact]
    public void ABand_IsCutOutOfThePageByItsFractions()
    {
        var page = Png(100, 200, y => y < 100 ? Color.Red : Color.Blue);

        using var cut = Load(BandImage.Crop(page, new Band(0.25, 0.75, 0, 0)));

        Assert.Equal(100, cut.Width);
        Assert.Equal(100, cut.Height);
        Assert.Equal(Color.Red.ToArgb(), cut.GetPixel(10, 10).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), cut.GetPixel(10, 90).ToArgb());
    }

    [Fact]
    public void TheWholePage_IsReturnedWhenTheBandCoversIt()
    {
        var page = Png(40, 80, _ => Color.White);

        using var cut = Load(BandImage.Crop(page, new Band(0, 1, 0, 0)));

        Assert.Equal(80, cut.Height);
    }

    [Fact]
    public void ABandIsNeverEmpty_EvenWhenTiny()
    {
        var page = Png(40, 80, _ => Color.White);

        using var cut = Load(BandImage.Crop(page, new Band(0.5, 0.5, 0, 0)));

        Assert.True(cut.Height >= 1);
    }
}
