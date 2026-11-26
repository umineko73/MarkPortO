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

using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>The drawings of a Word paragraph are cut out of the page image (FR-136).</summary>
public class DocxShapeCropTests
{
    private const double PageWidthPoints = 11906 / 20.0;
    private const double MarginPoints = 1134 / 20.0;

    private static string Text(DocxContent content) => string.Join("\n", content.Blocks.Select(b => b.Block.Markdown));

    private static int Marks(string text) => text.Count(c => c == PictureCollector.MarkStart);

    [Fact]
    public void ShapesPlacedFromTheParagraph_AreOneCut_WhosePlaceDownIsFoundLater()
    {
        using var dir = new TempDir();
        var path = dir.Combine("diagram.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram"],
            [DocShape.InParagraph("rect", 0.2, 0.1, 0, 10), DocShape.InParagraph("roundRect", 0.2, 0.1, 200, 10)])]);

        var content = DocxReader.ReadContent(path, pictures: new PictureCollector(cutShapes: true));

        var crop = Assert.Single(Assert.Single(content.Shapes).Crops!);
        Assert.Null(crop.Area);
        Assert.NotNull(crop.Left);
        Assert.Equal(MarginPoints - 6, crop.Left!.Value * PageWidthPoints, 1);
        Assert.Equal(200 + 0.2 * PageWidthPoints + 12, crop.Width * PageWidthPoints, 0);
        Assert.Equal(0.1, crop.Height, 3);
        Assert.Equal(1, Marks(Text(content)));
    }

    [Fact]
    public void APictureOfACutDrawing_IsKeptToBeWrittenWhenTheDrawingIsNotFound()
    {
        using var dir = new TempDir();
        var path = dir.Combine("diagram.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram"],
            [DocShape.InParagraph("rect", 0.2, 0.1, 0, 10), DocShape.InParagraph("rect", 0.2, 0.1, 200, 10), DocShape.InParagraph("picture", 0.1, 0.05, 130, 20)])]);
        var pictures = new PictureCollector(cutShapes: true);

        var content = DocxReader.ReadContent(path, pictures: pictures);

        var crop = Assert.Single(Assert.Single(content.Shapes).Crops!);
        var fallback = pictures.Pictures[PictureCollector.NumberOf(crop.Mark)].Fallback;
        Assert.NotNull(fallback);
        Assert.Equal(1, Marks(fallback));
        Assert.Equal(1, Marks(Text(content))); // only the cut: the picture is in it
        Assert.Contains(pictures.Pictures, p => !p.IsShape);
    }

    [Fact]
    public void APictureThatIsNotInTheCut_StaysAPicture()
    {
        // The shapes are placed on the page, the picture from the paragraph: the cut is of the shapes, the picture is written as it is.
        using var dir = new TempDir();
        var path = dir.Combine("diagram.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram"],
            [new DocShape("rect", 0.2, 0.1, XPoints: 72, YPoints: 300), new DocShape("rect", 0.2, 0.1, XPoints: 300, YPoints: 300), DocShape.InParagraph("picture", 0.1, 0.05, 0, 0)])]);

        var pictures = new PictureCollector(cutShapes: true);
        var content = DocxReader.ReadContent(path, pictures: pictures);

        var crop = Assert.Single(Assert.Single(content.Shapes).Crops!);
        Assert.NotNull(crop.Area);
        Assert.Null(pictures.Pictures[PictureCollector.NumberOf(crop.Mark)].Fallback);
        Assert.Equal(2, Marks(Text(content))); // the cut and the picture
    }

    [Fact]
    public void NotGrouped_EachShapeIsCutAlone_AndThePictureStaysAPicture()
    {
        using var dir = new TempDir();
        var path = dir.Combine("diagram.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram"],
            [DocShape.InParagraph("rect", 0.2, 0.1, 0, 10), DocShape.InParagraph("rect", 0.2, 0.1, 200, 10), DocShape.InParagraph("picture", 0.1, 0.05, 130, 20)])]);

        var content = DocxReader.ReadContent(path, pictures: new PictureCollector(cutShapes: true, groupShapes: false));

        Assert.Equal(2, Assert.Single(content.Shapes).Crops!.Count);
        Assert.Equal(3, Marks(Text(content))); // two cuts and the picture
    }

    [Fact]
    public void ACutPlacedOnThePage_HasSixPointsOfRoom()
    {
        using var dir = new TempDir();
        var path = dir.Combine("diagram.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram"], [new DocShape("rect", 0.2, 0.1, XPoints: 100, YPoints: 200)])]);

        var content = DocxReader.ReadContent(path, pictures: new PictureCollector(cutShapes: true));

        var area = Assert.Single(Assert.Single(content.Shapes).Crops!).Area!;
        Assert.Equal(94, area.Left * PageWidthPoints, 1);
        Assert.Equal(0.2 * PageWidthPoints + 12, area.Width * PageWidthPoints, 1);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(0, 0)]
    [InlineData(-1, PictureCollector.DefaultGroupGapPoints)]
    [InlineData(double.NaN, PictureCollector.DefaultGroupGapPoints)]
    [InlineData(10000, PictureCollector.DefaultGroupGapPoints)]
    public void TheGapOfTheShapesOfASlide_IsTakenFromTheSettings_WhenItMakesSense(double given, double taken) =>
        Assert.Equal(taken, new PictureCollector(groupGapPoints: given).GroupGapPoints);

    [LibreOfficeFact]
    public async Task ADiagramPlacedFromItsParagraph_IsFoundInThePdf_AndCutOutAsOnePicture()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "diagram.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["text above the diagram", "text below"],
            [DocShape.InParagraph("rect", 0.2, 0.1, 0, 30), DocShape.InParagraph("roundRect", 0.2, 0.1, 200, 30), DocShape.InParagraph("picture", 0.1, 0.05, 130, 40)])]);
        var options = Options();
        options.ExtractShapes = true;
        options.ExtractPictures = true;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));
        var output = Assert.Single(result.Documents).OutputFolder;
        var files = Directory.GetFiles(Path.Combine(output, PictureFiles.Folder), "*.png");
        var markdown = File.ReadAllText(Path.Combine(output, "diagram.md"));

        // One picture: the cut, which has the picture in it (the picture itself is not written).
        var file = Assert.Single(files);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markdown, @"pictures/page-\d{3}-\d+\.png"));
        using var image = new System.Drawing.Bitmap(file);
        var widthPoints = image.Width * 72.0 / options.Dpi;
        var heightPoints = image.Height * 72.0 / options.Dpi;
        Assert.InRange(widthPoints, 0.85 * (200 + 0.2 * PageWidthPoints + 12), 1.15 * (200 + 0.2 * PageWidthPoints + 12));
        Assert.InRange(heightPoints, 0.75 * (0.1 * 842 + 12), 1.5 * (0.1 * 842 + 12));
    }
}
