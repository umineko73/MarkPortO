// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.Globalization;
using MarkPort.Core.Localization;
using Microsoft.Extensions.Logging;
using MarkPort.Core.Convert;
using MarkPort.Core.Engines;
using MarkPort.Core.IO;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Markdown;
using MarkPort.Core.Ooxml;
using MarkPort.Core.Pdf;

namespace MarkPort.Core.Pipeline;

/// <summary>
/// Step 1: turn the document into a PDF with the engine, save the page images, read the text, put the text on
/// the pages, and write the first Markdown (without image references, which step 2 adds).
/// </summary>
public sealed class Step1Worker : IPipelineStep<DocumentWork>
{
    public string Name => "step1";

    /// <summary>How many documents are converted at once. Set from the engine (Office: 1, LibreOffice: 2).</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>One page of a PDF that becomes one page of the output.</summary>
    private sealed record PdfPageRef(string PdfPath, int PageIndexInPdf);

    /// <summary>What a kind-specific reader found out.</summary>
    private sealed class Layout
    {
        public List<PdfPageRef> Pdf { get; } = [];

        /// <summary>The label of each page (same order as <see cref="Pdf"/>).</summary>
        public List<string> Labels { get; } = [];

        public List<SheetInfo?> Sheets { get; } = [];

        /// <summary>Blocks as 1-based output page numbers are not known for docx until the PDF text is read.</summary>
        public List<Block> Blocks { get; } = [];

        public List<Warning> Warnings { get; } = [];

        public IReadOnlyList<DocxBlock>? DocxBlocks { get; set; }

        /// <summary>1-based numbers of the output pages that are deleted pages (a "×" over the page, FR-012).</summary>
        public HashSet<int> DeletedPages { get; } = [];
    }

    public async Task ProcessAsync(DocumentWork work, CancellationToken cancellationToken)
    {
        var document = work.Document;
        var context = work.Context;

        // Finished earlier (a resumed run): the page images and text are kept as they are.
        if (document.Step1 is StepStatus.Done or StepStatus.Failed)
        {
            return;
        }

        document.Step1 = StepStatus.Running;
        document.Error = null;
        context.Save(document);

        var workDirectory = context.WorkDirectory(document);
        try
        {
            OoxmlPackage.EnsureOpens(document.SourcePath, document.Kind);
            if (!Directory.Exists(document.OutputFolder))
            {
                Directory.CreateDirectory(document.OutputFolder);
            }

            AtomicFile.DeleteStaleTemps(document.OutputFolder);
            Directory.CreateDirectory(workDirectory);

            var layout = document.Kind switch
            {
                DocumentKind.Docx => await LayoutDocxAsync(work, workDirectory, cancellationToken).ConfigureAwait(false),
                DocumentKind.Pptx => await LayoutPptxAsync(work, workDirectory, cancellationToken).ConfigureAwait(false),
                DocumentKind.Xlsx => await LayoutXlsxAsync(work, workDirectory, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(work)),
            };

            await BuildPagesAsync(document, layout, context.Options.Dpi, cancellationToken).ConfigureAwait(false);

            document.Step1 = StepStatus.Done;
            MarkdownRenderer.Write(document);
            context.Save(document);
        }
        catch (OperationCanceledException)
        {
            document.Step1 = StepStatus.Interrupted;
            context.Save(document);
            throw;
        }
        catch (Exception ex)
        {
            document.Step1 = StepStatus.Failed;
            document.Error = new DocumentError(1, ex.Message);
            context.Save(document);
            context.Logger.LogWarning("Step 1 failed for {Source}: {Message}", document.SourcePath, ex.Message);
            throw;
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    // ---- docx: one PDF, one page per PDF page, text placed by the PDF text ----

    private static async Task<Layout> LayoutDocxAsync(DocumentWork work, string workDirectory, CancellationToken cancellationToken)
    {
        var document = work.Document;
        var layout = new Layout();
        var content = DocxReader.ReadContent(document.SourcePath);
        var blocks = content.Blocks;

        var pdf = Path.Combine(workDirectory, "document.pdf");
        await ExportAsync(work, document.SourcePath, pdf, cancellationToken).ConfigureAwait(false);

        var pageTexts = PdfTextExtractor.GetPageTexts(pdf);
        layout.Warnings.AddRange(DocxPageAssigner.Assign(blocks, pageTexts));
        layout.DocxBlocks = blocks;
        layout.Blocks.AddRange(blocks.Select(b => b.Block));

        // A shape belongs to the page of the paragraph it is anchored in.
        var effective = DocxPageAssigner.EffectivePages(layout.Blocks, pageTexts.Count);
        var shapesByPage = new Dictionary<int, List<ShapeBox>>();
        foreach (var shapes in content.Shapes)
        {
            var page = shapes.HostBlockIndex >= 0 ? effective[shapes.HostBlockIndex] : 1;
            if (!shapesByPage.TryGetValue(page, out var list))
            {
                shapesByPage[page] = list = [];
            }

            list.AddRange(shapes.Boxes);
        }

        foreach (var (page, boxes) in shapesByPage)
        {
            MarkDeleted(layout, page, CrossShapeDetector.Evaluate(boxes), PageLabel.Page(page));
        }

        for (var i = 0; i < pageTexts.Count; i++)
        {
            layout.Pdf.Add(new PdfPageRef(pdf, i));
            layout.Labels.Add(PageLabel.Page(i + 1));
            layout.Sheets.Add(null);
        }

        return layout;
    }

    // ---- pptx: one PDF page per slide ----

    private static async Task<Layout> LayoutPptxAsync(DocumentWork work, string workDirectory, CancellationToken cancellationToken)
    {
        var document = work.Document;
        var includeHidden = work.Context.Options.IncludeHidden;
        var layout = new Layout();
        var slides = PptxReader.Read(document.SourcePath, includeHidden);
        if (slides.Count == 0)
        {
            layout.Warnings.Add(new Warning("EmptyDocument", Loc.T("The presentation has no slides to convert.")));
            return layout;
        }

        var source = document.SourcePath;
        if (includeHidden && slides.Any(s => s.Hidden))
        {
            source = Path.Combine(workDirectory, "slides.pptx");
            PptxTempCopy.CreateWithHiddenSlidesShown(document.SourcePath, source);
        }

        var pdf = Path.Combine(workDirectory, "document.pdf");
        await ExportAsync(work, source, pdf, cancellationToken).ConfigureAwait(false);

        var verdicts = PptxCrossExtractor.Evaluate(document.SourcePath);
        var pdfPages = (await PdfRenderer.GetPageCountAsync(pdf).ConfigureAwait(false));
        if (pdfPages != slides.Count)
        {
            layout.Warnings.Add(new Warning(
                "PageCountMismatch",
                Loc.T("The presentation has {0} slides to convert but the PDF has {1} pages.", slides.Count, pdfPages)));
        }

        for (var i = 0; i < Math.Min(pdfPages, slides.Count); i++)
        {
            layout.Pdf.Add(new PdfPageRef(pdf, i));
            layout.Labels.Add(PageLabel.Slide(slides[i].Number));
            layout.Sheets.Add(null);
            layout.Blocks.Add(new Block { Kind = BlockKind.SlideText, Markdown = slides[i].Markdown, PageIndex = i + 1 });
            if (verdicts.TryGetValue(slides[i].Number, out var verdict))
            {
                MarkDeleted(layout, i + 1, verdict, PageLabel.Slide(slides[i].Number));
            }
        }

        return layout;
    }

    // ---- xlsx: one temporary copy and one PDF per part of a sheet ----

    private static async Task<Layout> LayoutXlsxAsync(DocumentWork work, string workDirectory, CancellationToken cancellationToken)
    {
        var document = work.Document;
        var layout = new Layout();
        var sheets = XlsxReader.Read(document.SourcePath, work.Context.Options.IncludeHidden);
        var verdicts = XlsxCrossExtractor.Evaluate(document.SourcePath, sheets);

        var unitNumber = 0;
        foreach (var sheet in sheets)
        {
            foreach (var unit in ExcelUnitPlanner.Plan(sheet))
            {
                cancellationToken.ThrowIfCancellationRequested();
                unitNumber++;
                var copy = Path.Combine(workDirectory, $"unit{unitNumber}.xlsx");
                ExcelTempCopy.Create(document.SourcePath, copy, sheet, unit);
                var pdf = Path.Combine(workDirectory, $"unit{unitNumber}.pdf");
                await ExportAsync(work, copy, pdf, cancellationToken).ConfigureAwait(false);

                var pdfPages = await PdfRenderer.GetPageCountAsync(pdf).ConfigureAwait(false);
                var firstPageOfUnit = layout.Pdf.Count + 1;
                for (var i = 0; i < pdfPages; i++)
                {
                    layout.Pdf.Add(new PdfPageRef(pdf, i));
                    layout.Labels.Add(PageLabel.Sheet(sheet.Name));
                    layout.Sheets.Add(new SheetInfo(sheet.Name, unit.ColumnRange, unit.Part, unit.PartCount));
                    if (verdicts.TryGetValue(sheet.Name, out var verdict))
                    {
                        // A "×" over the sheet deletes every part of the sheet. The warning is given once per sheet.
                        MarkDeleted(layout, layout.Pdf.Count, verdict, PageLabel.Sheet(sheet.Name), warn: unit.Part == 1 && i == 0);
                    }
                }

                if (pdfPages > 0)
                {
                    // The table of the part goes with its first page; later pages of a long sheet carry only the image.
                    layout.Blocks.Add(new Block
                    {
                        Kind = BlockKind.SheetTable,
                        Markdown = sheet.ToMarkdown(unit.FromColumnIndex, unit.ToColumnIndex),
                        PageIndex = firstPageOfUnit,
                    });
                }

                if (unit.PartCount > 1 && unit.Part == 1)
                {
                    layout.Warnings.Add(new Warning(
                        "SheetSplit",
                        Loc.T("The sheet '{0}' has too many columns for one image and was split into {1} parts.", sheet.Name, unit.PartCount)));
                }
            }
        }

        if (layout.Pdf.Count == 0)
        {
            layout.Warnings.Add(new Warning("EmptyDocument", Loc.T("The workbook has no sheets with content to convert.")));
        }

        return layout;
    }

    // ---- common ----

    /// <summary>Records that a page is a deleted page, or warns about a drawing that cannot be judged.</summary>
    private static void MarkDeleted(Layout layout, int page, CrossVerdict verdict, string where, bool warn = true)
    {
        if (verdict.IsCross)
        {
            layout.DeletedPages.Add(page);
        }
        else if (verdict.HasUnsupportedLargeShape && warn)
        {
            layout.Warnings.Add(new Warning(
                "ShapeUnsupported",
                Loc.T("{0} has a freehand drawing over the whole page. If it is a \"×\" mark it cannot be recognised, so the page is converted as usual.", PageLabel.Text(where, CultureInfo.CurrentUICulture)),
                page));
        }
    }

    private static async Task ExportAsync(DocumentWork work, string source, string pdf, CancellationToken cancellationToken)
    {
        var kind = work.Document.Kind;
        await work.Context.Engine.ExportPdfAsync(
            new PdfExportRequest(source, kind, pdf, work.Context.EngineTimeout),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renders the page images and fills the pages and blocks of the document.</summary>
    private static async Task BuildPagesAsync(DocumentRecord document, Layout layout, int dpi, CancellationToken cancellationToken)
    {
        var total = layout.Pdf.Count;
        var pages = new List<PageRecord>(total);
        var opened = new Dictionary<string, Windows.Data.Pdf.PdfDocument>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = i + 1;
            var imageFile = MarkdownRenderer.ImageFileName(index, total);
            var page = new PageRecord
            {
                Index = index,
                SourceLabel = layout.Labels[i],
                ImageFile = imageFile,
                Sheet = layout.Sheets[i],
                IsDeleted = layout.DeletedPages.Contains(index),
            };
            if (page.IsDeleted)
            {
                page.Step3 = PageStep3Status.Skipped; // a deleted page is never read by the VLM
            }

            try
            {
                var reference = layout.Pdf[i];
                if (!opened.TryGetValue(reference.PdfPath, out var pdfDocument))
                {
                    pdfDocument = await PdfRenderer.OpenAsync(reference.PdfPath).ConfigureAwait(false);
                    opened[reference.PdfPath] = pdfDocument;
                }

                await PdfRenderer.RenderPageAsync(
                    pdfDocument,
                    reference.PageIndexInPdf,
                    dpi,
                    Path.Combine(document.OutputFolder, imageFile.Replace('/', Path.DirectorySeparatorChar)),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (PageTooLargeException ex)
            {
                // One page that is too large does not stop the document (FR-029): the page is kept without an image.
                page.ImageFile = string.Empty;
                page.Error = ex.Message;
                layout.Warnings.Add(new Warning("ImageFailed", ex.Message, index));
            }

            pages.Add(page);
        }

        // The text of a deleted page is not kept: it must not reach the Markdown (FR-012).
        var effective = DocxPageAssigner.EffectivePages(layout.Blocks, total);
        var kept = layout.Blocks.Where((_, i) => !layout.DeletedPages.Contains(effective[i])).ToList();

        // The note that a changed input made the file start over (FR-015) was added before this step; keep it.
        var carried = document.Warnings.Where(w => w.Code == Jobs.ResumePlanner.RestartedCode).ToList();

        document.Pages = pages;
        document.Blocks = kept;
        document.Warnings = [.. carried, .. layout.Warnings];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the state folder; it is removed with the job.
        }
    }
}
