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

using System.Globalization;
using MarkPortO.Core.Localization;
using Microsoft.Extensions.Logging;
using MarkPortO.Core.Convert;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Extraction;
using MarkPortO.Core.IO;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Pdf;
using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Pipeline;

/// <summary>
/// Step 1: turn the document into a PDF with the engine, save the page images, read the text, put the text on
/// the pages, and write the first Markdown (without image references, which step 2 adds).
/// </summary>
public sealed class Step1Worker : IPipelineStep<DocumentWork>
{
    public string Name => "step1";

    /// <summary>How many documents are converted at once. Set from the engine (Office: 1, LibreOffice: 2).</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>The DPI of the image of a wide sheet is raised up to this (FR-079); a DPI that is set higher is kept.</summary>
    private const int MaxRaisedDpi = 600;

    /// <summary>One page of a PDF that becomes one page of the output.</summary>
    private sealed record PdfPageRef(string PdfPath, int PageIndexInPdf);

    /// <summary>What a kind-specific reader found out.</summary>
    private sealed class Layout
    {
        public List<PdfPageRef> Pdf { get; } = [];

        /// <summary>The label of each page (same order as <see cref="Pdf"/>).</summary>
        public List<string> Labels { get; } = [];

        public List<SheetInfo?> Sheets { get; } = [];

        /// <summary>How many times the DPI is raised for a page (0-based index in <see cref="Pdf"/>); a page that is not in it has the DPI as it is.</summary>
        public Dictionary<int, double> DpiFactors { get; } = [];

        /// <summary>Blocks as 1-based output page numbers are not known for docx until the PDF text is read.</summary>
        public List<Block> Blocks { get; } = [];

        public List<Warning> Warnings { get; } = [];

        public IReadOnlyList<DocxBlock>? DocxBlocks { get; set; }

        /// <summary>Collects the pictures of a docx, pptx or xlsx (FR-133, FR-135); null when they are not saved.</summary>
        public PictureCollector? Pictures { get; init; }

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
        var started = System.Diagnostics.Stopwatch.StartNew();
        DocumentLog.Write(document, $"Step1 start: {document.SourcePath} ({document.Kind}), {context.Options.Dpi} dpi");
        try
        {
            if (document.Kind == DocumentKind.Pdf)
            {
                PdfPackage.EnsureOpens(document.SourcePath);
            }
            else
            {
                OoxmlPackage.EnsureOpens(document.SourcePath, document.Kind);
            }

            if (!Directory.Exists(document.OutputFolder))
            {
                Directory.CreateDirectory(document.OutputFolder);
            }

            AtomicFile.DeleteStaleTemps(document.OutputFolder);

            // The pictures of an earlier conversion into this folder are removed, also when they are not saved this time (FR-133).
            PictureFiles.Clear(document.OutputFolder);
            Directory.CreateDirectory(workDirectory);

            var layout = document.Kind switch
            {
                DocumentKind.Docx => await LayoutDocxAsync(work, workDirectory, cancellationToken).ConfigureAwait(false),
                DocumentKind.Pptx => await LayoutPptxAsync(work, workDirectory, cancellationToken).ConfigureAwait(false),
                DocumentKind.Xlsx => await LayoutXlsxAsync(work, workDirectory, cancellationToken).ConfigureAwait(false),
                DocumentKind.Pdf => await LayoutPdfAsync(work, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(work)),
            };

            await BuildPagesAsync(document, layout, context.Options.Dpi, cancellationToken).ConfigureAwait(false);

            // The pictures get their names now that the pages of the blocks are known (FR-133).
            document.ExtractPictures = layout.Pictures is not null;
            if (layout.Pictures is not null)
            {
                SnapSheetShapes(document, layout);
                string? PageImage(int index) => document.Pages.FirstOrDefault(p => p.Index == index) is { ImageFile.Length: > 0 } page
                    ? Path.Combine(document.OutputFolder, page.ImageFile.Replace('/', Path.DirectorySeparatorChar))
                    : null;
                DocumentLog.Write(document, [.. layout.Pictures.Skipped, .. PictureFiles.Write(document.OutputFolder, document.Blocks, document.Pages.Count, layout.Pictures, PageImage)]);
            }

            // The pages (and their images) are made as always, for Step2, Step3 and the preview; the Markdown is the one of the extractor (FR-112).
            if (context.ExtractorFor(document.Kind) is { } extractor)
            {
                await ExtractAsync(document, extractor, workDirectory, cancellationToken).ConfigureAwait(false);
            }

            document.Converter = ConverterText(document, work.Context);
            document.Step1 = StepStatus.Done;
            MarkdownRenderer.Write(document);
            context.Save(document);
            DocumentLog.Write(document, [
                .. document.Warnings.Select(w => $"warning {w.Code}: {w.Message}"),
                $"Step1 done: {document.Pages.Count} pages, {started.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s ({document.Converter})",
            ]);
        }
        catch (OperationCanceledException)
        {
            document.Step1 = StepStatus.Interrupted;
            context.Save(document);
            DocumentLog.Write(document, "Step1 interrupted");
            throw;
        }
        catch (Exception ex)
        {
            document.Step1 = StepStatus.Failed;
            document.Error = new DocumentError(1, ex.Message);
            context.Save(document);
            DocumentLog.Write(document, $"Step1 failed after {started.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s: {ex.GetType().Name}: {ex.Message}");
            context.Logger.LogWarning("Step 1 failed for {Source}: {Message}", document.SourcePath, ex.Message);
            throw;
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>The program that made the Markdown: MarkPortO and its version, then the extractor, or the engine that made the PDF (not for a PDF file, which needs none).</summary>
    private static string ConverterText(DocumentRecord document, JobContext context)
    {
        var text = "MarkPortO " + AppVersion.Current;
        if (document.Extractor is not null)
        {
            return text + "; extractor: " + document.Extractor;
        }

        return document.Kind != DocumentKind.Pdf && context.Engine.Describe(document.Kind) is { Length: > 0 } engine ? text + "; engine: " + engine : text;
    }

    // ---- an extractor: an external command writes the Markdown of the whole document, without pages (FR-110 to FR-112) ----

    private static async Task ExtractAsync(DocumentRecord document, ExtractorProfile extractor, string workDirectory, CancellationToken cancellationToken)
    {
        var result = await ExtractorRunner.RunAsync(extractor, document.SourcePath, Path.Combine(workDirectory, "extractor"), cancellationToken).ConfigureAwait(false);
        var note = "> " + Loc.M("This Markdown was written by the extractor `{0}`. It has no page marks and no references to the page images.", document.MarkdownLanguage, extractor.Name);
        var name = Path.GetFileNameWithoutExtension(document.SourcePath);
        AtomicFile.WriteAllText(Path.Combine(document.OutputFolder, MarkdownRenderer.MarkdownFileName(name)), note + "\n\n" + result.Markdown.TrimEnd() + "\n");

        // The other files of the command (the pictures that the Markdown refers to) go to the output folder with the same relative paths; nothing is overwritten.
        foreach (var file in Directory.EnumerateFiles(result.OutputFolder, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, result.MarkdownFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Path.Combine(document.OutputFolder, Path.GetRelativePath(result.OutputFolder, file));
            if (File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target);
        }

        document.Extractor = extractor.Name;
    }

    // ---- docx: one PDF, one page per PDF page, text placed by the PDF text ----

    private static async Task<Layout> LayoutDocxAsync(DocumentWork work, string workDirectory, CancellationToken cancellationToken)
    {
        var document = work.Document;
        var layout = new Layout { Pictures = NewPictureCollector(work, shapes: false) };
        // Word has the numbers of figures and tables counted again when it prints, so the text is read from a copy in which Word did it.
        var content = DocxReader.ReadContent(await UpdatedFieldsCopyAsync(work, workDirectory, cancellationToken).ConfigureAwait(false) ?? document.SourcePath, work.Context.Options.Emphasis, layout.Pictures);
        var blocks = content.Blocks;

        var pdf = Path.Combine(workDirectory, "document.pdf");
        await ExportAsync(work, document.SourcePath, pdf, cancellationToken).ConfigureAwait(false);

        var pageTexts = PdfTextExtractor.GetPageTexts(pdf);
        layout.Warnings.AddRange(DocxPageAssigner.Assign(blocks, pageTexts));

        // A shape belongs to the page of the paragraph it is anchored in (its index is a place in the blocks as they were read).
        var effective = DocxPageAssigner.EffectivePages([.. blocks.Select(b => b.Block)], pageTexts.Count);

        // A table that runs over several pages is cut at the page breaks: each page has the rows that are on it.
        var placed = DocxPageAssigner.ExpandTables(blocks);
        layout.DocxBlocks = placed;
        layout.Blocks.AddRange(placed.Select(b => b.Block));

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

    // ---- pdf: the page images are the content; the text layer, when there is one, only helps (FR-076) ----

    private static async Task<Layout> LayoutPdfAsync(DocumentWork work, CancellationToken cancellationToken)
    {
        var document = work.Document;
        var layout = new Layout();
        var pdf = document.SourcePath;
        var pageCount = await PdfRenderer.GetPageCountAsync(pdf).ConfigureAwait(false);
        var usePdfText = work.Context.Options.UsePdfText;

        using var text = usePdfText ? PdfPackage.OpenText(pdf) : null;
        var withoutText = 0;
        for (var i = 0; i < pageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            layout.Pdf.Add(new PdfPageRef(pdf, i));
            layout.Labels.Add(PageLabel.Page(i + 1));
            layout.Sheets.Add(null);

            if (text is null)
            {
                continue;
            }

            var paragraphs = PdfPackage.TryGetParagraphs(text, i);
            foreach (var paragraph in paragraphs)
            {
                layout.Blocks.Add(new Block { Kind = BlockKind.Paragraph, Markdown = paragraph, PageIndex = i + 1 });
            }

            if (paragraphs.Count == 0)
            {
                withoutText++;
            }
        }

        if (withoutText > 0)
        {
            layout.Warnings.Add(new Warning(
                "PdfNoText",
                Loc.T("{0} of {1} pages have no text layer (a scanned page?). Their text comes only from the VLM (Step3).", withoutText, pageCount)));
        }

        return layout;
    }

    // ---- pptx: one PDF page per slide ----

    private static async Task<Layout> LayoutPptxAsync(DocumentWork work, string workDirectory, CancellationToken cancellationToken)
    {
        var document = work.Document;
        var includeHidden = work.Context.Options.IncludeHiddenSlides;
        var layout = new Layout { Pictures = NewPictureCollector(work) };
        var slides = PptxReader.Read(document.SourcePath, includeHidden, work.Context.Options.IncludeOffSlideText, work.Context.Options.Emphasis, layout.Pictures);
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
        var layout = new Layout { Pictures = NewPictureCollector(work) };
        var sheets = XlsxReader.Read(document.SourcePath, work.Context.Options.IncludeHiddenSheets, work.Context.Options.Emphasis, layout.Pictures);
        var verdicts = XlsxCrossExtractor.Evaluate(document.SourcePath, sheets);

        var unitNumber = 0;
        foreach (var sheet in sheets)
        {
            foreach (var unit in ExcelUnitPlanner.Plan(sheet, work.Context.Options.ExcelRange))
            {
                cancellationToken.ThrowIfCancellationRequested();
                unitNumber++;
                var copy = Path.Combine(workDirectory, $"unit{unitNumber}.xlsx");
                ExcelTempCopy.Create(document.SourcePath, copy, sheet, unit, work.Context.Options.ExcelRange);
                var pdf = Path.Combine(workDirectory, $"unit{unitNumber}.pdf");
                await ExportAsync(work, copy, pdf, cancellationToken).ConfigureAwait(false);

                var pdfPages = await PdfRenderer.GetPageCountAsync(pdf).ConfigureAwait(false);
                var firstPageOfUnit = layout.Pdf.Count + 1;
                for (var i = 0; i < pdfPages; i++)
                {
                    layout.Pdf.Add(new PdfPageRef(pdf, i));
                    layout.Labels.Add(PageLabel.Sheet(sheet.Name));
                    layout.Sheets.Add(new SheetInfo(sheet.Name, unit.ColumnRange, unit.Part, unit.PartCount));
                    if (work.Context.Options.ExcelRange != ExcelRange.PrintArea && ExcelUnitPlanner.DpiFactor(sheet, unit, work.Context.Options.ExcelRange) is var factor and > 1.0)
                    {
                        layout.DpiFactors[layout.Pdf.Count - 1] = factor;
                    }

                    if (verdicts.TryGetValue(sheet.Name, out var verdict))
                    {
                        // A "×" over the sheet deletes every part of the sheet. The warning is given once per sheet.
                        MarkDeleted(layout, layout.Pdf.Count, verdict, PageLabel.Sheet(sheet.Name), warn: unit.Part == 1 && i == 0);
                    }
                }

                if (pdfPages > 0)
                {
                    // A long sheet is printed on several pages, one below the other: the rows are put on the page they are on, so that each page has the
                    // table of its own rows (the text sent with the image of the page), and the Markdown of the document joins them again (FR-090).
                    int[]? rowPages = null;
                    if (pdfPages > 1)
                    {
                        try
                        {
                            rowPages = ExcelRowPages.Assign(sheet, unit.FromColumnIndex, unit.ToColumnIndex, PdfTextExtractor.GetPageTexts(pdf));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            work.Context.Logger.LogWarning("The pages of the rows of '{Sheet}' could not be found: {Message}", sheet.Name, ex.Message);
                        }
                    }

                    int PageOf(int pageInUnit) => firstPageOfUnit + Math.Min(Math.Max(0, pageInUnit), pdfPages - 1);
                    PlaceSheetShapes(document, layout.Pictures, sheet, unit, work.Context.Options.ExcelRange, rowPages, PageOf, pdf);
                    foreach (var part in sheet.ToMarkdownParts(unit.FromColumnIndex, unit.ToColumnIndex, rowPages))
                    {
                        layout.Blocks.Add(new Block
                        {
                            Kind = part.IsTable ? BlockKind.SheetTable : BlockKind.Paragraph,
                            Markdown = part.Markdown,
                            PageIndex = PageOf(part.Page),
                            JoinedToPage = part.IsTable && part.Page != part.HeadPage ? PageOf(part.HeadPage) : null,
                        });
                    }
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

    /// <summary>
    /// A collector of the pictures when they are saved (FR-133, FR-135): not when an extractor writes the Markdown (FR-110). A metafile is drawn at
    /// the DPI of the page images (FR-134).
    /// </summary>
    private static PictureCollector? NewPictureCollector(DocumentWork work, bool shapes = true)
    {
        var options = work.Context.Options;
        var cutShapes = shapes && options.ExtractShapes;
        return (options.ExtractPictures || cutShapes) && work.Context.ExtractorFor(work.Document.Kind) is null
            ? new PictureCollector(options.Dpi, options.ExtractPictures, cutShapes)
            : null;
    }

    /// <summary>
    /// Moves the worked-out places of the drawn shapes of the sheets onto the shapes as the PDF of their pages draws them (FR-136): the engine draws
    /// the columns and the rows a little differently from what their widths and heights say. A place that is not found in the PDF is kept.
    /// </summary>
    private static void SnapSheetShapes(DocumentRecord document, Layout layout)
    {
        if (document.Kind != DocumentKind.Xlsx || layout.Pictures is not { CutShapes: true } pictures)
        {
            return;
        }

        var opened = new Dictionary<string, UglyToad.PdfPig.PdfDocument?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var picture in pictures.Pictures.Where(p => p.IsShape && p.Area?.Page is not null))
            {
                var index = picture.Area!.Page!.Value - 1;
                if (index < 0 || index >= layout.Pdf.Count)
                {
                    continue;
                }

                var reference = layout.Pdf[index];
                try
                {
                    if (!opened.TryGetValue(reference.PdfPath, out var pdf))
                    {
                        pdf = UglyToad.PdfPig.PdfDocument.Open(reference.PdfPath);
                        opened[reference.PdfPath] = pdf;
                    }

                    if (pdf is not null)
                    {
                        var before = picture.Area;
                        picture.Area = PdfShapeSnap.Snap(pdf.GetPage(reference.PageIndexInPdf + 1), before, ShapeAreas.SheetRoomPoints);
                        DocumentLog.Write(document, ReferenceEquals(before, picture.Area)
                            ? $"page {index + 1}: a drawn shape is not found in the drawing of the PDF; the worked-out place is used"
                            : $"page {index + 1}: a drawn shape is moved onto its drawing in the PDF: {Describe(picture.Area)}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    opened[reference.PdfPath] = null; // not readable: the worked-out places are used
                    DocumentLog.Write(document, $"page {index + 1}: the drawing of the shapes could not be read from the PDF ({ex.GetType().Name}); the worked-out place is used");
                }
            }
        }
        finally
        {
            foreach (var pdf in opened.Values)
            {
                pdf?.Dispose();
            }
        }
    }

    private static string Describe(ShapeArea? area) => area is null
        ? "nowhere"
        : string.Create(CultureInfo.InvariantCulture, $"left {area.Left:0.###}, top {area.Top:0.###}, {area.Width:0.###} x {area.Height:0.###} of the page");

    /// <summary>
    /// Works out where the drawn shapes of one printed part of a sheet are on its pages (FR-136), from the widths of the columns, the heights of the
    /// rows and how much the part is shrunk to fit the A3 page (<see cref="ExcelTempCopy"/>). Not for a sheet that follows its own print setup.
    /// </summary>
    private static void PlaceSheetShapes(DocumentRecord document, PictureCollector? pictures, SheetContent sheet, ExcelUnit unit, ExcelRange range, IReadOnlyList<int>? rowPages, Func<int, int> pageOf, string pdfPath)
    {
        if (pictures is not { CutShapes: true } || range == ExcelRange.PrintArea || sheet.Shapes.Count == 0)
        {
            return;
        }

        var widths = sheet.ShapeColumnWidthsPoints;
        var heights = sheet.RowHeightsPoints;
        double Sum(double[] sizes, int from, int to) => sizes.Skip(from).Take(Math.Max(0, to - from)).Sum();
        var partLeft = Sum(widths, 0, unit.FromColumnIndex);
        var partWidth = Sum(widths, unit.FromColumnIndex, unit.ToColumnIndex + 1);
        var landscape = range != ExcelRange.Sheet || ExcelUnitPlanner.IsLandscape(sheet);
        var pageWidth = landscape ? ShapeAreas.A3LongPoints : ShapeAreas.A3ShortPoints;
        var pageHeight = landscape ? ShapeAreas.A3ShortPoints : ShapeAreas.A3LongPoints;
        var scale = ShapeAreas.SheetScale(partWidth, heights.Sum(), pageWidth, pageHeight, fitHeight: range == ExcelRange.Sheet);
        int PageInPart(int row) => rowPages is null || rowPages.Count == 0 ? 0 : rowPages[Math.Clamp(row, 0, rowPages.Count - 1)];

        var shapes = sheet.Shapes.Where(s => s.Column >= unit.FromColumnIndex && s.Column <= unit.ToColumnIndex).ToList();
        if (shapes.Count == 0)
        {
            return;
        }

        // From the top of the page, in points: where the rows of a page are worked out to be (its first row printed at the top margin).
        double TopOfPage(int page) => Sum(heights, 0, Enumerable.Range(0, heights.Length).FirstOrDefault(r => PageInPart(r) == page));
        double Worked(double sheetY, int page) => ShapeAreas.SheetMarginPoints + (sheetY - TopOfPage(page)) * scale;

        // The rows are printed a little taller or shorter than their heights say; the rows whose text is found on the page tell how (PdfRowFit).
        var fits = new Dictionary<int, PdfRowFit?>();
        using var pdf = OpenForFit(pdfPath);
        PdfRowFit? FitOf(int page)
        {
            if (!fits.TryGetValue(page, out var fit))
            {
                var rows = Enumerable.Range(0, sheet.Rows.Length)
                    .Where(r => PageInPart(r) == page && heights[r] > 0)
                    .Select(r => (Text: Enumerable.Range(unit.FromColumnIndex, unit.ToColumnIndex - unit.FromColumnIndex + 1)
                        .Select(c => c < sheet.Rows[r].Length ? sheet.Rows[r][c].Trim() : string.Empty).FirstOrDefault(t => t.Length > 0) ?? string.Empty,
                        Bottom: Worked(Sum(heights, 0, r + 1), page)))
                    .Where(r => r.Text.Length > 0)
                    .ToList();
                try
                {
                    fit = pdf is null || page >= pdf.NumberOfPages ? null : PdfRowFit.Fit(pdf.GetPage(page + 1), rows);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    fit = null;
                }

                fits[page] = fit;
            }

            return fit;
        }

        foreach (var shape in shapes)
        {
            var page = PageInPart(shape.Row);
            var x = ShapeAreas.SheetMarginPoints + (shape.X - partLeft) * scale;
            var y = Worked(shape.Y, page);
            var height = shape.Height * scale;
            var fit = FitOf(page);
            if (fit is not null)
            {
                height *= fit.Scale;
                y = fit.Map(y);
            }

            var area = pictures.Pictures[shape.Mark].Area = ShapeAreas.Of(x, y, shape.Width * scale, height, shape.Degrees, ShapeAreas.SheetRoomPoints, pageWidth, pageHeight, pageOf(page));
            DocumentLog.Write(document,
                FormattableString.Invariant($"sheet '{sheet.Name}': a drawn shape at {shape.X:0.#},{shape.Y:0.#} ({shape.Width:0.#} x {shape.Height:0.#} pt), part {partWidth:0.#} x {heights.Sum():0.#} pt shrunk to {scale:0.##}")
                + (fit is null ? ", rows not found in the PDF" : FormattableString.Invariant($", rows fitted by {fit.Rows} found in the PDF ({fit.Scale:0.###} x + {fit.Offset:0.#} pt)"))
                + FormattableString.Invariant($": page {pageOf(page)} at {Describe(area)}"));
        }
    }

    private static UglyToad.PdfPig.PdfDocument? OpenForFit(string pdfPath)
    {
        try
        {
            return UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// A copy of the docx saved by Word with its fields updated, when the document has numbered figures or tables and the engine is Office;
    /// otherwise null (the original is read). The numbers in the file can be old, and the page images show the new ones.
    /// </summary>
    private static async Task<string?> UpdatedFieldsCopyAsync(DocumentWork work, string workDirectory, CancellationToken cancellationToken)
    {
        if (work.Context.Engine is not IFieldUpdatingEngine updating || !DocxFieldProbe.HasCountingFields(work.Document.SourcePath))
        {
            return null;
        }

        var copy = Path.Combine(workDirectory, "fields-updated.docx");
        try
        {
            if (await updating.TryUpdateFieldsAsync(work.Document.SourcePath, copy, work.Context.EngineTimeout, cancellationToken).ConfigureAwait(false) && File.Exists(copy))
            {
                return copy;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the original is read
        }

        work.Context.Logger.LogInformation("The fields of {Source} could not be updated by Word; the numbers saved in the file are used.", work.Document.SourcePath);
        return null;
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
        var textSources = new Dictionary<string, UglyToad.PdfPig.PdfDocument?>(StringComparer.OrdinalIgnoreCase);

        try
        {
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

                var pageDpi = layout.DpiFactors.TryGetValue(i, out var factor) ? Math.Clamp((int)Math.Round(dpi * factor), dpi, Math.Max(dpi, MaxRaisedDpi)) : dpi;
                var rendered = await PdfRenderer.RenderPageAsync(
                    pdfDocument,
                    reference.PageIndexInPdf,
                    pageDpi,
                    Path.Combine(document.OutputFolder, imageFile.Replace('/', Path.DirectorySeparatorChar)),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                // A page with nothing on it is not sent to the VLM (FR-082).
                if (!page.IsDeleted && PageBlank.IsBlank(rendered.Path, ignoreHeaderAndFooter: document.Kind == DocumentKind.Docx))
                {
                    page.IsBlank = true;
                    page.Step3 = PageStep3Status.Skipped;
                }

                DocumentLog.Write(document, $"page {index} ({page.SourceLabel}): {rendered.Width} x {rendered.Height} px at {pageDpi} dpi"
                    + $" ({DocumentLog.PageSize(rendered.Width, rendered.Height, pageDpi)}), {imageFile}"
                    + (page.IsDeleted ? "; deleted page (a × over the whole page): its text is left out and the VLM does not read it" : string.Empty)
                    + (page.IsBlank ? (document.Kind == DocumentKind.Docx ? "; blank page (nothing but the header and the footer): the VLM does not read it" : "; blank page: the VLM does not read it") : string.Empty));
            }
            catch (PageTooLargeException ex)
            {
                // One page that is too large does not stop the document (FR-029): the page is kept without an image.
                page.ImageFile = string.Empty;
                page.Error = ex.Message;
                layout.Warnings.Add(new Warning("ImageFailed", ex.Message, index));
                DocumentLog.Write(document, $"page {index} ({page.SourceLabel}): no image: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DocumentLog.Write(document, $"page {index} ({page.SourceLabel}): the image could not be made: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            page.TextLines = TextLinesOf(layout.Pdf[i], textSources);
            pages.Add(page);
        }
        }
        finally
        {
            foreach (var source in textSources.Values)
            {
                source?.Dispose();
            }
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

    /// <summary>The lines of text of a page, from the PDF, for cutting the page into bands (FR-041); <c>null</c> when they cannot be read.</summary>
    private static List<LineSpan>? TextLinesOf(PdfPageRef reference, Dictionary<string, UglyToad.PdfPig.PdfDocument?> sources)
    {
        try
        {
            if (!sources.TryGetValue(reference.PdfPath, out var source))
            {
                source = UglyToad.PdfPig.PdfDocument.Open(reference.PdfPath);
                sources[reference.PdfPath] = source;
            }

            return source is null ? null : [.. PdfLineExtractor.GetLines(source, reference.PageIndexInPdf)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            sources[reference.PdfPath] = null; // not readable: the pages of this PDF are read whole
            return null;
        }
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
