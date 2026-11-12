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

using MarkPortO.Core.Convert;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Convert;

[Trait("Engine", "LibreOffice")]
public class ConvertSingleFileTests
{
    [LibreOfficeFact]
    public async Task Docx_MakesTheDocumentFolderWithMarkdownAndOneImagePerPage()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "報告書 2026.docx"); // Japanese name with a space
        DocxFactory.CreatePaged(source, ["First page heading", "First page text"], ["Second page text"], ["Third page text"]);
        var out1 = dir.Combine("out");

        var result = await RunAsync(dir, Request(out1, Options(), source));

        Assert.Equal(JobStatus.Completed, result.Job.Status);
        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Equal(Path.Combine(out1, "報告書 2026"), document.OutputFolder);

        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "報告書 2026.md"));
        var images = ImageFiles(document.OutputFolder);
        Assert.Equal(3, images.Length); // SC-002: one image per page
        Assert.Equal(["page-001.png", "page-002.png", "page-003.png"], images.Select(Path.GetFileName));
        Assert.All(images, image => Assert.True(PdfFactory.ReadPngSize(image).Width > 100));

        // The structure note, then each page: marker, image, text.
        Assert.StartsWith("# 報告書 2026.docx", markdown);
        Assert.Contains("> **このMarkdownの構造**", markdown);
        Assert.Contains("<!-- page: 1 -->\n\n![ページ 1](images/page-001.png)\n\nFirst page heading\n\nFirst page text", markdown);
        Assert.Contains("<!-- page: 2 -->\n\n![ページ 2](images/page-002.png)\n\nSecond page text", markdown);
        Assert.Contains("<!-- page: 3 -->\n\n![ページ 3](images/page-003.png)\n\nThird page text", markdown);
    }

    // FR-133: with the option the pictures are saved in pictures/ under the page they are on, and the Markdown refers to them.
    [LibreOfficeFact]
    public async Task Docx_WithPictures_SavesThemUnderTheirPage_AndTheMarkdownRefersToThem()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "pictures.docx");
        DocxFactory.CreatePaged(source, ["First page text"], ["Second page text"]);
        using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(source, isEditable: true))
        {
            var main = document.MainDocumentPart!;
            var red = Ooxml.PictureExtractionTests.AddImage(main, DocumentFormat.OpenXml.Packaging.ImagePartType.Png, Ooxml.PictureExtractionTests.RedPng);
            var second = main.Document!.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().First(p => p.InnerText == "Second page text");
            second.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Run(Ooxml.PictureExtractionTests.WordPicture(red, "Company logo")));
            main.Document.Save();
        }

        var options = Options();
        options.ExtractPictures = true;
        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        var converted = Assert.Single(result.Documents);
        Assert.True(converted.IsDone());
        Assert.Equal(Ooxml.PictureExtractionTests.RedPng, File.ReadAllBytes(Path.Combine(converted.OutputFolder, "pictures", "page-002-1.png")));
        var markdown = File.ReadAllText(Path.Combine(converted.OutputFolder, "pictures.md"));
        Assert.Contains("Second page text![Company logo](pictures/page-002-1.png)", markdown);
        Assert.Contains("pictures/page-NNN-K.png", markdown); // the structure note
    }

    // FR-134, FR-135: a picture of a sheet is saved under the page of its row; an EMF becomes a PNG.
    [LibreOfficeFact]
    public async Task Xlsx_WithPictures_SavesThePicturesOfTheSheet_AndAnEmfAsAPng()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Data", [["Name", "Qty"], ["Apple", "3"], ["Pear", "5"]]));
        using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(source, isEditable: true))
        {
            var sheetPart = document.WorkbookPart!.WorksheetParts.First();
            var drawings = sheetPart.AddNewPart<DocumentFormat.OpenXml.Packaging.DrawingsPart>();
            var emf = Ooxml.PictureExtractionTests.AddImage(drawings, DocumentFormat.OpenXml.Packaging.ImagePartType.Emf, Ooxml.PictureExtractionTests.RedHalfEmf());
            drawings.WorksheetDrawing = new DocumentFormat.OpenXml.Drawing.Spreadsheet.WorksheetDrawing(
                "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
                + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + $"<xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>4</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx=\"1828800\" cy=\"914400\"/>"
                + $"<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\" descr=\"Flow\"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed=\"{emf}\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>"
                + "<xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>");
            drawings.WorksheetDrawing.Save();
            sheetPart.Worksheet!.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Drawing { Id = sheetPart.GetIdOfPart(drawings) });
            sheetPart.Worksheet.Save();
        }

        var options = Options();
        options.ExtractPictures = true;
        var result = await RunAsync(dir, Request(dir.Combine("out"), options, source));

        var converted = Assert.Single(result.Documents);
        Assert.True(converted.IsDone());
        Assert.True(File.Exists(Path.Combine(converted.OutputFolder, "pictures", "page-001-1.png")));
        var markdown = File.ReadAllText(Path.Combine(converted.OutputFolder, "book.md"));
        Assert.Matches(@"\| Pear \| 5 \|\n\n!\[Flow\]\(pictures/page-001-1\.png\)", markdown);
        Assert.Contains("turned into a PNG from image/x-emf", File.ReadAllText(Path.Combine(converted.OutputFolder, "book.log")));
    }

    [LibreOfficeFact]
    public async Task Pptx_MakesOneImagePerVisibleSlide_AndSkipsHiddenSlides()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "deck.pptx");
        PptxFactory.Create(
            source,
            new SlideSpec("Plan", ["first point", "second point"]),
            new SlideSpec("Hidden slide", ["secret"], Hidden: true),
            new SlideSpec("Results", Table: [["A", "B"], ["1", "2"]]));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Equal(2, ImageFiles(document.OutputFolder).Length);
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "deck.md"));
        Assert.Contains("<!-- page: 1 -->\n\n![ページ 1](images/page-001.png)\n\n## Plan\n\n- first point\n- second point", markdown);
        Assert.Contains("<!-- page: 2 -->", markdown);
        Assert.Contains("## Results", markdown);
        Assert.Contains("| A | B |", markdown);
        Assert.DoesNotContain("secret", markdown);
        Assert.Equal(["slide:1", "slide:3"], document.Pages.Select(p => p.SourceLabel));
    }

    [LibreOfficeFact]
    public async Task Xlsx_PutsAllColumnsOfAnOrdinarySheetOnOneImage_AndSplitsAnExtremelyWideOne()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "book.xlsx");
        XlsxFactory.Create(
            source,
            new SheetSpec("Data", [["Name", "Qty"], ["Apple", "3"]], PrintArea: "A1:A1"), // the print area is ignored
            XlsxFactory.WideSheet("Wide", columns: 160, rows: 4, columnWidthChars: 14));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());

        // Data: one image. Wide: three parts (A:BZ, CA:EZ, FA:FD), each one image.
        Assert.Equal(4, document.Pages.Count);
        Assert.Equal(4, ImageFiles(document.OutputFolder).Length);
        Assert.Equal(
            [("Data", "A:B", 1, 1), ("Wide", "A:BZ", 1, 3), ("Wide", "CA:EZ", 2, 3), ("Wide", "FA:FD", 3, 3)],
            document.Pages.Select(p => (p.Sheet!.Name, p.Sheet.ColumnRange, p.Sheet.Part, p.Sheet.PartCount)));

        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "book.md"));
        Assert.Contains("<!-- sheet: Data part=1/1 columns=A:B -->", markdown);
        Assert.Contains("<!-- sheet: Wide part=2/3 columns=CA:EZ -->", markdown);
        Assert.Contains("| Name | Qty |", markdown);
        Assert.Contains("| Apple | 3 |", markdown); // the author's print area A1:A1 did not cut the data
        Assert.Contains("SheetSplit", document.Warnings.Select(w => w.Code));
        Assert.DoesNotContain("H79", markdown.Split("part=1/3")[1].Split("part=2/3")[0]); // part 1 has no column of part 2
    }

    [LibreOfficeFact]
    public async Task WithoutStep2_ImagesAreSavedButTheMarkdownHasNoImageReferences()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["page one text"], ["page two text"]);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(step2: false), source));

        var document = Assert.Single(result.Documents);
        Assert.Equal(StepStatus.NotApplicable, document.Step2);
        Assert.Equal(2, ImageFiles(document.OutputFolder).Length);
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "a.md"));
        Assert.Contains("page one text", markdown);
        Assert.DoesNotContain("](images/", markdown);
    }

    [LibreOfficeFact]
    public async Task TheSourceFileIsNeverChanged()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["text"]);
        var bytes = File.ReadAllBytes(source);
        var time = File.GetLastWriteTimeUtc(source);

        await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(time, File.GetLastWriteTimeUtc(source));
        Assert.Equal([source], Directory.GetFiles(dir.Combine("in"))); // nothing else appears next to it
    }

    [LibreOfficeFact]
    public async Task ConvertingTheSameFileAgain_NeverOverwritesTheFirstOutput()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["text"]);
        var outFolder = dir.Combine("out");

        var first = await RunAsync(dir, Request(outFolder, Options(), source));
        var firstMarkdown = File.ReadAllText(Path.Combine(first.Documents[0].OutputFolder, "a.md"));
        var second = await RunAsync(dir, Request(outFolder, Options(), source));

        Assert.Equal(Path.Combine(outFolder, "a"), first.Documents[0].OutputFolder);
        Assert.Equal(Path.Combine(outFolder, "a (2)"), second.Documents[0].OutputFolder);
        Assert.Equal(firstMarkdown, File.ReadAllText(Path.Combine(first.Documents[0].OutputFolder, "a.md")));
        Assert.True(File.Exists(Path.Combine(outFolder, "a (2)", "a.md"))); // the file inside is named after the document, not the folder
    }

    [LibreOfficeFact]
    public async Task TheStateIsSavedInTheJobFolder()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["text"]);
        var outFolder = dir.Combine("out");

        var result = await RunAsync(dir, Request(outFolder, Options(), source));

        var store = new StateStore(outFolder);
        var job = store.LoadJob(result.Job.JobId)!;
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal([source], job.Inputs.Select(p => p).ToArray());
        var saved = store.LoadDocument(job.JobId, "d0001")!;
        Assert.Equal(StepStatus.Done, saved.Step1);
        Assert.Equal(StepStatus.Done, saved.Step2);
        Assert.Single(saved.Pages);
        Assert.False(Directory.Exists(store.WorkDirectory(job.JobId, "d0001"))); // scratch files are removed
    }

    [LibreOfficeFact]
    public async Task ReportsEachDocumentChange()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["text"]);
        var changes = new List<(StepStatus One, StepStatus Two)>();
        using var engine = Engine(dir);
        var service = new ConvertService(engine);
        service.DocumentChanged += d => { lock (changes) { changes.Add((d.Step1, d.Step2)); } };

        await service.RunAsync(Request(dir.Combine("out"), Options(), source));

        Assert.NotEmpty(changes);
        Assert.Contains(changes, c => c.One == StepStatus.Done && c.Two == StepStatus.Done);
    }
}
