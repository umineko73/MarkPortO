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
using DocumentFormat.OpenXml.Wordprocessing;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pdf;
using MarkPortO.Core.Tests.Fixtures;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>
/// The findings of the second review of the cut-out of Word drawings (FR-136, FR-012): a page is taken as deleted only where the PDF shows a "×",
/// and a drawing is cut out only where it is found for sure.
/// </summary>
public class DocxShapeRound2Tests
{
    private const double W = 11906 / 20.0;
    private const double H = 16838 / 20.0;

    // ---- the "×" of a deleted page is where the PDF draws it ----

    [LibreOfficeTheory]
    [InlineData(false, false)] // R2-01: with no cut-out, the page of the "×" is still found
    [InlineData(true, true)] // R2-02: a small logo in the header of every page is not the "×"
    public async Task AnXInAnEmptyParagraph_DeletesThePageItIsDrawnOn(bool cutShapes, bool logo)
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "cross.docx");
        DocxFactory.CreateWithShapes(source,
            [new DocPage(["the first page stays"]), new DocPage([""], DocShape.CrossShape(0.8)), new DocPage(["the third page"])],
            logo ? [new DocShape("rect", 0.04, 0.04, XPoints: 40, YPoints: 20)] : null);
        var options = Options();
        options.ExtractShapes = cutShapes;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        Assert.Equal([2], Assert.Single(result.Documents).Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }

    [LibreOfficeFact]
    public async Task AnXOfTwoLinesCutApart_DeletesThePageOfTheLines() // R2-03, R2-06 (lines over nearly the whole page)
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "lines.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["the first page stays"]), new DocPage([""], DocShape.CrossLines(0.95)), new DocPage(["the third page"])]);
        var options = Options();
        options.ExtractShapes = true;
        options.GroupNearShapes = false;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        Assert.Equal([2], Assert.Single(result.Documents).Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }

    [LibreOfficeFact]
    public async Task AnXAfterAPageOfADrawingOnly_DeletesItsOwnPage() // R2-04
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "pages.docx");
        DocxFactory.CreateWithShapes(source,
            [new DocPage(["the first page stays"]), new DocPage([""], [new DocShape("rect", 0.3, 0.2, XPoints: 100, YPoints: 200)]), new DocPage([""], DocShape.CrossShape(0.8))]);
        var options = Options();
        options.ExtractShapes = true;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        Assert.Equal([3], Assert.Single(result.Documents).Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }

    [Fact]
    public void HasCross_IsTwoCrossingLinesOverThePage_NotOneLineABorderOrALogo()
    {
        static UglyToad.PdfPig.Content.Page PageWith(Action<PdfPageBuilder> draw)
        {
            var builder = new PdfDocumentBuilder();
            draw(builder.AddPage(W, H));
            var document = UglyToad.PdfPig.PdfDocument.Open(builder.Build());
            return document.GetPage(1);
        }

        Assert.True(PdfShapeSnap.HasCross(PageWith(p =>
        {
            p.DrawLine(new PdfPoint(20, 20), new PdfPoint(W - 20, H - 20), 2);
            p.DrawLine(new PdfPoint(20, H - 20), new PdfPoint(W - 20, 20), 2);
        })));
        Assert.False(PdfShapeSnap.HasCross(PageWith(p => p.DrawLine(new PdfPoint(20, 20), new PdfPoint(W - 20, H - 20), 2)))); // one line is not a "×"
        Assert.False(PdfShapeSnap.HasCross(PageWith(p => p.DrawRectangle(new PdfPoint(10, 10), W - 20, H - 20, 2))));
        Assert.False(PdfShapeSnap.HasCross(PageWith(p => p.DrawLine(new PdfPoint(40, H - 40), new PdfPoint(80, H - 90), 2))));
    }

    // ---- finding a drawing for sure ----

    [Fact]
    public void FindDown_TakesTheDrawingAtItsParagraph_EvenWithABiggerLogoAbove() // R2-11
    {
        DrawnBox[] drawn = [new(57, 54, 125, 91), new(57, 312, 105, 337)];

        var area = PdfShapeSnap.FindDown(drawn, W, H, 57 / W, 80 / W, 25 / H, 0, 1, 310 / H, 6);

        Assert.NotNull(area);
        Assert.Equal(306, area.Top * H, 0);
    }

    [Fact]
    public void FindDown_FindsNothing_WhenNothingIsNearWhereItIsExpected()
    {
        DrawnBox[] drawn = [new(57, 54, 125, 91)];

        Assert.Null(PdfShapeSnap.FindDown(drawn, W, H, 57 / W, 80 / W, 25 / H, 0, 1, 310 / H, 6));
    }

    [Fact]
    public void FindDown_WithoutAPlaceAcross_DoesNotTakeTwoDrawingsSideBySide() // R2-12
    {
        DrawnBox[] drawn = [new(240, 100, 323, 160), new(480, 100, 563, 160)];

        Assert.Null(PdfShapeSnap.FindDown(drawn, W, H, null, 83 / W, 60 / H, 0, 1, 100 / H, 6));
    }

    [Fact]
    public void FindDown_WithoutAPlaceDown_DoesNotChooseBetweenTwoPlaces()
    {
        DrawnBox[] drawn = [new(60, 100, 120, 160), new(60, 500, 120, 560)];

        Assert.Null(PdfShapeSnap.FindDown(drawn, W, H, 60 / W, 60 / W, 60 / H, 0, 1, null, 6));
    }

    [Fact]
    public void Fills_TakesADrawingThatReachesOutOfOneFoundBefore() // R2-13
    {
        DrawnBox[] drawn = [new(60, 80, 270, 230), new(180, 165, 285, 255)];
        var first = new ShapeArea(54 / W, 74 / H, 222 / W, 162 / H);
        var second = new ShapeArea(174 / W, 159 / H, 117 / W, 102 / H);

        Assert.True(PdfShapeSnap.Fills(drawn, W, H, second, 6, [first]));
    }

    [Fact]
    public void Fills_IsNotASmallLogoInACornerOfThePlace()
    {
        DrawnBox[] drawn = [new(40, 20, 64, 54)];
        var place = new ShapeArea(34 / W, 14 / H, 460 / W, 680 / H);

        Assert.False(PdfShapeSnap.Fills(drawn, W, H, place, 6));
    }

    [Fact]
    public void TextPlace_FindsTheParagraphByAllItsText_WhenItBeginsLikeAnother() // R2-10
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(W, H);
        page.AddText("abcdefghijklmnop first anchor caption", 10, new PdfPoint(60, H - 60), font);
        page.AddText("abcdefghijklmnop second anchor caption", 10, new PdfPoint(60, H - 300), font);
        using var document = UglyToad.PdfPig.PdfDocument.Open(builder.Build());

        var place = new PdfTextPlace(document.GetPage(1));

        Assert.InRange(place.TopOf("abcdefghijklmnop second anchor caption")!.Value * H, 280, 300);
        Assert.Null(place.TopOf("abcdefghijklmnop"));
    }

    // ---- table cells ----

    [Fact]
    public void ADrawingInATableCell_PlacedFromItsColumn_HasNoPlaceAcross() // R2-07
    {
        using var dir = new TempDir();
        var path = dir.Combine("cell.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["in the cell", "after"], [DocShape.InParagraph("rect", 0.2, 0.1, 0, 10)])]);
        IntoCell(path);

        var content = DocxReader.ReadContent(path, pictures: new PictureCollector(cutShapes: true, groupShapes: false));

        Assert.Null(Assert.Single(Assert.Single(content.Shapes).Crops!).Left);
    }

    [Fact]
    public void TextBoxesInACell_ThatAreNotCutOut_KeepTheirText_AndAPipeIsEscaped() // R2-09, R2-14
    {
        using var dir = new TempDir();
        var path = dir.Combine("boxes.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["in the cell", "after"], [DocShape.InParagraph("picture", 0.1, 0.05, 0, 10)])]);
        using (var document = WordprocessingDocument.Open(path, isEditable: true))
        {
            var paragraph = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
            paragraph.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>().First().Description = "A|B";
            paragraph.AppendChild(TextBoxRun("first label", 0));
            paragraph.AppendChild(TextBoxRun("second label", 200));
            document.MainDocumentPart.Document.Save();
        }

        IntoCell(path);
        var pictures = new PictureCollector(savePictures: false, cutShapes: true);

        var content = DocxReader.ReadContent(path, pictures: pictures);

        var crop = Assert.Single(Assert.Single(content.Shapes).Crops!);
        var fallback = pictures.Pictures[PictureCollector.NumberOf(crop.Mark)].Fallback;
        Assert.NotNull(fallback);
        Assert.Contains("first label", fallback, StringComparison.Ordinal);
        Assert.Contains("second label", fallback, StringComparison.Ordinal);
        Assert.Contains("A\\|B", fallback, StringComparison.Ordinal);
        Assert.DoesNotContain("A|B", fallback, StringComparison.Ordinal);
    }

    // ---- the third review ----

    [LibreOfficeFact]
    public async Task APolygonOfTwelveSides_IsNotAnX() // R3-01
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "polygon.docx");
        DocxFactory.CreateWithShapes(source,
            [new DocPage(["Keep this text"], [new DocShape("dodecagon", 0.55, 0.55, XPoints: 100, YPoints: 150)]), new DocPage([""], DocShape.CrossShape(0.8)), new DocPage(["the third page"])]);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        Assert.Equal([2], Assert.Single(result.Documents).Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }

    [LibreOfficeFact]
    public async Task TwoXsOnPagesOfEmptyParagraphs_DeleteBothPages() // R3-02
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "two.docx");
        DocxFactory.CreateWithShapes(source,
            [new DocPage(["the first page"]), new DocPage([""], DocShape.CrossShape(0.8)), new DocPage([""], DocShape.CrossShape(0.8)), new DocPage(["the fourth page"])]);
        var options = Options();
        options.ExtractShapes = true;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        Assert.Equal([2, 3], Assert.Single(result.Documents).Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }

    [LibreOfficeFact]
    public async Task TwoLinesOfAnXInAParagraphAndTheEmptyOneAfterIt_DeleteTheirPage() // R3-03
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "split.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["the lines", ""], DocShape.CrossLines(0.95)), new DocPage(["the second page"])]);
        using (var document = WordprocessingDocument.Open(source, isEditable: true))
        {
            var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();
            var second = paragraphs[0].Elements<Run>().Last(r => r.Descendants<Drawing>().Any());
            second.Remove();
            paragraphs[1].AppendChild(second);
            document.MainDocumentPart.Document.Save();
        }

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        Assert.Equal([1], Assert.Single(result.Documents).Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }

    [Fact]
    public void FindDown_DoesNotTakeASmallDrawingNearTheParagraph_ForTheDrawing() // R3-04
    {
        DrawnBox[] drawn = [new(60, 100, 64, 104), new(60, 125, 140, 150)];

        var area = PdfShapeSnap.FindDown(drawn, 600, 800, 60 / 600.0, 80 / 600.0, 25 / 800.0, 0, 1, 100 / 800.0, 6);

        Assert.NotNull(area);
        Assert.Equal(119, area.Top * 800, 0);
        Assert.Equal(156, (area.Top + area.Height) * 800, 0);
    }

    [Fact]
    public void Fills_ADrawingAtTheEdgeOfThePage_WhoseRoomWasCutOff() // R3-05
    {
        DrawnBox[] drawn = [new(0, 100, 3, 200)];
        var area = new ShapeArea(0, 94 / H, 9 / W, 112 / H);

        Assert.True(PdfShapeSnap.Fills(drawn, W, H, area, 6));
    }

    [Fact]
    public void ATableInATextBoxInACell_KeepsItsText_WhenItIsNotCutOut() // R3-06
    {
        using var dir = new TempDir();
        var path = dir.Combine("table-box.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["in the cell", "after"], [DocShape.InParagraph("picture", 0.1, 0.05, 0, 10)])]);
        using (var document = WordprocessingDocument.Open(path, isEditable: true))
        {
            var paragraph = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
            paragraph.AppendChild(TextBoxRun("paragraph kept", 0));
            paragraph.AppendChild(TextBoxRun("nested table text", 200, inTable: true));
            document.MainDocumentPart.Document.Save();
        }

        IntoCell(path);
        var pictures = new PictureCollector(savePictures: false, cutShapes: true);

        var content = DocxReader.ReadContent(path, pictures: pictures);

        var fallback = pictures.Pictures[PictureCollector.NumberOf(Assert.Single(Assert.Single(content.Shapes).Crops!).Mark)].Fallback;
        Assert.Contains("paragraph kept", fallback, StringComparison.Ordinal);
        Assert.Contains("nested table text", fallback, StringComparison.Ordinal);
    }

    // Moves the first paragraph of the body into the cell of a table of one row.
    private static void IntoCell(string path)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: true);
        var body = document.MainDocumentPart!.Document!.Body!;
        var paragraph = body.Elements<Paragraph>().First();
        var table = new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("left"))))), new TableRow(new TableCell()));
        paragraph.InsertBeforeSelf(table);
        paragraph.Remove();
        table.Elements<TableRow>().Last().GetFirstChild<TableCell>()!.AppendChild(paragraph);
        document.MainDocumentPart.Document.Save();
    }

    // A text box without line or fill, with one paragraph of text (or a table of one cell), placed from the column and the paragraph.
    private static Run TextBoxRun(string text, double xPoints, bool inTable = false) => new($"""
        <w:r xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
             xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
             xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
             xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
          <w:drawing>
            <wp:anchor distT="0" distB="0" distL="0" distR="0" simplePos="0" relativeHeight="251659264" behindDoc="0" locked="0" layoutInCell="1" allowOverlap="1">
              <wp:simplePos x="0" y="0"/>
              <wp:positionH relativeFrom="column"><wp:posOffset>{(long)(xPoints * 12700)}</wp:posOffset></wp:positionH>
              <wp:positionV relativeFrom="paragraph"><wp:posOffset>127000</wp:posOffset></wp:positionV>
              <wp:extent cx="1905000" cy="635000"/>
              <wp:effectExtent l="0" t="0" r="0" b="0"/>
              <wp:wrapNone/>
              <wp:docPr id="{900 + (int)xPoints}" name="Text box"/>
              <wp:cNvGraphicFramePr/>
              <a:graphic>
                <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
                  <wps:wsp>
                    <wps:cNvSpPr txBox="1"/>
                    <wps:spPr>
                      <a:xfrm><a:off x="0" y="0"/><a:ext cx="1905000" cy="635000"/></a:xfrm>
                      <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                      <a:noFill/>
                      <a:ln><a:noFill/></a:ln>
                    </wps:spPr>
                    <wps:txbx><w:txbxContent>{(inTable ? $"<w:tbl><w:tr><w:tc><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p/>" : $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>")}</w:txbxContent></wps:txbx>
                    <wps:bodyPr/>
                  </wps:wsp>
                </a:graphicData>
              </a:graphic>
            </wp:anchor>
          </w:drawing>
        </w:r>
        """);
}
