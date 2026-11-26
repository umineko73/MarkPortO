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
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;
using P = DocumentFormat.OpenXml.Presentation;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>The drawn shapes of a Word, PowerPoint, or Excel file are cut out of the page image (FR-136).</summary>
public class ShapeCutTests
{
    [LibreOfficeFact]
    public async Task Docx_DiagramShapes_AreCutOutTogetherAsOnePicture()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "diagram.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["diagram text"], [new DocShape("rect", 0.2, 0.2), new DocShape("roundRect", 0.2, 0.2)])]);
        var options = Options();
        options.ExtractShapes = true;
        options.GroupNearShapes = true;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));
        var output = Assert.Single(result.Documents).OutputFolder;
        var pictures = Directory.GetFiles(Path.Combine(output, PictureFiles.Folder), "*.png");
        var markdown = File.ReadAllText(Path.Combine(output, "diagram.md"));

        Assert.Single(pictures);
        Assert.Contains("pictures/", markdown, StringComparison.Ordinal);
    }

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
            SlideRectangle(21, 4 * Inch, 6 * Inch + Inch / 2, 2 * Inch, Inch / 4), // a quarter of an inch tall: too small (on a row of its own)
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
    public void Pptx_ShapesNearOneAnother_AreCutOutAsOnePicture_ShapesFarApartAreNot()
    {
        using var dir = new TempDir();
        var path = dir.Combine("cluster.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        AddToSlide(path,
            SlideRectangle(20, Inch, Inch, 2 * Inch, Inch, "Start"),
            SlideRectangle(21, 3 * Inch + Inch / 8, Inch + Inch / 4, 3 * Inch / 4, Inch / 4), // a small arrow between two boxes: too small alone, near both
            SlideRectangle(22, 4 * Inch, Inch, 2 * Inch, Inch, "End"),
            SlideRectangle(23, Inch, 5 * Inch, 2 * Inch, Inch, "Apart"),
            SlideRectangle(24, 8 * Inch, 6 * Inch, Inch / 4, Inch / 4)); // alone and small: not cut

        var collector = new PictureCollector(savePictures: false, cutShapes: true);
        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false, pictures: collector));

        Assert.Equal(2, collector.Pictures.Count);
        Assert.Contains($"![]({PictureCollector.MarkStart}0{PictureCollector.MarkEnd})\n\nStart\n\nEnd", slide.Markdown, StringComparison.Ordinal);
        Assert.Contains($"![]({PictureCollector.MarkStart}1{PictureCollector.MarkEnd})\n\nApart", slide.Markdown, StringComparison.Ordinal);

        // The first picture holds the three shapes: from the left of the first box to the right of the last one.
        var room = 4.0 / 72;
        var area = collector.Pictures[0].Area!;
        Assert.Equal((1 - room) / 10, area.Left, 4);
        Assert.Equal((5 + 2 * room) / 10, area.Width, 4);
    }

    private static P.Picture PictureAt(uint id, string relationshipId, string description, long x, long y, long width, long height) =>
        new(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Picture " + id, Description = description },
                new P.NonVisualPictureDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new DocumentFormat.OpenXml.Drawing.Blip { Embed = relationshipId }, new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle())),
            new P.ShapeProperties(new DocumentFormat.OpenXml.Drawing.Transform2D(
                new DocumentFormat.OpenXml.Drawing.Offset { X = x, Y = y },
                new DocumentFormat.OpenXml.Drawing.Extents { Cx = width, Cy = height })));

    private static string[] AddPictures(string path, Func<string, string, P.Picture[]> pictures)
    {
        using var document = PresentationDocument.Open(path, isEditable: true);
        var slidePart = document.PresentationPart!.SlideParts.First();
        var red = PictureExtractionTests.AddImage(slidePart, ImagePartType.Png, PictureExtractionTests.RedPng);
        var blue = PictureExtractionTests.AddImage(slidePart, ImagePartType.Png, PictureExtractionTests.BluePng);
        foreach (var picture in pictures(red, blue))
        {
            slidePart.Slide!.CommonSlideData!.ShapeTree!.AppendChild(picture);
        }

        slidePart.Slide!.Save();
        return [red, blue];
    }

    [Fact]
    public void Pptx_PicturesNearOneAnother_AreCutOutAsOnePicture_APictureFarFromThemKeepsItsFile()
    {
        using var dir = new TempDir();
        var path = dir.Combine("pictures.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        AddPictures(path, (red, blue) =>
        [
            PictureAt(90, red, "Left", Inch, Inch, 2 * Inch, 2 * Inch),
            PictureAt(91, blue, "Right", 3 * Inch + Inch / 8, Inch, 2 * Inch, 2 * Inch), // an eighth of an inch from the left one
            PictureAt(92, red, "Far", 7 * Inch, 5 * Inch, 2 * Inch, Inch),
        ]);

        var collector = new PictureCollector(savePictures: true, cutShapes: true);
        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false, pictures: collector));

        // The two near ones are one picture of the slide (the box that holds both); the far one is its own file.
        Assert.Equal(2, collector.Pictures.Count);
        var cut = Assert.Single(collector.Pictures, p => p.IsShape);
        Assert.Contains("Left", slide.Markdown, StringComparison.Ordinal);
        Assert.Contains("Far", slide.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Right", slide.Markdown, StringComparison.Ordinal);
        var room = 4.0 / 72;
        Assert.Equal((1 - room) / 10, cut.Area!.Left, 4);
        Assert.Equal((4.125 + 2 * room) / 10, cut.Area.Width, 4);
    }

    [Fact]
    public void Pptx_AShapeOnAPicture_IsCutOutTogetherWithIt_AndThePictureIsNotAFileOfItsOwn()
    {
        using var dir = new TempDir();
        var path = dir.Combine("overlap.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        AddPictures(path, (red, _) => [PictureAt(90, red, "Photo", Inch, Inch, 5 * Inch, 4 * Inch)]);
        AddToSlide(path, SlideRectangle(20, 5 * Inch, 4 * Inch, 2 * Inch, Inch)); // a callout over the corner of the photo, reaching beyond it

        var collector = new PictureCollector(savePictures: true, cutShapes: true);
        PptxReader.Read(path, includeHidden: false, pictures: collector);

        var only = Assert.Single(collector.Pictures);
        Assert.True(only.IsShape);
        var room = 4.0 / 72;
        Assert.Equal((6 + 2 * room) / 10, only.Area!.Width, 4);
    }

    [Fact]
    public void Pptx_ShapesOnTheSameRow_AreCutOutTogetherHoweverFarApartAcross_ShapesOnAnotherRowAreNot()
    {
        using var dir = new TempDir();
        var path = dir.Combine("row.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        AddToSlide(path,
            SlideRectangle(20, Inch / 2, Inch, Inch, Inch, "Left"),
            SlideRectangle(21, 8 * Inch + Inch / 2, Inch + Inch / 4, Inch, Inch, "Right"), // seven inches from the first, on the same row
            SlideRectangle(22, Inch / 2, 4 * Inch, Inch, Inch, "Below"));

        var collector = new PictureCollector(savePictures: false, cutShapes: true);
        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false, pictures: collector));

        Assert.Equal(2, collector.Pictures.Count);
        Assert.Contains($"![]({PictureCollector.MarkStart}0{PictureCollector.MarkEnd})\n\nLeft\n\nRight", slide.Markdown, StringComparison.Ordinal);
        var room = 4.0 / 72;
        Assert.Equal((9 + 2 * room) / 10, collector.Pictures[0].Area!.Width, 4);
    }

    [Fact]
    public void Pptx_WithGroupingOff_EveryShapeIsCutOutAlone_AndAPictureKeepsItsFile()
    {
        using var dir = new TempDir();
        var path = dir.Combine("alone.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        AddToSlide(path,
            SlideRectangle(20, Inch, Inch, 2 * Inch, Inch, "Start"),
            SlideRectangle(21, 4 * Inch, Inch, 2 * Inch, Inch, "Near"), // on the same row: together when grouping is on
            SlideRectangle(22, 8 * Inch, Inch, Inch, Inch, "Far"));
        AddPictures(path, (red, _) => [PictureAt(90, red, "Photo", Inch, 4 * Inch, 2 * Inch, 2 * Inch)]);

        var grouped = new PictureCollector(savePictures: true, cutShapes: true);
        PptxReader.Read(path, includeHidden: false, pictures: grouped);
        Assert.Equal(2, grouped.Pictures.Count); // the three shapes are one, and the picture

        var alone = new PictureCollector(savePictures: true, cutShapes: true, groupShapes: false);
        PptxReader.Read(path, includeHidden: false, pictures: alone);
        Assert.Equal(4, alone.Pictures.Count); // three shapes, and the picture
        Assert.Equal(3, alone.Pictures.Count(p => p.IsShape));
    }

    [Fact]
    public void Pptx_ABackgroundShape_DoesNotJoinTheShapesOnIt()
    {
        using var dir = new TempDir();
        var path = dir.Combine("backdrop.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        AddToSlide(path,
            SlideRectangle(20, 0, 0, 10 * Inch, 7 * Inch),
            SlideRectangle(21, Inch, Inch, 2 * Inch, Inch, "A"),
            SlideRectangle(22, 6 * Inch, 5 * Inch, 2 * Inch, Inch, "B"));

        var collector = new PictureCollector(savePictures: false, cutShapes: true);
        PptxReader.Read(path, includeHidden: false, pictures: collector);

        Assert.Equal(3, collector.Pictures.Count);
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

        // 100 pixels of the page image at 150 dpi are 64 pixels at 96 to the inch: the width of the shape in the document (FR-148).
        Assert.Equal("<img src=\"pictures/page-001-1.png\" alt=\"Flow\" width=\"64\">\n\n![Lost]\n\n", blocks[0].Markdown);
        using var piece = new Bitmap(dir.Combine("pictures", "page-001-1.png"));
        Assert.Equal((100, 60), (piece.Width, piece.Height));
        Assert.Equal(Color.Red.ToArgb(), piece.GetPixel(50, 30).ToArgb());
        Assert.Contains(log, l => l.Contains("not cut out", StringComparison.Ordinal));
    }
}
