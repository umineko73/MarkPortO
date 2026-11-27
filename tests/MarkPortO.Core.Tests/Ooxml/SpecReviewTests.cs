// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// The findings of the review of the code against the spec (2026-11-27, MP-01 and on).

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pdf;
using MarkPortO.Core.Tests.Fixtures;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Ooxml;

public class SpecReviewTests
{
    private const double W = 11906 / 20.0;
    private const double H = 16838 / 20.0;

    private static UglyToad.PdfPig.Content.Page PageWith(Action<PdfPageBuilder> draw)
    {
        var builder = new PdfDocumentBuilder();
        draw(builder.AddPage(W, H));
        var document = UglyToad.PdfPig.PdfDocument.Open(builder.Build());
        return document.GetPage(1);
    }

    [Fact]
    public void HasCross_IsNotTwoLongLinesTheOtherWayThatDoNotMeet() // MP-01
    {
        // One down to the right in the lower left, one up to the right in the upper right: they would meet only beyond their ends.
        Assert.False(PdfShapeSnap.HasCross(PageWith(p =>
        {
            p.DrawLine(new PdfPoint(0, 0), new PdfPoint(0.6 * W, 0.6 * H), 2);
            p.DrawLine(new PdfPoint(0.4 * W, H), new PdfPoint(W, 0.4 * H), 2);
        })));
        // They meet only at their ends: a "V" on its side, not a "×".
        Assert.False(PdfShapeSnap.HasCross(PageWith(p =>
        {
            p.DrawLine(new PdfPoint(0, 0), new PdfPoint(0.7 * W, 0.7 * H), 2);
            p.DrawLine(new PdfPoint(0, 1.4 * 0.7 * H), new PdfPoint(0.7 * W, 0.7 * H), 2);
        })));
        // A "×" whose lines are drawn in another order and the other way round is still one.
        Assert.True(PdfShapeSnap.HasCross(PageWith(p =>
        {
            p.DrawLine(new PdfPoint(W - 20, 20), new PdfPoint(20, H - 20), 2);
            p.DrawLine(new PdfPoint(W - 20, H - 20), new PdfPoint(20, 20), 2);
        })));
    }

    [Fact]
    public void FindDown_WithoutAPlaceDown_DoesNotTakeASmallThingForTheDrawing() // MP-05
    {
        DrawnBox[] drawn = [new(100, 300, 105, 305)];

        Assert.Null(PdfShapeSnap.FindDown(drawn, W, H, 100 / W, 180 / W, 160 / H, 0, 1, null, 6));
        Assert.Null(PdfShapeSnap.FindDown(drawn, W, H, null, 180 / W, 160 / H, 0, 1, null, 6));
        // A drawing of its size is still found.
        Assert.NotNull(PdfShapeSnap.FindDown([new(100, 300, 268, 448)], W, H, null, 180 / W, 160 / H, 0, 1, null, 6));
    }

    [Fact]
    public void SheetShapesCutOutTogether_HoldATurnedShapeWhole() // MP-04
    {
        // A long flat box turned upright, and a box beside it on the same row.
        using var dir = new TempDir();
        var path = dir.Combine("turned.xlsx");
        string[][] cells = [.. Enumerable.Range(0, 40).Select(r => r == 39 ? Enumerable.Repeat("x", 12).ToArray() : ["a"])];
        XlsxFactory.Create(path, new SheetSpec("Data", cells, Shapes:
        [
            new CellShapeSpec("rect", 0, 15, 6, 17, Degrees: 90),
            new CellShapeSpec("rect", 9, 15, 11, 18),
        ]));

        var sheet = Assert.Single(XlsxReader.Read(path, false, pictures: new PictureCollector(cutShapes: true)));

        var shape = Assert.Single(sheet.Shapes);
        var turnedHeight = sheet.ShapeColumnWidthsPoints.Take(6).Sum(); // upright, the box is as tall as it was wide
        Assert.True(shape.Height >= turnedHeight - 0.01, $"{shape.Height} < {turnedHeight}");
        Assert.Equal(0, shape.Degrees);
    }

    [LibreOfficeFact]
    public async Task SheetShapesCutOutTogether_ThatArePrintedOnTwoPages_AreCutOutOnBoth() // MP-03
    {
        // Boxes one below another down a long sheet, each near the next: they are together, but printed on more than one page.
        using var dir = new TempDir();
        var source = dir.Combine("in", "long.xlsx");
        string[][] cells = [.. Enumerable.Range(0, 300).Select(r => r == 299 ? Enumerable.Repeat("x", 6).ToArray() : ["row " + r])]; // the used range holds the shapes
        XlsxFactory.Create(source, new SheetSpec("Data", cells, Shapes: [.. Enumerable.Range(0, 70).Select(i => new CellShapeSpec("rect", 2, i * 4, 4, i * 4 + 3))]));
        var options = Options();
        options.ExtractShapes = true;
        options.ExcelRange = MarkPortO.Core.Jobs.Models.ExcelRange.Columns;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.Pages.Count >= 2, "the sheet is printed on more than one page");
        var pictures = Directory.GetFiles(Path.Combine(document.OutputFolder, MarkPortO.Core.Pipeline.PictureFiles.Folder), "*.png");
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "long.md"));
        Assert.Equal(document.Pages.Count, pictures.Length); // one picture of the shapes on each page
        Assert.All(pictures, p => Assert.Contains("pictures/" + Path.GetFileName(p), markdown, StringComparison.Ordinal));
    }

    [LibreOfficeFact]
    public async Task AnXOfTwoLinesOnEachOfTwoPages_DeletesBothPages() // MP-02
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "two.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["the first page"]), new DocPage(["", ""]), new DocPage(["", ""]), new DocPage(["the last page"])]);
        using (var document = WordprocessingDocument.Open(source, isEditable: true))
        {
            var main = document.MainDocumentPart!;
            var paragraphs = main.Document!.Body!.Elements<Paragraph>().ToList();
            var lines = DocShape.CrossLines(0.95);
            paragraphs[1].AppendChild(DocxFactory.ShapeRunFor(lines[0], main));
            paragraphs[2].AppendChild(DocxFactory.ShapeRunFor(lines[1], main));
            paragraphs[3].AppendChild(DocxFactory.ShapeRunFor(lines[0], main));
            paragraphs[4].AppendChild(DocxFactory.ShapeRunFor(lines[1], main));
            main.Document.Save();
        }

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        Assert.Equal([2, 3], Assert.Single(result.Documents).Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }
}
