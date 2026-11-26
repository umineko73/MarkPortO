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

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pdf;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>The findings of the review of the cut-out of Word drawings (FR-136), each kept from coming back.</summary>
public class DocxShapeReviewTests
{
    private const double PageWidthPoints = 11906 / 20.0;
    private const double PageHeightPoints = 16838 / 20.0;

    private static DocShape[] TwoBoxes => [DocShape.InParagraph("rect", 0.2, 0.1, 0, 10), DocShape.InParagraph("rect", 0.2, 0.1, 200, 10)];

    // Changes the body of a docx that the factory made.
    private static void Change(string path, Action<Body> change)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: true);
        change(document.MainDocumentPart!.Document!.Body!);
        document.MainDocumentPart.Document.Save();
    }

    // ---- the reader ----

    [Fact]
    public void ADrawingDeletedWithTrackedChanges_IsNotCutOut()
    {
        using var dir = new TempDir();
        var path = dir.Combine("deleted.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram"], [new DocShape("rect", 0.2, 0.1, XPoints: 100, YPoints: 200)])]);
        Change(path, body =>
        {
            var run = body.Descendants<Run>().First(r => r.Descendants<Drawing>().Any());
            var deleted = new DeletedRun { Id = "1", Author = "someone" };
            run.InsertBeforeSelf(deleted);
            run.Remove();
            deleted.AppendChild(run);
        });

        var content = DocxReader.ReadContent(path, pictures: new PictureCollector(cutShapes: true, groupShapes: false));

        Assert.DoesNotContain(content.Shapes, s => s.Crops is { Count: > 0 });
    }

    [Fact]
    public void TheSectionOfAParagraph_IsFoundInsideAContentControl()
    {
        // The paragraph of the drawing and a landscape section break are in a content control; the last section is portrait.
        using var dir = new TempDir();
        var path = dir.Combine("sections.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram", "end of the landscape section"], [new DocShape("rect", 0.2, 0.1, XPoints: 100, YPoints: 200)])]);
        Change(path, body =>
        {
            var paragraphs = body.Elements<Paragraph>().Take(2).ToList();
            var content = new SdtContentBlock();
            var control = new SdtBlock(new SdtProperties(), content);
            paragraphs[0].InsertBeforeSelf(control);
            foreach (var paragraph in paragraphs)
            {
                paragraph.Remove();
                content.AppendChild(paragraph);
            }

            paragraphs[1].ParagraphProperties ??= new ParagraphProperties();
            paragraphs[1].ParagraphProperties!.AppendChild(new SectionProperties(
                new PageSize { Width = 16838, Height = 11906, Orient = PageOrientationValues.Landscape },
                new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134 }));
        });

        var content = DocxReader.ReadContent(path, pictures: new PictureCollector(cutShapes: true, groupShapes: false));

        var area = Assert.Single(Assert.Single(content.Shapes).Crops!).Area!;
        Assert.Equal(94 / PageHeightPoints, area.Left, 3); // the width of the landscape page is the height of the portrait one
    }

    [Theory]
    [InlineData("character")]
    [InlineData("column")]
    public void APlaceAcrossFromACharacterOrAColumnOfSeveral_IsNotKnown(string from)
    {
        using var dir = new TempDir();
        var path = dir.Combine("across.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram"],
            [new DocShape("rect", 0.2, 0.1, HorizontalFrom: from, VerticalFrom: "paragraph", XPoints: 40, YPoints: 10), new DocShape("rect", 0.2, 0.1, HorizontalFrom: from, VerticalFrom: "paragraph", XPoints: 250, YPoints: 10)])]);
        if (from == "column")
        {
            Change(path, body => body.Elements<SectionProperties>().Last().AppendChild(new Columns { ColumnCount = 2 }));
        }

        var content = DocxReader.ReadContent(path, pictures: new PictureCollector(cutShapes: true));

        var crop = Assert.Single(Assert.Single(content.Shapes).Crops!);
        Assert.Null(crop.Left);
        Assert.True(crop.Width > 0);
    }

    [Fact]
    public void ADrawingInATableCell_IsCutOut_AsAPictureInItsRow()
    {
        using var dir = new TempDir();
        var path = dir.Combine("table.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["in the cell", "after the table"], TwoBoxes)]);
        Change(path, body =>
        {
            var paragraph = body.Elements<Paragraph>().First();
            var table = new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("first row"))))), new TableRow(new TableCell()));
            paragraph.InsertBeforeSelf(table);
            paragraph.Remove();
            table.Elements<TableRow>().Last().GetFirstChild<TableCell>()!.AppendChild(paragraph);
        });
        var pictures = new PictureCollector(cutShapes: true);

        var content = DocxReader.ReadContent(path, pictures: pictures);

        var shapes = Assert.Single(content.Shapes);
        var crop = Assert.Single(shapes.Crops!);
        Assert.Equal(1, shapes.Row);
        Assert.Null(crop.Block);
        Assert.Empty(shapes.Boxes); // not part of the "×" check
        Assert.Equal(BlockKind.Table, content.Blocks[shapes.HostBlockIndex].Block.Kind);
        Assert.Contains(crop.Mark, content.Blocks[shapes.HostBlockIndex].Block.Markdown, StringComparison.Ordinal);
    }

    // ---- finding a drawing in the PDF ----

    [Fact]
    public void FindDown_TakesTheDrawingNearItsParagraph_NotOneAboveIt()
    {
        // A logo in the same band at the top of the page, and the drawing next to its paragraph further down.
        DrawnBox[] drawn = [new(57, 21, 119, 61), new(57, 262, 117, 342)];

        var area = PdfShapeSnap.FindDown(drawn, PageWidthPoints, PageHeightPoints, 51 / PageWidthPoints, 72 / PageWidthPoints, 80 / PageHeightPoints, 0, 1, 255 / PageHeightPoints, 6);

        Assert.NotNull(area);
        Assert.Equal(256, area.Top * PageHeightPoints, 0);
    }

    [Fact]
    public void FindDown_LeavesOutTheDrawingsFoundBefore_NotWhatIsBesideThem()
    {
        // Two drawings side by side at the same height: the first is found, the second is still found beside it.
        DrawnBox[] drawn = [new(60, 100, 120, 180), new(320, 100, 380, 180)];
        var first = PdfShapeSnap.FindDown(drawn, PageWidthPoints, PageHeightPoints, 54 / PageWidthPoints, 72 / PageWidthPoints, 80 / PageHeightPoints, 0, 1, null, 6)!;

        var second = PdfShapeSnap.FindDown(drawn, PageWidthPoints, PageHeightPoints, 314 / PageWidthPoints, 72 / PageWidthPoints, 80 / PageHeightPoints, 0, 1, null, 6, [first]);

        Assert.NotNull(second);
        Assert.Equal(308, second.Left * PageWidthPoints, 0); // its place across, with the room around it
    }

    [Fact]
    public void HasDrawing_DoesNotCountADrawingFoundBefore()
    {
        DrawnBox[] drawn = [new(100, 100, 200, 200)];
        var area = new ShapeArea(90 / PageWidthPoints, 90 / PageHeightPoints, 120 / PageWidthPoints, 120 / PageHeightPoints);

        Assert.True(PdfShapeSnap.HasDrawing(drawn, PageWidthPoints, PageHeightPoints, area));
        Assert.False(PdfShapeSnap.HasDrawing(drawn, PageWidthPoints, PageHeightPoints, area, [area]));
    }

    [Fact]
    public void FindDown_OnAPageOfManySmallDrawings_FindsTheBand()
    {
        var drawn = Enumerable.Range(0, 1800).Select(i => new DrawnBox(60 + i % 30 * 2, 100 + i / 30 * 2, 61 + i % 30 * 2, 101 + i / 30 * 2)).ToArray();

        var area = PdfShapeSnap.FindDown(drawn, PageWidthPoints, PageHeightPoints, null, 70 / PageWidthPoints, 130 / PageHeightPoints, 0, 1, null, 6);

        Assert.NotNull(area);
        Assert.Equal(94, area.Top * PageHeightPoints, 0);
    }

    // ---- writing the pictures ----

    [Fact]
    public void ADrawingThatCannotBeCutOut_IsWrittenAsThePicturesInIt()
    {
        // No page image: the cut cannot be made, so the picture that is in the drawing is saved and written in its place.
        using var dir = new TempDir();
        var path = dir.Combine("diagram.docx");
        DocxFactory.CreateWithShapes(path, [new DocPage(["diagram"], [.. TwoBoxes, DocShape.InParagraph("picture", 0.1, 0.05, 130, 20)])]);
        var pictures = new PictureCollector(cutShapes: true);
        var content = DocxReader.ReadContent(path, pictures: pictures);
        var crop = Assert.Single(Assert.Single(content.Shapes).Crops!);
        pictures.SetArea(crop.Mark, new ShapeArea(0.1, 0.1, 0.5, 0.2, 1));
        var blocks = content.Blocks.Select(b => b.Block).ToList();
        blocks.ForEach(b => b.PageIndex = 1);

        PictureFiles.Write(dir.Combine("out"), blocks, 1, pictures, pageImage: null);

        var markdown = string.Join("\n", blocks.Select(b => b.Markdown));
        Assert.Contains("pictures/page-001-1.", markdown, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(dir.Combine("out", PictureFiles.Folder)));
    }

    // ---- the setting, the record of a resume ----

    [Theory]
    [InlineData("1e400")]
    [InlineData("-5")]
    [InlineData("1000")]
    public void AGapOutOfTheRange_IsTheDefault_AndCanBeSavedAgain(string value)
    {
        var settings = JsonDefaults.Deserialize<AppSettings>("{\"schemaVersion\":1,\"input\":{\"shapeGroupGapPoints\":" + value + "}}")!;

        Assert.Equal(24, settings.Input.ShapeGroupGapPoints);
        var options = MarkPortO.Core.Convert.ConvertRequest.OptionsFrom(settings);
        Assert.Contains("\"shapeGroupGapPoints\": 24", JsonDefaults.Serialize(options), StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithStep1DoneWhoseFolderIsGone_IsNotTakenFromAnInterruptedJob()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["text"]);
        var output = dir.Combine("out");
        var job = FakeRecords.Write(output, [source]);
        var store = new StateStore(output);
        job.Status = JobStatus.Interrupted;
        store.SaveJob(job, touch: false);
        var document = store.LoadDocument(job.JobId, job.Documents[0])!;
        document.Step2 = StepStatus.Pending;
        store.SaveDocument(job.JobId, document);
        Directory.Delete(document.OutputFolder, recursive: true);

        Assert.Empty(FileStates.LastRecords(output));
    }

    // ---- with LibreOffice ----

    [LibreOfficeFact]
    public async Task AnXOnTheSecondPage_InAnEmptyParagraph_DeletesTheSecondPage_NotTheFirst()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "cross.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["the first page stays"]), new DocPage([""], DocShape.CrossShape(0.8)), new DocPage(["the third page"])]);
        var options = Options();
        options.ExtractShapes = true;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        var document = Assert.Single(result.Documents);
        Assert.Equal([2], document.Pages.Where(p => p.IsDeleted).Select(p => p.Index));
    }

    [LibreOfficeFact]
    public async Task ADrawingPlacedWhereNothingIsDrawn_IsNotCutOutAsAWhitePicture()
    {
        // The shapes have no line and no fill: nothing is drawn where the document places them, so no picture is made of them.
        using var dir = new TempDir();
        var source = dir.Combine("in", "blank.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["diagram"],
            [new DocShape("rect", 0.2, 0.1, XPoints: 72, YPoints: 300), new DocShape("rect", 0.2, 0.1, XPoints: 300, YPoints: 300)])]);
        Change(source, body =>
        {
            foreach (var line in body.Descendants<DocumentFormat.OpenXml.Drawing.Outline>().ToList())
            {
                line.RemoveAllChildren();
                line.AppendChild(new DocumentFormat.OpenXml.Drawing.NoFill());
            }
        });
        var options = Options();
        options.ExtractShapes = true;
        options.ExtractPictures = true;

        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));
        var output = Assert.Single(result.Documents).OutputFolder;
        var markdown = File.ReadAllText(Path.Combine(output, "blank.md"));

        Assert.False(Directory.Exists(Path.Combine(output, PictureFiles.Folder)) && Directory.GetFiles(Path.Combine(output, PictureFiles.Folder)).Length > 0);
        Assert.DoesNotMatch(@"pictures/page-\d{3}-\d+\.png", markdown);
    }
}
