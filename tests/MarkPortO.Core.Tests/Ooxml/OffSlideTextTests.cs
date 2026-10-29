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

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>FR-086: the text of a shape that lies outside the slide (a memo on the pasteboard).</summary>
public class OffSlideTextTests
{
    // The slide of the factory is 9144000 x 6858000 EMU.
    private static string Deck(TempDir dir)
    {
        var path = dir.Combine("memo.pptx");
        PptxFactory.Create(path, new SlideSpec("Title", ["body text"]));
        using var document = PresentationDocument.Open(path, isEditable: true);
        var tree = document.PresentationPart!.SlideParts.First().Slide!.CommonSlideData!.ShapeTree!;
        tree.AppendChild(TextShape(90, "memo right of the slide", x: 10_000_000, y: 100_000));
        tree.AppendChild(TextShape(91, "memo below the slide", x: 100_000, y: 7_500_000));
        tree.AppendChild(TextShape(92, "memo half outside", x: 8_500_000, y: 100_000));
        document.PresentationPart.SlideParts.First().Slide!.Save();
        return path;
    }

    private static Shape TextShape(int id, string text, long x, long y) => new(
        "<p:sp xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">"
        + $"<p:nvSpPr><p:cNvPr id=\"{id}\" name=\"memo{id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>"
        + $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"2000000\" cy=\"500000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>"
        + $"<p:txBody><a:bodyPr/><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>");

    [Fact]
    public void Read_ByDefault_WritesTheTextOutsideTheSlideAsAQuoteOfItsOwn()
    {
        using var dir = new TempDir();

        var markdown = PptxReader.Read(Deck(dir), includeHidden: false).Single().Markdown;

        Assert.Contains("> **Off-slide**: memo right of the slide", markdown);
        Assert.Contains("> **Off-slide**: memo below the slide", markdown);
        Assert.Contains("body text", markdown);
    }

    [Fact]
    public void Read_AShapeThatIsPartlyOnTheSlide_IsPartOfTheSlide()
    {
        using var dir = new TempDir();

        var markdown = PptxReader.Read(Deck(dir), includeHidden: false).Single().Markdown;

        Assert.Contains("memo half outside", markdown);
        Assert.DoesNotContain("**Off-slide**: memo half outside", markdown);
    }

    [Fact]
    public void Read_WhenTheTextOutsideTheSlideIsOff_LeavesItOut()
    {
        using var dir = new TempDir();

        var markdown = PptxReader.Read(Deck(dir), includeHidden: false, includeOffSlideText: false).Single().Markdown;

        Assert.DoesNotContain("memo right of the slide", markdown);
        Assert.DoesNotContain("memo below the slide", markdown);
        Assert.Contains("memo half outside", markdown);
        Assert.Contains("body text", markdown);
    }
}
