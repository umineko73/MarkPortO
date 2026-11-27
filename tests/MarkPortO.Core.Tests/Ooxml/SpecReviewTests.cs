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
        for (var page = 1; page <= document.Pages.Count; page++)
        {
            // The picture of a page is named after it, and is in the Markdown of that page (R2 R1-02).
            var name = $"pictures/page-{page:000}-1.png";
            Assert.Contains(pictures, p => p.Replace('\\', '/').EndsWith(name, StringComparison.Ordinal));
            var start = markdown.IndexOf($"<!-- page: {page} -->", StringComparison.Ordinal);
            var end = markdown.IndexOf($"<!-- page: {page + 1} -->", StringComparison.Ordinal);
            var at = markdown.IndexOf(name, StringComparison.Ordinal);
            Assert.True(start >= 0 && at > start && (end < 0 || at < end), $"{name} is not in page {page}");
        }
    }

    [LibreOfficeFact]
    public async Task SheetShapesCutOutTogether_ThatAreTooSmallOnEachPage_KeepTheirText() // R2-01
    {
        // Two flat boxes near each other, one at the end of the first page and one at the top of the second (rows of 15 points, the first
        // page of this sheet ends after row 61): together they are big enough, on each page not.
        using var dir = new TempDir();
        var source = dir.Combine("in", "split.xlsx");
        string[][] cells = [.. Enumerable.Range(0, 300).Select(r => r == 299 ? Enumerable.Repeat("x", 6).ToArray() : ["row " + r])];
        XlsxFactory.Create(source, new SheetSpec("Data", cells, Shapes:
        [
            new CellShapeSpec("rect", 2, 60, 4, 61, Description: "Inspection diagram"),
            new CellShapeSpec("rect", 2, 62, 4, 63),
        ]));
        var options = Options();
        options.ExtractShapes = true;
        options.ExcelRange = MarkPortO.Core.Jobs.Models.ExcelRange.Columns;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        var document = Assert.Single(result.Documents);
        // On one page they would be cut out as one picture: none is, as no piece on either page is big enough.
        Assert.False(Directory.Exists(Path.Combine(document.OutputFolder, MarkPortO.Core.Pipeline.PictureFiles.Folder)), "nothing is cut out");
        Assert.Contains("Inspection diagram", File.ReadAllText(Path.Combine(document.OutputFolder, "split.md")), StringComparison.Ordinal);
    }

    // ---- a "×" of old VML shapes (a document in compatibility mode), a little smaller than the page (2026-11-27) ----

    // The "×" of a design document saved by Word 16 in the compatibility mode of Word 2007: a group of two straight connectors, 390.75 x 553.5 pt.
    private const string VmlCross = """
        <w:pict xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office"><v:group id="_x0000_s2052" style="position:absolute;left:0;text-align:left;margin-left:9.45pt;margin-top:6.5pt;width:390.75pt;height:553.5pt;z-index:251660288" coordorigin="1890,2115" coordsize="7815,11070"><v:shapetype id="_x0000_t32" coordsize="21600,21600" o:spt="32" o:oned="t" path="m,l21600,21600e" filled="f"><v:path arrowok="t" fillok="f" o:connecttype="none"/><o:lock v:ext="edit" shapetype="t"/></v:shapetype><v:shape id="_x0000_s2050" type="#_x0000_t32" style="position:absolute;left:1890;top:2310;width:7575;height:10875;flip:x" o:connectortype="straight" strokecolor="#e00" strokeweight="2.25pt"/><v:shape id="_x0000_s2051" type="#_x0000_t32" style="position:absolute;left:2130;top:2115;width:7575;height:10875" o:connectortype="straight" strokecolor="#e00" strokeweight="2.25pt"/></v:group></w:pict>
        """;

    [Fact]
    public void TheLinesOfAnOldVmlDrawing_AreReadForTheXCheck()
    {
        var paragraph = new Paragraph(new Run(new Picture(VmlCross.Trim())));
        const double letterWidth = 612 * 12700.0, letterHeight = 792 * 12700.0;

        var boxes = ShapeExtractor.ExtractVmlLines(paragraph, letterWidth, letterHeight);

        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, b => Assert.Equal("line", b.Geometry));
        Assert.All(boxes, b => Assert.Equal(7575 / 7815.0 * 390.75 / 612, b.WidthFraction, 3));
        Assert.All(boxes, b => Assert.Equal(10875 / 11070.0 * 553.5 / 792, b.HeightFraction, 3));
        Assert.Equal([true, false], boxes.Select(b => b.AntiDiagonal));
        // 62% x 69% of the page: a "×" of a Word document (3/5 of the page), not of the rule for the other kinds (7/10).
        Assert.True(CrossShapeDetector.Evaluate(boxes, CrossShapeDetector.WordCoverageThreshold).IsCross);
        Assert.False(CrossShapeDetector.Evaluate(boxes).IsCross);
    }

    [Fact]
    public void AVmlCopyForOldReaders_IsNotCountedAgain_NorAShapeThatIsNotALine()
    {
        var fallback = new Paragraph(new Run(new DocumentFormat.OpenXml.AlternateContent(
            new DocumentFormat.OpenXml.AlternateContentFallback(new Picture(VmlCross.Trim())))));
        Assert.Empty(ShapeExtractor.ExtractVmlLines(fallback, 612 * 12700.0, 792 * 12700.0));

        var oval = new Paragraph(new Run(new Picture("""<w:pict xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:v="urn:schemas-microsoft-com:vml"><v:oval style="position:absolute;width:500pt;height:700pt"/><v:rect style="position:absolute;width:500pt;height:700pt"/></w:pict>""")));
        Assert.Empty(ShapeExtractor.ExtractVmlLines(oval, 612 * 12700.0, 792 * 12700.0));

        var line = new Paragraph(new Run(new Picture("""<w:pict xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:v="urn:schemas-microsoft-com:vml"><v:line from="10pt,700pt" to="500pt,20pt"/></w:pict>""")));
        var box = Assert.Single(ShapeExtractor.ExtractVmlLines(line, 612 * 12700.0, 792 * 12700.0));
        Assert.True(box.AntiDiagonal); // up to the right
        Assert.Equal(490 / 612.0, box.WidthFraction, 3);
    }

    [LibreOfficeFact]
    public async Task AnXOfOldVmlShapes_DeletesItsPage()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "vml.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["the first page stays"]), new DocPage(["the second page, crossed out", ""]), new DocPage(["the third page"])]);
        using (var document = WordprocessingDocument.Open(source, isEditable: true))
        {
            var main = document.MainDocumentPart!;
            main.Document!.Body!.Elements<Paragraph>().ElementAt(2).AppendChild(new Run(new Picture(VmlCross.Trim())));
            main.Document.Save();
        }

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        Assert.Equal([2], Assert.Single(result.Documents).Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }

    [Fact]
    public void TheLinesOfAnX_DeleteOnlyAPageThatBothCanBeOn() // R2 R1-01
    {
        // A line down on pages 1-2, a line up on pages 2-3; the PDF draws a "×" on every page (another drawing on page 1).
        Assert.Equal([2], MarkPortO.Core.Pipeline.Step1Worker.LineCrossPages([(1, 2, false), (2, 3, true)], _ => true));
        // Two "×" on two pages, each of its two lines.
        Assert.Equal([1, 2], MarkPortO.Core.Pipeline.Step1Worker.LineCrossPages([(1, 2, false), (1, 2, true), (2, 3, false), (2, 3, true)], _ => true));
        // A page without a "×" in the PDF is not deleted; the lines are kept for the next page they can be on.
        Assert.Equal([2], MarkPortO.Core.Pipeline.Step1Worker.LineCrossPages([(1, 2, false), (1, 2, true)], p => p != 1));
        Assert.Empty(MarkPortO.Core.Pipeline.Step1Worker.LineCrossPages([(1, 3, false), (1, 3, false)], _ => true));
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
