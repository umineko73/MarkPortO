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
using DocumentFormat.OpenXml.Packaging;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Tests.Fixtures;
using P = DocumentFormat.OpenXml.Presentation;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>The drawn shapes of a pptx or xlsx are cut out of the page image (FR-136).</summary>
public class ShapeCutTests
{
    [Fact]
    public void SheetShape_EndBeyondUsedCells_KeepsItsFullSize()
    {
        using var dir = new TempDir();
        var path = dir.Combine("edge.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["a"]], Shapes: [new CellShapeSpec("rect", 0, 0, 4, 5)]));
        var sheet = Assert.Single(XlsxReader.Read(path, false, pictures: new PictureCollector(cutShapes: true)));
        var shape = Assert.Single(sheet.Shapes);
        Assert.Equal(4 * sheet.ShapeColumnWidthsPoints[0], shape.Width, 6);
        Assert.Equal(5 * sheet.RowHeightsPoints[0], shape.Height, 6);
        Assert.Single(sheet.Rows); // the drawing dimensions do not extend the text's used range
    }

    [Fact]
    public void SheetPicturesAndShapes_AreInsertedInCellOrder()
    {
        var sheet = new SheetContent
        {
            Name = "Data", FirstRow = 1, LastRow = 5, FirstColumn = 1, LastColumn = 1,
            Rows = [["start"], [""], ["middle"], [""], ["end"]], ColumnWidthsPoints = [48],
            Pictures = [new SheetPicture(3, 0, "![picture](late.png)"), new SheetPicture(1, 0, "![shape](early.png)")],
        };
        var parts = sheet.ToMarkdownParts(0, 0, null).Select(p => p.Markdown).ToList();
        Assert.True(parts.IndexOf("![shape](early.png)") < parts.IndexOf("middle"));
        Assert.True(parts.IndexOf("middle") < parts.IndexOf("![picture](late.png)"));
        Assert.True(parts.IndexOf("![picture](late.png)") < parts.IndexOf("end"));
    }

    private const string SlideNamespaces =
        "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";

    private const long Inch = 914400;

    /// <summary>A red rectangle of a slide, with a text or without.</summary>
    public static P.Shape SlideRectangle(uint id, long x, long y, long width, long height, string? text = null, bool textBox = false) =>
        new($"<p:sp {SlideNamespaces}><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Shape {id}\"/><p:cNvSpPr{(textBox ? " txBox=\"1\"" : string.Empty)}/><p:nvPr/></p:nvSpPr>"
            + $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>"
            + (textBox ? "<a:noFill/>" : "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>") + "</p:spPr>"
            + (text is null ? string.Empty : $"<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{text}</a:t></a:r></a:p></p:txBody>")
            + "</p:sp>");

    private static P.GroupShape SlideGroup(uint id, long x, long y, long width, long height, params P.Shape[] shapes)
    {
        var group = new P.GroupShape(
            $"<p:grpSp {SlideNamespaces}><p:nvGrpSpPr><p:cNvPr id=\"{id}\" name=\"Group {id}\" descr=\"Flow\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
            + $"<p:grpSpPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{width}\" cy=\"{height}\"/><a:chOff x=\"{x}\" y=\"{y}\"/><a:chExt cx=\"{width}\" cy=\"{height}\"/></a:xfrm></p:grpSpPr></p:grpSp>");
        foreach (var shape in shapes)
        {
            group.AppendChild(shape);
        }

        return group;
    }

    /// <summary>Adds shapes to the first slide of a presentation made by <see cref="PptxFactory"/>.</summary>
    internal static void AddToSlide(string path, params DocumentFormat.OpenXml.OpenXmlElement[] shapes)
    {
        using var document = PresentationDocument.Open(path, isEditable: true);
        var slide = document.PresentationPart!.SlideParts.First().Slide!;
        foreach (var shape in shapes)
        {
            slide.CommonSlideData!.ShapeTree!.AppendChild(shape);
        }

        slide.Save();
    }

    [Fact]
    public void Pptx_AGroupAndALargeShapeAreCut_ATextBoxAndASmallShapeAreNot()
    {
        using var dir = new TempDir();
        var path = dir.Combine("shapes.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        AddToSlide(path,
            SlideRectangle(20, Inch, Inch, 2 * Inch, Inch, "Start"),
            SlideRectangle(21, 4 * Inch, Inch, 2 * Inch, Inch / 4), // a quarter of an inch tall: too small
            SlideRectangle(22, Inch, 3 * Inch, 3 * Inch, Inch, "a note", textBox: true),
            SlideGroup(23, 5 * Inch, 3 * Inch, 3 * Inch, 2 * Inch, SlideRectangle(24, 5 * Inch, 3 * Inch, Inch, Inch, "inside"), SlideRectangle(25, 7 * Inch, 4 * Inch, Inch, Inch)));

        var collector = new PictureCollector(savePictures: false, cutShapes: true);
        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false, pictures: collector));

        // The large shape before its text, the group (with its text) before the texts of its shapes; nothing else.
        Assert.Equal(2, collector.Pictures.Count);
        Assert.All(collector.Pictures, p => Assert.True(p.IsShape));
        Assert.Matches("!\\[\\]\\(0\\)\n\nStart", slide.Markdown);
        Assert.Matches("!\\[Flow\\]\\(1\\)\n\ninside", slide.Markdown);
        Assert.Contains("a note", slide.Markdown, StringComparison.Ordinal);

        // The place is that of the slide (10 x 7.5 inches by default), with 4 points of room.
        var area = collector.Pictures[0].Area!;
        var room = 4.0 / 72;
        Assert.Equal((1 - room) / 10, area.Left, 4);
        Assert.Equal((1 - room) / 7.5, area.Top, 4);
        Assert.Equal((2 + 2 * room) / 10, area.Width, 4);
        Assert.Null(area.Page);
    }

    [Fact]
    public void Pptx_WithoutCuttingShapes_NothingChanges()
    {
        using var dir = new TempDir();
        var path = dir.Combine("shapes.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        AddToSlide(path, SlideRectangle(20, Inch, Inch, 2 * Inch, Inch, "Start"));

        var collector = new PictureCollector(savePictures: true, cutShapes: false);
        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false, pictures: collector));

        Assert.Empty(collector.Pictures);
        Assert.DoesNotContain("![", slide.Markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 10, 30, 30, 20)]
    [InlineData(90, 0, 30, 50, 20)] // a 30 x 20 box turned a quarter is 20 x 30 about the same centre (25, 40)
    public void TheAreaOfATurnedShape_HoldsTheTurnedBox(double degrees, double left, double top, double width, double height)
    {
        var area = ShapeAreas.Of(10, 30, 30, 20, degrees, 0, 100, 100)!;

        var expected = degrees == 0 ? (left, top, width, height) : (15.0, 25.0, 20.0, 30.0);
        Assert.Equal(expected.Item1 / 100, area.Left, 6);
        Assert.Equal(expected.Item2 / 100, area.Top, 6);
        Assert.Equal(expected.Item3 / 100, area.Width, 6);
        Assert.Equal(expected.Item4 / 100, area.Height, 6);
    }

    [Fact]
    public void TheArea_IsKeptOnThePage_AndAShapeOffThePageHasNone()
    {
        var area = ShapeAreas.Of(90, 90, 30, 30, 0, 5, 100, 100)!;
        Assert.Equal(0.85, area.Left, 6);
        Assert.Equal(0.15, area.Width, 6);
        Assert.Null(ShapeAreas.Of(120, 10, 10, 10, 0, 0, 100, 100));
    }

    [Theory]
    [InlineData(500, 300, false, 1.0)] // fits: not enlarged
    [InlineData(2309.1, 300, false, 0.5)] // twice the printable width
    [InlineData(500, 1611.7, true, 0.5)] // the whole sheet on one page: (just under) twice the printable height of 805.89 points
    [InlineData(500, 1611.8, true, 0.49)] // just over: Excel takes the percent below
    public void TheScaleOfAPartOfASheet_IsAWholePercent(double width, double height, bool fitHeight, double expected)
    {
        Assert.Equal(expected, ShapeAreas.SheetScale(width, height, ShapeAreas.A3LongPoints, ShapeAreas.A3ShortPoints, fitHeight), 6);
    }

    // The widths of the columns count in digits of the default font: a Japanese font of 11 points is 8 pixels wide, Calibri 7.
    [Theory]
    [InlineData("游ゴシック", 11, 8)]
    [InlineData("ＭＳ Ｐゴシック", 11, 8)]
    [InlineData("Meiryo UI", 11, 8)]
    [InlineData("Calibri", 11, 7)]
    [InlineData("Arial", 22, 14)]
    public void TheDigitWidth_IsThatOfTheDefaultFont(string font, double size, double expected)
    {
        using var dir = new TempDir();
        var path = dir.Combine("font.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data", [["a"]]));
        using (var document = SpreadsheetDocument.Open(path, isEditable: true))
        {
            var styles = document.WorkbookPart!.WorkbookStylesPart ?? document.WorkbookPart.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet ??= new DocumentFormat.OpenXml.Spreadsheet.Stylesheet();
            styles.Stylesheet.Fonts = new DocumentFormat.OpenXml.Spreadsheet.Fonts(new DocumentFormat.OpenXml.Spreadsheet.Font(
                new DocumentFormat.OpenXml.Spreadsheet.FontSize { Val = size }, new DocumentFormat.OpenXml.Spreadsheet.FontName { Val = font }));
            styles.Stylesheet.Save();
        }

        using var read = SpreadsheetDocument.Open(path, isEditable: false);
        Assert.Equal(expected, XlsxReader.DigitWidthOf(read.WorkbookPart!));
    }

    [Fact]
    public void AShapeIsCutOutOfTheImageOfItsPage_AndOneWithoutAPlaceIsItsTextOnly()
    {
        using var dir = new TempDir();
        var image = dir.Combine("page.png");
        using (var page = new Bitmap(200, 100))
        using (var graphics = Graphics.FromImage(page))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Red, 50, 20, 100, 60);
            page.Save(image, System.Drawing.Imaging.ImageFormat.Png);
        }

        var collector = new PictureCollector(savePictures: false, cutShapes: true);
        var cut = collector.AddShape(new ShapeArea(0.25, 0.2, 0.5, 0.6));
        var lost = collector.AddShape(null);
        var blocks = new List<Block> { new() { Kind = BlockKind.SlideText, Markdown = $"![Flow]({cut})\n\n![Lost]({lost})\n\n![]({lost})", PageIndex = 1 } };

        var log = PictureFiles.Write(dir.Path, blocks, 1, collector, page => page == 1 ? image : null);

        Assert.Equal("![Flow](pictures/page-001-1.png)\n\n![Lost]\n\n", blocks[0].Markdown);
        using var piece = new Bitmap(dir.Combine("pictures", "page-001-1.png"));
        Assert.Equal((100, 60), (piece.Width, piece.Height));
        Assert.Equal(Color.Red.ToArgb(), piece.GetPixel(50, 30).ToArgb());
        Assert.Contains(log, l => l.Contains("not cut out", StringComparison.Ordinal));
    }
}
