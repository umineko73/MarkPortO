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

using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Pdf;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Engines;

[Trait("Engine", "LibreOffice")]
public class LibreOfficeEngineTests
{
    private static LibreOfficeEngine NewEngine(TempDir dir) =>
        new(profileRoot: dir.Combine("profiles"));

    [Fact]
    public void CheckAvailability_ExplainsWhenTheConfiguredPathIsWrong()
    {
        using var engine = new LibreOfficeEngine(@"C:\no\such\soffice.exe");

        var availability = engine.CheckAvailability();

        Assert.False(availability.IsAvailable);
        Assert.Contains(@"C:\no\such\soffice.exe", availability.Reason);
    }

    [LibreOfficeFact]
    public void CheckAvailability_ReportsAvailableWhenInstalled()
    {
        using var dir = new TempDir();
        using var engine = NewEngine(dir);

        Assert.True(engine.CheckAvailability().IsAvailable);
    }

    [LibreOfficeFact]
    public async Task ExportPdf_Docx_ProducesOnePdfPagePerPageBreak()
    {
        using var dir = new TempDir();
        var source = dir.Combine("入力 文書.docx"); // a Japanese name with a space
        DocxFactory.CreatePaged(source, ["P1-a", "P1-b"], ["P2-a"], ["P3-a"]);
        using var engine = NewEngine(dir);
        var pdf = dir.Combine("out", "result.pdf");

        await engine.ExportPdfAsync(new PdfExportRequest(source, DocumentKind.Docx, pdf, PdfExportRequest.DefaultTimeout), CancellationToken.None);

        Assert.True(File.Exists(pdf));
        var pages = PdfTextExtractor.GetPageTexts(pdf);
        Assert.Equal(3, pages.Count);
        Assert.Contains("P1-a", pages[0]);
        Assert.Contains("P2-a", pages[1]);
        Assert.Contains("P3-a", pages[2]);
        Assert.False(File.Exists(source + ".pdf")); // nothing is written next to the source
    }

    [LibreOfficeFact]
    public async Task ExportPdf_Pptx_ProducesOnePdfPagePerSlide()
    {
        using var dir = new TempDir();
        var source = dir.Combine("deck.pptx");
        PptxFactory.Create(source, new SlideSpec("First", ["one"]), new SlideSpec("Second", ["two"]), new SlideSpec("Hidden", ["secret"], Hidden: true));
        using var engine = NewEngine(dir);
        var pdf = dir.Combine("deck.pdf");

        await engine.ExportPdfAsync(new PdfExportRequest(source, DocumentKind.Pptx, pdf, PdfExportRequest.DefaultTimeout), CancellationToken.None);

        var pages = PdfTextExtractor.GetPageTexts(pdf);
        Assert.Equal(2, pages.Count); // the hidden slide is not exported
        Assert.Contains("First", pages[0]);
        Assert.Contains("Second", pages[1]);
    }

    [LibreOfficeFact]
    public async Task ExportPdf_Xlsx_ProducesAPdf()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Data", [["Name", "Qty"], ["Apple", "3"]]));
        using var engine = NewEngine(dir);
        var pdf = dir.Combine("book.pdf");

        await engine.ExportPdfAsync(new PdfExportRequest(source, DocumentKind.Xlsx, pdf, PdfExportRequest.DefaultTimeout), CancellationToken.None);

        var pages = PdfTextExtractor.GetPageTexts(pdf);
        Assert.NotEmpty(pages);
        Assert.Contains("Apple", string.Concat(pages));
    }

    [LibreOfficeFact]
    public async Task ExportPdf_DoesNotChangeTheSourceFile()
    {
        using var dir = new TempDir();
        var source = dir.Combine("a.docx");
        DocxFactory.CreatePaged(source, ["x"]);
        var before = StateStoreFingerprint(source);
        using var engine = NewEngine(dir);

        await engine.ExportPdfAsync(new PdfExportRequest(source, DocumentKind.Docx, dir.Combine("a.pdf"), PdfExportRequest.DefaultTimeout), CancellationToken.None);

        Assert.Equal(before, StateStoreFingerprint(source));
    }

    // A damaged file is NOT rejected by LibreOffice: a file named .docx whose content is not a document is
    // converted as plain text and "succeeds". That is why step 1 checks that the file opens as an OOXML
    // package before it calls an engine (see ErrorCasesTests and Decision 001). This test documents it.
    [LibreOfficeFact]
    public async Task ExportPdf_NonDocumentFile_IsConvertedAsText_SoTheCallerMustValidateFirst()
    {
        using var dir = new TempDir();
        var source = dir.Combine("broken.docx");
        DocxFactory.CreateCorrupt(source);
        using var engine = NewEngine(dir);

        await engine.ExportPdfAsync(new PdfExportRequest(source, DocumentKind.Docx, dir.Combine("broken.pdf"), PdfExportRequest.DefaultTimeout), CancellationToken.None);

        Assert.True(File.Exists(dir.Combine("broken.pdf")));
    }

    [LibreOfficeFact]
    public async Task ExportPdf_WhenTheTimeoutPasses_StopsLibreOfficeAndReportsATimeout()
    {
        using var dir = new TempDir();
        var source = dir.Combine("a.docx");
        DocxFactory.CreatePaged(source, ["x"]);
        using var engine = NewEngine(dir);

        var error = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExportPdfAsync(new PdfExportRequest(source, DocumentKind.Docx, dir.Combine("a.pdf"), TimeSpan.FromMilliseconds(50)), CancellationToken.None));

        Assert.True(error.TimedOut);
    }

    [LibreOfficeFact]
    public async Task ExportPdf_MissingSource_FailsClearly()
    {
        using var dir = new TempDir();
        using var engine = NewEngine(dir);

        var error = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExportPdfAsync(new PdfExportRequest(dir.Combine("none.docx"), DocumentKind.Docx, dir.Combine("o.pdf"), PdfExportRequest.DefaultTimeout), CancellationToken.None));

        Assert.Contains("not found", error.Message);
    }

    [LibreOfficeFact]
    public async Task ExportPdf_TwoConversionsAtOnce_BothSucceed()
    {
        using var dir = new TempDir();
        var a = dir.Combine("a.docx");
        var b = dir.Combine("b.docx");
        DocxFactory.CreatePaged(a, ["alpha-text"]);
        DocxFactory.CreatePaged(b, ["bravo-text"]);
        using var engine = new LibreOfficeEngine(profileRoot: dir.Combine("profiles"), maxParallel: 2);

        await Task.WhenAll(
            engine.ExportPdfAsync(new PdfExportRequest(a, DocumentKind.Docx, dir.Combine("a.pdf"), PdfExportRequest.DefaultTimeout), CancellationToken.None),
            engine.ExportPdfAsync(new PdfExportRequest(b, DocumentKind.Docx, dir.Combine("b.pdf"), PdfExportRequest.DefaultTimeout), CancellationToken.None));

        Assert.Contains("alpha-text", string.Concat(PdfTextExtractor.GetPageTexts(dir.Combine("a.pdf"))));
        Assert.Contains("bravo-text", string.Concat(PdfTextExtractor.GetPageTexts(dir.Combine("b.pdf"))));
    }

    private static (long Size, long Ticks) StateStoreFingerprint(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }
}
