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
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Tests.Fixtures;
using W = DocumentFormat.OpenXml.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MarkPortO.Core.Tests.Ooxml;

/// <summary>The pictures of a docx or pptx are saved in pictures/ and the Markdown refers to them (FR-133).</summary>
public class PictureExtractionTests
{
    /// <summary>A red pixel and a blue pixel: two different PNG files.</summary>
    public static readonly byte[] RedPng = System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static readonly byte[] BluePng = System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static readonly byte[] SomeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    private static readonly byte[] SomeEmf = [0x01, 0x00, 0x00, 0x00, 0x6C, 0x00, 0x00, 0x00];

    private const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" "
        + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" "
        + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    /// <summary>A Word picture: in the line, or floating (anchored) in the paragraph.</summary>
    public static W.Drawing WordPicture(string relationshipId, string? description, bool floating = false, uint id = 1)
    {
        var docPr = $"<wp:docPr id=\"{id}\" name=\"Picture {id}\"{(description is null ? string.Empty : $" descr=\"{description}\"")}/>";
        var graphic = "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"p\"/><pic:cNvPicPr/></pic:nvPicPr>"
            + $"<pic:blipFill><a:blip r:embed=\"{relationshipId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
            + "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"952500\" cy=\"952500\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic>";
        var extent = "<wp:extent cx=\"952500\" cy=\"952500\"/>";
        var body = floating
            ? "<wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"1\" behindDoc=\"0\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">"
              + "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>"
              + extent + "<wp:wrapSquare wrapText=\"bothSides\"/>" + docPr + graphic + "</wp:anchor>"
            : "<wp:inline>" + extent + docPr + graphic + "</wp:inline>";
        return new W.Drawing($"<w:drawing {Namespaces}>{body}</w:drawing>");
    }

    public static string AddImage(OpenXmlPartContainer owner, PartTypeInfo type, byte[] data)
    {
        ImagePart part = owner switch
        {
            MainDocumentPart main => main.AddImagePart(type),
            SlidePart slide => slide.AddImagePart(type),
            DrawingsPart drawings => drawings.AddImagePart(type),
            _ => throw new ArgumentException("no images", nameof(owner)),
        };
        using (var stream = new MemoryStream(data))
        {
            part.FeedData(stream);
        }

        return ((OpenXmlPart)owner).GetIdOfPart(part);
    }

    private static W.Paragraph Paragraph(string text, params W.Drawing[] pictures) =>
        new([new W.Run(new W.Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }), .. pictures.Select(p => new W.Run(p))]);

    // Page 1: a paragraph with a picture in the line. Page 2: a floating JPEG without a text, a table with a floating picture in a cell, the first
    // picture again. Page 3: an EMF.
    private static void CreateDocument(string path)
    {
        using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());
        var body = main.Document.Body!;
        var red = AddImage(main, ImagePartType.Png, RedPng);
        var jpeg = AddImage(main, ImagePartType.Jpeg, SomeJpeg);
        var blue = AddImage(main, ImagePartType.Png, BluePng);
        var emf = AddImage(main, ImagePartType.Emf, SomeEmf);
        var redAgain = AddImage(main, ImagePartType.Png, RedPng);

        body.Append(Paragraph("First ", WordPicture(red, "Company logo", id: 1)));
        body.Append(Paragraph("Second", WordPicture(jpeg, null, floating: true, id: 2)));
        body.Append(new W.Table(new W.TableRow(
            new W.TableCell(Paragraph("cell", WordPicture(blue, "Blue", floating: true, id: 3))),
            new W.TableCell(Paragraph("other")))));
        body.Append(Paragraph("Again ", WordPicture(redAgain, null, id: 4)));
        body.Append(Paragraph("Third ", WordPicture(emf, "Diagram", id: 5)));
        main.Document.Save();
    }

    [Fact]
    public void Docx_PicturesAreSavedPerPage_TheSamePictureIsOneFile_AndAnEmfIsNotSaved()
    {
        using var dir = new TempDir();
        var path = dir.Combine("pictures.docx");
        CreateDocument(path);
        var collector = new PictureCollector();

        var blocks = DocxReader.ReadContent(path, pictures: collector).Blocks.Select(b => b.Block).ToList();
        // The pages are put by hand: the first paragraph on page 1, the EMF on page 3, the rest on page 2.
        for (var i = 0; i < blocks.Count; i++)
        {
            blocks[i].PageIndex = i == 0 ? 1 : i == blocks.Count - 1 ? 3 : 2;
        }

        var log = PictureFiles.Write(dir.Path, blocks, 3, collector);
        var markdown = string.Join("\n\n", blocks.Select(b => b.Markdown));

        Assert.Equal("First ![Company logo](pictures/page-001-1.png)", blocks[0].Markdown);
        // A floating picture follows its paragraph as a line of its own; in a cell it stays in the cell.
        Assert.Contains("Second\n\n![](pictures/page-002-1.jpg)", markdown, StringComparison.Ordinal);
        Assert.Contains("| cell![Blue](pictures/page-002-2.png) | other |", markdown, StringComparison.Ordinal);
        // The same picture again is the file of the first page.
        Assert.Contains("Again ![](pictures/page-001-1.png)", markdown, StringComparison.Ordinal);
        // An EMF is written by its text only, and the log says why.
        Assert.Contains("Third ![Diagram]", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Diagram](", markdown, StringComparison.Ordinal);
        Assert.Contains(collector.Skipped, s => s.Contains("image/x-emf", StringComparison.Ordinal));

        Assert.Equal(["page-001-1.png", "page-002-1.jpg", "page-002-2.png"], Directory.GetFiles(dir.Combine("pictures")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(RedPng, File.ReadAllBytes(dir.Combine("pictures", "page-001-1.png")));
        Assert.Equal(BluePng, File.ReadAllBytes(dir.Combine("pictures", "page-002-2.png")));
        Assert.Equal(3, log.Count);
        Assert.DoesNotContain(blocks, b => b.Markdown.Contains(PictureCollector.MarkStart, StringComparison.Ordinal));
    }

    [Fact]
    public void Docx_WithoutTheCollector_APictureIsItsAlternativeTextOnly()
    {
        using var dir = new TempDir();
        var path = dir.Combine("pictures.docx");
        CreateDocument(path);

        var markdown = string.Join("\n\n", DocxReader.ReadContent(path).Blocks.Select(b => b.Block.Markdown));

        Assert.Contains("First ![Company logo]", markdown, StringComparison.Ordinal);
        Assert.Contains("Third ![Diagram]", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("](", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("![]", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pptx_PicturesOfTheSlideAndOfAGroupAreSaved_AndAVideoAndAnEmfAreNot()
    {
        using var dir = new TempDir();
        var path = dir.Combine("pictures.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan", ["point"]));
        using (var document = PresentationDocument.Open(path, isEditable: true))
        {
            var slidePart = document.PresentationPart!.SlideParts.First();
            var tree = slidePart.Slide!.CommonSlideData!.ShapeTree!;
            var red = AddImage(slidePart, ImagePartType.Png, RedPng);
            var blue = AddImage(slidePart, ImagePartType.Png, BluePng);
            var emf = AddImage(slidePart, ImagePartType.Emf, SomeEmf);
            tree.AppendChild(SlidePicture(90, red, "Company logo"));
            tree.AppendChild(new P.GroupShape(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 91, Name = "Group" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(),
                SlidePicture(92, blue, null)));
            tree.AppendChild(SlidePicture(93, emf, null));
            var video = SlidePicture(94, red, null);
            video.NonVisualPictureProperties!.ApplicationNonVisualDrawingProperties!.AppendChild(new DocumentFormat.OpenXml.Drawing.VideoFromFile { Link = red });
            tree.AppendChild(video);
            slidePart.Slide.Save();
        }

        var collector = new PictureCollector();
        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false, pictures: collector));
        var blocks = new List<Block> { new() { Kind = BlockKind.SlideText, Markdown = slide.Markdown, PageIndex = 1 } };
        PictureFiles.Write(dir.Path, blocks, 1, collector);

        Assert.Contains("- point\n\n![Company logo](pictures/page-001-1.png)\n\n![](pictures/page-001-2.png)", blocks[0].Markdown, StringComparison.Ordinal);
        Assert.Equal(["page-001-1.png", "page-001-2.png"], Directory.GetFiles(dir.Combine("pictures")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains(collector.Skipped, s => s.Contains("image/x-emf", StringComparison.Ordinal));
        Assert.DoesNotContain("page-001-3", blocks[0].Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Pptx_PicturesAndShapesBothOn_APictureInACutGroupIsNotWrittenTwice()
    {
        using var dir = new TempDir();
        var path = dir.Combine("both.pptx");
        PptxFactory.Create(path, new SlideSpec("Plan"));
        const long inch = 914400;
        using (var document = PresentationDocument.Open(path, isEditable: true))
        {
            var slidePart = document.PresentationPart!.SlideParts.First();
            var tree = slidePart.Slide!.CommonSlideData!.ShapeTree!;
            var red = AddImage(slidePart, ImagePartType.Png, RedPng);
            var blue = AddImage(slidePart, ImagePartType.Png, BluePng);
            tree.AppendChild(SlidePicture(90, red, "Logo")); // alone on the slide
            tree.AppendChild(new P.GroupShape(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 91, Name = "Group" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new DocumentFormat.OpenXml.Drawing.TransformGroup(
                    new DocumentFormat.OpenXml.Drawing.Offset { X = inch, Y = inch },
                    new DocumentFormat.OpenXml.Drawing.Extents { Cx = 3 * inch, Cy = 2 * inch })),
                SlidePicture(92, blue, "In the group")));
            slidePart.Slide.Save();
        }

        var collector = new PictureCollector(savePictures: true, cutShapes: true);
        var slide = Assert.Single(PptxReader.Read(path, includeHidden: false, pictures: collector));

        // The logo is a file; the group is one shape picture that holds the other picture, which is not a file of its own.
        Assert.Equal(2, collector.Pictures.Count);
        Assert.Single(collector.Pictures, p => p.IsShape);
        Assert.Contains("Logo", slide.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("In the group", slide.Markdown, StringComparison.Ordinal);
    }

    private static P.Picture SlidePicture(uint id, string relationshipId, string? description) =>
        new(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Picture " + id, Description = description },
                new P.NonVisualPictureDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new DocumentFormat.OpenXml.Drawing.Blip { Embed = relationshipId }, new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle())),
            new P.ShapeProperties());

    [Theory]
    [InlineData("See ![Logo](pictures/page-001-1.png) here", "See ![Logo] here")]
    [InlineData("![](pictures/page-002-1.jpg)", "")]
    [InlineData("![A \\] b](pictures/page-010-2.gif)", "![A \\] b]")]
    [InlineData("![Page 1](images/page-001.png)", "![Page 1](images/page-001.png)")]
    public void TheTextSentToTheVlm_HasNoFilesOfPictures(string markdown, string expected)
    {
        Assert.Equal(expected, PictureFiles.WithoutFiles(markdown));
    }

    [Fact]
    public void TheNameOfAPicture_HasTheDigitsOfThePageImages()
    {
        Assert.Equal("pictures/page-003-1.png", MarkdownRenderer.PictureFileName(3, 12, 1, ".png"));
        Assert.Equal("pictures/page-0012-10.jpg", MarkdownRenderer.PictureFileName(12, 1200, 10, ".jpg"));
    }

    /// <summary>An EMF of 2 x 1 inches: its left half is red, its right half is not drawn (white in the PNG).</summary>
    public static byte[] RedHalfEmf()
    {
        using var stream = new MemoryStream();
        using (var reference = new System.Drawing.Bitmap(1, 1))
        using (var referenceGraphics = System.Drawing.Graphics.FromImage(reference))
        {
            var hdc = referenceGraphics.GetHdc();
            try
            {
                // 2 x 1 inches, in hundredths of a millimetre.
                using var metafile = new System.Drawing.Imaging.Metafile(stream, hdc, new System.Drawing.RectangleF(0, 0, 5080, 2540), System.Drawing.Imaging.MetafileFrameUnit.GdiCompatible, System.Drawing.Imaging.EmfType.EmfOnly);
                using var graphics = System.Drawing.Graphics.FromImage(metafile);
                graphics.PageUnit = System.Drawing.GraphicsUnit.Millimeter;
                graphics.FillRectangle(System.Drawing.Brushes.Red, 0, 0, 25.4f, 25.4f);
            }
            finally
            {
                referenceGraphics.ReleaseHdc(hdc);
            }
        }

        return stream.ToArray();
    }

    // FR-134: an EMF is drawn at the DPI on white; a BMP is written again as a PNG.
    [Fact]
    public void AnEmf_IsDrawnIntoAPng_AtTheDpi_OnWhite()
    {
        var png = PictureConverter.ToPng(RedHalfEmf(), "image/x-emf", 100);

        using var image = new System.Drawing.Bitmap(new MemoryStream(png));
        Assert.InRange(image.Width, 190, 210); // 2 inches at 100 dpi
        Assert.InRange(image.Height, 90, 110);
        var left = image.GetPixel(image.Width / 4, image.Height / 2);
        var right = image.GetPixel(image.Width * 3 / 4, image.Height / 2);
        Assert.True(left.R > 200 && left.G < 60 && left.B < 60, $"left is {left}");
        Assert.True(right.R > 240 && right.G > 240 && right.B > 240, $"right is {right}");
    }

    [Fact]
    public void ABmp_IsWrittenAgainAsAPng_AndABrokenMetafileThrows()
    {
        using var bitmap = new System.Drawing.Bitmap(3, 2);
        using var bmp = new MemoryStream();
        bitmap.Save(bmp, System.Drawing.Imaging.ImageFormat.Bmp);

        var png = PictureConverter.ToPng(bmp.ToArray(), "image/bmp", 150);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.ThrowsAny<Exception>(() => PictureConverter.ToPng(SomeEmf, "image/x-emf", 150));
        Assert.False(PictureConverter.CanConvert("image/vnd.ms-photo"));
    }

    [Fact]
    public void Docx_AnEmfIsSavedAsAPng_AndTheLogSaysSo()
    {
        using var dir = new TempDir();
        var path = dir.Combine("emf.docx");
        using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var emf = AddImage(main, ImagePartType.Emf, RedHalfEmf());
            main.Document.Body!.Append(Paragraph("Figure ", WordPicture(emf, "Flow")));
            main.Document.Save();
        }

        var collector = new PictureCollector(100);
        var blocks = DocxReader.ReadContent(path, pictures: collector).Blocks.Select(b => b.Block).ToList();
        blocks.ForEach(b => b.PageIndex = 1);
        var log = PictureFiles.Write(dir.Path, blocks, 1, collector);

        Assert.Equal("Figure ![Flow](pictures/page-001-1.png)", blocks[0].Markdown);
        Assert.Empty(collector.Skipped);
        Assert.Contains("turned into a PNG from image/x-emf", Assert.Single(log), StringComparison.Ordinal);
        using var image = new System.Drawing.Bitmap(dir.Combine("pictures", "page-001-1.png"));
        Assert.InRange(image.Width, 190, 210);
    }

    // FR-135: a picture of a sheet comes before the first piece that starts below its row; a column outside the used range is that of its nearest part.
    [Fact]
    public void Xlsx_APictureComesBeforeTheFirstPieceBelowItsRow()
    {
        using var dir = new TempDir();
        var path = dir.Combine("pictures.xlsx");
        XlsxFactory.Create(path, new SheetSpec("Data",
        [
            ["Name", "Qty", "Unit"], ["Apple", "3", "box"], ["Pear", "5", "box"], ["Plum", "7", "bag"], ["Fig", "9", "bag"],
            [], [], ["Note: the numbers are boxes."],
        ]));
        using (var document = SpreadsheetDocument.Open(path, isEditable: true))
        {
            var sheetPart = document.WorkbookPart!.WorksheetParts.First();
            var drawings = sheetPart.DrawingsPart ?? sheetPart.AddNewPart<DrawingsPart>();
            var red = AddImage(drawings, ImagePartType.Png, RedPng);
            var blue = AddImage(drawings, ImagePartType.Png, BluePng);
            drawings.WorksheetDrawing = new DocumentFormat.OpenXml.Drawing.Spreadsheet.WorksheetDrawing(
                "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + SheetAnchor(column: 0, row: 9, id: 3, blue, null) // A10: below the note
                + SheetAnchor(column: 4, row: 1, id: 2, red, "Logo") // E2: beside the table, outside the used range
                + "</xdr:wsDr>");
            drawings.WorksheetDrawing.Save();
        }

        var collector = new PictureCollector();
        var sheet = Assert.Single(XlsxReader.Read(path, includeHidden: false, pictures: collector));
        var parts = sheet.ToMarkdownParts(0, sheet.ColumnCount - 1, null).Select(p => p.Markdown).ToList();

        Assert.Equal(4, parts.Count);
        Assert.StartsWith("|", parts[0], StringComparison.Ordinal);
        Assert.StartsWith("![Logo](" + PictureCollector.MarkStart, parts[1], StringComparison.Ordinal);
        Assert.Equal("Note: the numbers are boxes.", parts[2]);
        Assert.StartsWith("![](" + PictureCollector.MarkStart, parts[3], StringComparison.Ordinal);

        // Without the collector, the sheet is as before.
        Assert.Equal(2, Assert.Single(XlsxReader.Read(path, includeHidden: false)).ToMarkdownParts(0, sheet.ColumnCount - 1, null).Count);
    }

    private static string SheetAnchor(int column, int row, int id, string relationshipId, string? description) =>
        $"<xdr:oneCellAnchor><xdr:from><xdr:col>{column}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{row}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx=\"952500\" cy=\"952500\"/>"
        + $"<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"{id}\" name=\"Picture {id}\"{(description is null ? string.Empty : $" descr=\"{description}\"")}/><xdr:cNvPicPr/></xdr:nvPicPr>"
        + $"<xdr:blipFill><a:blip r:embed=\"{relationshipId}\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>"
        + "<xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:oneCellAnchor>";

    [Fact]
    public void Clear_RemovesThePicturesOfAnEarlierConversion()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("pictures"));
        File.WriteAllBytes(dir.Combine("pictures", "page-001-1.png"), RedPng);

        PictureFiles.Clear(dir.Path);
        PictureFiles.Clear(dir.Path); // nothing to remove

        Assert.False(Directory.Exists(dir.Combine("pictures")));
    }
}
