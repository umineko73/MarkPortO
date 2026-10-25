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

using MarkPort.Core.Ooxml;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.Ooxml;

public class PptxReaderTests
{
    [Fact]
    public void Read_SlideWithTitleAndBody_ProducesAHeadingAndBulletItems()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan", ["first point", "second point"]));

        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false));

        Assert.Equal(1, slide.Number);
        Assert.Equal("Plan", slide.Title);
        Assert.Equal("## Plan\n\n- first point\n- second point", slide.Markdown);
    }

    [Fact]
    public void Read_Table_BecomesAPipeTable()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(path, new SlideSpec("Numbers", Table: [["A", "B"], ["1", "2"]]));

        var slide = PptxReader.Read(path, false).Single();

        Assert.Contains("| A | B |\n|---|---|\n| 1 | 2 |", slide.Markdown);
    }

    [Fact]
    public void Read_HiddenSlides_AreSkippedUnlessRequested_AndKeepTheirNumbers()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(
            path,
            new SlideSpec("One"),
            new SlideSpec("Two (hidden)", Hidden: true),
            new SlideSpec("Three"));

        var visible = PptxReader.Read(path, includeHidden: false);
        Assert.Equal([1, 3], visible.Select(s => s.Number));

        var all = PptxReader.Read(path, includeHidden: true);
        Assert.Equal([1, 2, 3], all.Select(s => s.Number));
        Assert.Equal([false, true, false], all.Select(s => s.Hidden));
        Assert.Equal([false, true, false], PptxReader.ReadHiddenFlags(path));
    }

    [Fact]
    public void Read_PresentationWithoutSlides_ReturnsNothing()
    {
        using var dir = new TempDir();
        var path = dir.Combine("empty.pptx");
        PptxFactory.Create(path);

        Assert.Empty(PptxReader.Read(path, includeHidden: true));
    }

    [Fact]
    public void Read_NeutralisesMarkPortMarkers()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(path, new SlideSpec("A <!-- page: 3 --> B"));

        var slide = PptxReader.Read(path, false).Single();

        Assert.DoesNotContain("<!--", slide.Markdown);
    }

    [Fact]
    public void Read_CorruptFile_ThrowsInvalidDocumentException()
    {
        using var dir = new TempDir();
        var path = dir.Combine("broken.pptx");
        File.WriteAllText(path, "not a presentation");

        Assert.Throws<InvalidDocumentException>(() => PptxReader.Read(path, false));
    }
}
