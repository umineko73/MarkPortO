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
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Pipeline;

/// <summary>
/// Step 3: read every page image with the VLM and add the text to the Markdown, page by page. A page that fails is
/// recorded and the others go on (FR-029). The state is saved after each page, so a resume continues with the next one.
/// Several pages may be read at the same time (FR-044); the governor of the job stops the reading when a limit is reached
/// or the same failure repeats (FR-049, FR-057), and the pages not yet read stay pending for a resume.
/// </summary>
public sealed class Step3Worker : IPipelineStep<DocumentWork>
{
    public string Name => "step3";

    /// <summary>Documents are taken one at a time. How many pages of a document are read together is the setting of the connection.</summary>
    public int Concurrency => 1;

    public async Task ProcessAsync(DocumentWork work, CancellationToken cancellationToken)
    {
        var document = work.Document;
        var context = work.Context;

        // Not selected, finished earlier, or an earlier step did not finish: nothing to do.
        if (document.Step3 is StepStatus.NotApplicable or StepStatus.Done
            || document.Step1 != StepStatus.Done
            || document.Step2 == StepStatus.Failed)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        document.Step3 = StepStatus.Running;
        document.VlmModel = ModelText(context.Job.Step3Connection);
        context.Save(document);

        try
        {
            var reader = context.Vlm ?? throw new InvalidOperationException("The VLM connection is not set up.");
            var promptName = context.Options.PromptNames.For(document.Kind);
            var prompts = context.Prompts ?? new Settings.PromptSettings();
            var prompt = prompts.Resolve(document.Kind, document.MarkdownLanguage, promptName);
            var figurePrompt = prompts.FigureFor(document.MarkdownLanguage);
            document.VlmPrompt = Jobs.Step3Settings.PromptText(promptName);
            var governor = context.Governor ?? new Step3Governor();

            // The header, the footer and the page number of a Word document are in every page image; the result of the VLM is without them (FR-100).
            var furniture = document.Kind == DocumentKind.Docx && File.Exists(document.SourcePath) ? PageFurniture.Read(document.SourcePath) : [];

            MarkOutOfRange(document, context.Options.Pages);
            MarkJoinedTableOnly(document);
            foreach (var page in document.Pages.Where(p => !p.ReadsInStep3))
            {
                page.Step3 = PageStep3Status.Skipped; // a deleted page, a blank page and a page outside the range are never read
            }

            // A page that the text of Step1 is enough for is not sent (FR-150): it is done at once, and it does not count for the limits.
            var effective = DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count);
            foreach (var page in document.Pages.Where(p => p.ReadsInStep3 && p.Step3 != PageStep3Status.Done))
            {
                if (ReadingPlanner.For(document, page, effective).Reading == PageReading.None)
                {
                    Record(document, page, PageReading.None, text: null, figures: null, usage: null, cost: null, bands: 0, table: null);
                }
            }

            var gate = new object();
            using var slots = new SemaphoreSlim(Math.Max(1, context.Step3Parallel));
            var readings = new List<Task>();
            try
            {
                foreach (var page in document.Pages.Where(p => p.ReadsInStep3 && p.Step3 != PageStep3Status.Done))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!governor.TryBegin())
                    {
                        slots.Release();
                        break;
                    }

                    readings.Add(ReadAndRecordAsync(page));
                }
            }
            finally
            {
                // Drain started readings even when cancellation interrupts the slot wait. They still own the semaphore and saved state.
                await Task.WhenAll(readings).ConfigureAwait(false);
            }

            var unfinished = document.Pages.Any(p => p.ReadsInStep3 && p.Step3 is PageStep3Status.Pending or PageStep3Status.Running);
            document.Step3 = unfinished ? StepStatus.Interrupted : StepStatus.Done;
            if (!unfinished)
            {
                document.RereadTotal = 0; // an interrupted reading again goes on counting from where it was when it is resumed
            }

            document.UsageTotal = Total(document);
            document.CostTotalUsd = CostTotal(document);
            MarkdownRenderer.Write(document);
            context.Save(document);

            async Task ReadAndRecordAsync(PageRecord page)
            {
                try
                {
                    lock (gate)
                    {
                        page.Step3 = PageStep3Status.Running;
                        context.Save(document);
                    }

                    var outcome = await ReadPageAsync(document, page, reader, prompt, figurePrompt, context.MaxLinesPerRead, context.MaxImageSide, furniture, cancellationToken).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (outcome.Succeeded)
                        {
                            governor.Succeeded(page.CostUsd);
                        }
                        else
                        {
                            governor.Failed(outcome.Kind, outcome.Message);
                        }

                        context.Job.Step3Totals = new Step3Totals { Pages = governor.PagesRead, CostUsd = governor.CostUsd };
                        context.Store.SaveJob(context.Job);
                        document.UsageTotal = Total(document);
                        document.CostTotalUsd = CostTotal(document);
                        MarkdownRenderer.Write(document);
                        context.Save(document);
                    }
                }
                finally
                {
                    if (page.Step3 == PageStep3Status.Running)
                    {
                        page.Step3 = PageStep3Status.Pending; // interrupted while reading: a resume reads it again
                    }

                    slots.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            document.Step3 = StepStatus.Interrupted;
            context.Save(document);
            throw;
        }
        catch (Exception ex)
        {
            document.Step3 = StepStatus.Failed;
            document.Error = new DocumentError(3, SecretRedact(ex.Message));
            context.Save(document);
            throw;
        }
    }

    /// <summary>The model for the comment in the Markdown: the command and the model (<c>claude haiku</c>), or the model of the server.</summary>
    private static string? ModelText(Step3ConnectionInfo? connection) => connection is null
        ? null
        : Jobs.Step3Settings.ModelText(connection.Kind, connection.Command, connection.Model);

    /// <summary>
    /// Marks the pages whose only content is the rows of a table that runs over from an earlier page. When the tables of Step1 are used
    /// (not <see cref="Step1TablesMode.Off"/>), such a page has nothing for the VLM to add, so it is not sent. With the VLM tables
    /// (<see cref="Step1TablesMode.Off"/>) the rows are read from the image and joined afterwards, so the page is read.
    /// </summary>
    internal static void MarkJoinedTableOnly(DocumentRecord document)
    {
        var onlyJoined = new HashSet<int>();
        if (document.Step1Tables != Step1TablesMode.Off)
        {
            var effective = DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count);
            var other = new HashSet<int>();
            for (var i = 0; i < document.Blocks.Count; i++)
            {
                if (document.Blocks[i] is { Kind: BlockKind.Table or BlockKind.SheetTable, JoinedToPage: not null })
                {
                    onlyJoined.Add(effective[i]);
                }
                else
                {
                    other.Add(effective[i]);
                }
            }

            onlyJoined.ExceptWith(other);
        }

        foreach (var page in document.Pages)
        {
            page.IsJoinedTableOnly = onlyJoined.Contains(page.Index);
        }
    }

    /// <summary>
    /// Marks the pages outside the page range of the job (FR-097). A range that goes past the last page is cut there, with a warning;
    /// a range with no page of this document fails the step.
    /// </summary>
    private static void MarkOutOfRange(DocumentRecord document, string? text)
    {
        var range = PageRange.Parse(text);
        foreach (var page in document.Pages)
        {
            page.IsOutOfRange = range is not null && !range.Contains(page.Index);
        }

        if (range is null)
        {
            return;
        }

        if (document.Pages.Count > 0 && document.Pages.All(p => p.IsOutOfRange))
        {
            throw new InvalidOperationException(Loc.T("The page range '{0}' has no page of this document ({1} pages).", text, document.Pages.Count));
        }

        if (range.Highest > document.Pages.Count && !document.Warnings.Any(w => w.Code == "PageRangeCut"))
        {
            document.Warnings.Add(new Warning("PageRangeCut", Loc.T("The page range '{0}' goes past the last page; it is cut at page {1}.", text, document.Pages.Count)));
        }
    }

    /// <summary>How a page ended: read, or failed with a kind of failure.</summary>
    private readonly record struct PageOutcome(bool Succeeded, VlmFailureKind Kind, string Message);

    private static async Task<PageOutcome> ReadPageAsync(DocumentRecord document, PageRecord page, IVlmReader reader, string prompt, string figurePrompt, int maxLines, int maxImageSide, IReadOnlyList<string> furniture, CancellationToken cancellationToken)
    {
        try
        {
            var effective = DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count);
            var plan = ReadingPlanner.For(document, page, effective);
            if (plan.Reading == PageReading.Pictures)
            {
                var (figures, figureUsage, figureCost) = await ReadPicturesAsync(document, page, plan.Pictures, effective, reader, figurePrompt, maxImageSide, cancellationToken).ConfigureAwait(false);
                Record(document, page, PageReading.Pictures, text: null, figures, figureUsage, figureCost, bands: 0, table: null);
                return new PageOutcome(true, VlmFailureKind.Unknown, string.Empty);
            }

            if (plan.Reading == PageReading.None)
            {
                Record(document, page, PageReading.None, text: null, figures: null, usage: null, cost: null, bands: 0, table: null);
                return new PageOutcome(true, VlmFailureKind.Unknown, string.Empty);
            }

            if (page.ImageFile.Length == 0)
            {
                throw new VlmException(Loc.T("The page has no image to read."));
            }

            var path = Path.Combine(document.OutputFolder, page.ImageFile.Replace('/', Path.DirectorySeparatorChar));
            var png = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var language = document.MarkdownLanguage;
            var bands = BandPlanner.Plan(page.TextLines, maxLines);
            var pageText = PromptSupplement.UsesPageText(prompt) ? PageTextOf(document, page, maxLines) : null;
            var table = pageText?.Table;
            string text;
            UsageInfo usage;
            double? cost;
            if (bands.Count == 0)
            {
                var answer = await ReadOnceAsync(reader, PromptSupplement.Build(prompt, language, pageText: pageText?.Text, table: table), Shrink(png, maxImageSide), cancellationToken).ConfigureAwait(false);
                text = AnswerFences.Strip(answer.Text);
                usage = answer.Usage;
                cost = answer.CostUsd;
            }
            else
            {
                // A page with many lines is read in bands, from the top; one band that fails fails the page (FR-041).
                var texts = new List<string>(bands.Count);
                var usages = new List<UsageInfo>(bands.Count);
                var costs = new List<double?>(bands.Count);
                for (var i = 0; i < bands.Count; i++)
                {
                    var strip = Shrink(Crop(png, bands[i]), maxImageSide);
                    var answer = await ReadOnceAsync(reader, PromptSupplement.Build(prompt, language, i + 1, bands.Count, pageText?.Text, table), strip, cancellationToken).ConfigureAwait(false);
                    texts.Add(AnswerFences.Strip(answer.Text));
                    usages.Add(answer.Usage);
                    costs.Add(answer.CostUsd);
                }

                text = BandMerger.Merge(texts);
                usage = Sum(usages);
                cost = costs.Any(c => c.HasValue) ? costs.Sum(c => c ?? 0) : null;
            }

            // The header and the footer are taken out; a page that the model wrote twice is cut at the second copy (and the header and the
            // footer that stood between the copies are taken out again).
            // A part that the model wrote again further down the page, in another form (a code block written again as a list), is cut too.
            var cleaned = WithoutNotes(PageFurniture.Strip(RepeatedStart.TrimSection(RepeatedStart.Trim(PageFurniture.Strip(text, furniture))), furniture), pageText?.Notes);
            Record(document, page, PageReading.Whole, cleaned, figures: null, usage, cost, bands.Count, table);
            return new PageOutcome(true, VlmFailureKind.Unknown, string.Empty);
        }
        catch (VlmException ex) when (ex.Kind.StopsTheRun())
        {
            // A login or a limit: every other page would fail the same way. The page stays pending, so a resume reads it.
            page.Step3 = PageStep3Status.Pending;
            page.Error = null;
            return new PageOutcome(false, ex.Kind, ex.Message);
        }
        catch (Exception ex) when (ex is VlmException or IOException)
        {
            // This page is recorded as failed; the others are still read.
            page.Step3 = PageStep3Status.Failed;
            page.Error = SecretRedact(ex.Message);
            document.Warnings.Add(new Warning("VlmPageFailed", $"{PageLabel.Text(page.SourceLabel, CultureInfo.CurrentUICulture)}: {page.Error}", page.Index));
            DocumentLog.Write(document, $"page {page.Index} ({page.SourceLabel}): Step3 failed: {page.Error}");
            return new PageOutcome(false, (ex as VlmException)?.Kind ?? VlmFailureKind.Unknown, page.Error);
        }
    }

    /// <summary>Records what was read on a page, and that it is done.</summary>
    private static void Record(DocumentRecord document, PageRecord page, PageReading reading, string? text, List<FigureReading>? figures, UsageInfo? usage, double? cost, int bands, PromptSupplement.TableContinuation? table)
    {
        page.ReadAs = reading;
        page.ReadMode = document.Step3Mode;
        page.VlmText = text;
        page.Figures = figures;
        page.Usage = usage;
        page.CostUsd = cost;
        page.Bands = bands;
        page.ContinuedRowsSent = table?.CarriedRows ?? 0;
        page.RowsWrittenBefore = table?.WrittenBeforeFromPage is not null;
        page.ContinuedCodeSent = table?.CarriesCode ?? false;
        page.CodeWrittenBefore = table?.CodeWrittenBeforeFromPage is not null;
        page.Step3 = PageStep3Status.Done;
        page.Error = null;
        page.RereadMark = false;
        page.VlmModel = document.VlmModel;
        page.VlmPrompt = document.VlmPrompt;
    }

    /// <summary>The most characters of the text around a picture that are sent with it (FR-150).</summary>
    internal const int MaxFigureContextCharacters = 2000;

    /// <summary>
    /// Reads the pictures that Step1 saved on a page, one by one (FR-150): each with its own image, at the size it was saved (the side of the
    /// connection is the limit), and the text around it: the block that holds it and the next block of the page. One picture that fails fails the page.
    /// </summary>
    private static async Task<(List<FigureReading> Figures, UsageInfo Usage, double? Cost)> ReadPicturesAsync(
        DocumentRecord document, PageRecord page, IReadOnlyList<ReadingPlanner.Picture> pictures, IReadOnlyList<int> effective, IVlmReader reader, string figurePrompt, int maxImageSide, CancellationToken cancellationToken)
    {
        var figures = new List<FigureReading>(pictures.Count);
        var usages = new List<UsageInfo>(pictures.Count);
        var costs = new List<double?>(pictures.Count);
        foreach (var picture in pictures)
        {
            var path = Path.Combine(document.OutputFolder, picture.File.Replace('/', Path.DirectorySeparatorChar));
            var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] png;
            try
            {
                png = BandImage.Shrink(BandImage.ToPng(data), maxImageSide);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                throw new VlmException(Loc.T("The picture {0} could not be read.", picture.File));
            }

            // For the drawn shapes of a page, the whole text of the page goes with the image, so that what is already written is not written again.
            var context = picture.Drawing ? PageContext(document, page, effective) : FigureContext(document, page, picture.Block, effective);
            var prompt = picture.Drawing
                ? PromptSupplement.WithSupplement(figurePrompt, Loc.M("This image is a whole page, not one figure. The text and the tables of the page are already written (they are the text around below). Read only the drawn shapes of the page (boxes, circles, arrows, lines, and how they are placed and connected), and write what they show.", document.MarkdownLanguage))
                : figurePrompt;
            var question = prompt.Contains(PromptSupplement.PageTextPlaceholder, StringComparison.Ordinal)
                ? prompt.Replace(PromptSupplement.PageTextPlaceholder, context, StringComparison.Ordinal)
                : prompt;
            var answer = await ReadOnceAsync(reader, question, png, cancellationToken).ConfigureAwait(false);
            figures.Add(new FigureReading(picture.File, FigureAnswer(answer.Text)));
            usages.Add(answer.Usage);
            costs.Add(answer.CostUsd);
        }

        return (figures, Sum(usages), costs.Any(c => c.HasValue) ? costs.Sum(c => c ?? 0) : null);
    }

    /// <summary>The text around a picture: the block that holds it and the next block of the same page, without the files of the pictures (FR-133), cut at <see cref="MaxFigureContextCharacters"/>.</summary>
    internal static string FigureContext(DocumentRecord document, PageRecord page, int block, IReadOnlyList<int> effective)
    {
        var parts = new List<string>();
        for (var i = block; i < document.Blocks.Count && i <= block + 1; i++)
        {
            if (effective[i] != page.Index)
            {
                continue;
            }

            var text = MarkdownText.ResolveMarks(PictureFiles.WithoutFiles(document.Blocks[i].Markdown)).Trim();
            if (text.Length > 0)
            {
                parts.Add(text);
            }
        }

        var joined = string.Join("\n\n", parts);
        if (joined.Length <= MaxFigureContextCharacters)
        {
            return joined;
        }

        var cut = char.IsHighSurrogate(joined[MaxFigureContextCharacters - 1]) ? MaxFigureContextCharacters - 1 : MaxFigureContextCharacters;
        return joined[..cut];
    }

    /// <summary>The text of every block of the page, without the files of the pictures, cut at <see cref="PromptSupplement.MaxPageTextCharacters"/>.</summary>
    private static string PageContext(DocumentRecord document, PageRecord page, IReadOnlyList<int> effective)
    {
        var joined = string.Join("\n\n", document.Blocks
            .Where((_, i) => effective[i] == page.Index)
            .Select(b => MarkdownText.ResolveMarks(PictureFiles.WithoutFiles(b.Markdown)).Trim())
            .Where(t => t.Length > 0));
        var limit = PromptSupplement.MaxPageTextCharacters;
        return joined.Length <= limit ? joined : joined[..(char.IsHighSurrogate(joined[limit - 1]) ? limit - 1 : limit)];
    }

    /// <summary>What the VLM wrote about a picture, without a fence around it; empty when it answered that it has nothing to add (<c>NONE</c>).</summary>
    internal static string FigureAnswer(string answer)
    {
        var text = AnswerFences.Strip(answer).Trim();
        var bare = text.Trim('*', '`', '.', '。', ' ', '"', '「', '」');
        return bare.Length == 0 || string.Equals(bare, "NONE", StringComparison.OrdinalIgnoreCase) || bare == "なし" ? string.Empty : text;
    }

    /// <summary>The text of a page for the prompt, and what it says about a table that runs over pages.</summary>
    /// <param name="Notes">The lines that were put in the text in place of a part that is written on an earlier page: a model may copy them into its answer.</param>
    internal sealed record PageTextResult(string Text, PromptSupplement.TableContinuation? Table, IReadOnlyList<string>? Notes = null);

    /// <summary>The answer without the lines that were put in the text of the page (<see cref="PageTextResult.Notes"/>): the Markdown writes its own.</summary>
    internal static string WithoutNotes(string text, IReadOnlyList<string>? notes)
    {
        if (notes is not { Count: > 0 })
        {
            return text;
        }

        static string Bare(string line) => line.Trim().TrimStart('>').Trim();
        var bare = notes.Select(Bare).ToHashSet(StringComparer.Ordinal);
        var lines = text.Split('\n').Where(l => !bare.Contains(Bare(l)));
        return System.Text.RegularExpressions.Regex.Replace(string.Join('\n', lines), @"\n{3,}", "\n\n").Trim('\n', '\r', ' ');
    }

    /// <summary>
    /// The text that Step1 took from the document for this page (FR-061): the blocks of the page, in the language of the
    /// job, cut at <see cref="PromptSupplement.MaxPageTextCharacters"/> (a warning tells when).
    /// </summary>
    /// <remarks>
    /// With the tables of the VLM, a table that runs over pages is read as one: the page where it starts gets the rows of the later pages after
    /// its own (the VLM writes them all), and a later page gets a line that tells that its rows are already written (<see cref="ContinuedTables"/>).
    /// A code block that runs over pages is read the same way, whatever the choice of the tables.
    /// </remarks>
    internal static PageTextResult PageTextOf(DocumentRecord document, PageRecord page, int maxLines)
    {
        var effective = DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count);
        var chains = ContinuedTables.Find(document, effective, p => CarriesRows(document, p, maxLines));
        var parts = new List<string>();
        int headRows = 0, carriedRows = 0;
        int? writtenBefore = null, codeWrittenBefore = null;
        var carriesCode = false;
        var notes = new List<string>();
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            var block = document.Blocks[i];
            if (effective[i] != page.Index || block.Markdown.Length == 0)
            {
                continue;
            }

            string markdown;
            if (chains.Heads.TryGetValue(i, out var later) && ContinuedTables.IsCode(block))
            {
                carriesCode = true;
                markdown = later.Aggregate(block.Markdown, (code, j) => CodeText.Join(code, document.Blocks[j].Markdown));
            }
            else if (later is not null)
            {
                var rows = later.Select(j => ContinuedTables.RowsOf(document.Blocks[j].Markdown)).ToList();
                headRows = ContinuedTables.RowCount(block.Markdown);
                carriedRows += rows.Sum(r => r.Count);
                markdown = block.Markdown + "\n" + string.Join('\n', rows.SelectMany(r => r));
            }
            else if (chains.Continuations.TryGetValue(i, out var head) && ContinuedTables.IsCode(block))
            {
                codeWrittenBefore = effective[head];
                markdown = "> " + Loc.M("The rest of this code is written with the code on page {0}.", document.MarkdownLanguage, effective[head]);
                notes.Add(markdown);
            }
            else if (chains.Continuations.TryGetValue(i, out head))
            {
                writtenBefore = effective[head];
                markdown = "> " + Loc.M("The rows of this table are written with the table on page {0}.", document.MarkdownLanguage, effective[head]);
                notes.Add(markdown);
            }
            else
            {
                markdown = block.Markdown;
            }

            var text = MarkdownText.ResolveMarks(PictureFiles.WithoutFiles(markdown)); // the files of the pictures are not sent (FR-133)
            if (text.Trim().Length > 0)
            {
                parts.Add(text);
            }
        }

        var table = carriedRows > 0 || writtenBefore is not null || carriesCode || codeWrittenBefore is not null
            ? new PromptSupplement.TableContinuation(headRows, carriedRows, writtenBefore, carriesCode, codeWrittenBefore)
            : null;
        var joined = string.Join("\n\n", parts);
        var limit = PromptSupplement.MaxPageTextCharacters;
        if (joined.Length <= limit)
        {
            return new PageTextResult(joined, table, notes);
        }

        var cut = char.IsHighSurrogate(joined[limit - 1]) ? limit - 1 : limit;
        document.Warnings.Add(new Warning(
            "PageTextCut",
            Loc.T("{0}: the text of the page was cut at {1} characters for the prompt.", PageLabel.Text(page.SourceLabel, CultureInfo.CurrentUICulture), limit),
            page.Index));
        return new PageTextResult(joined[..cut], table, notes);
    }

    /// <summary>The page where a table starts takes the rows of the later pages: it is read, and whole (not in bands), so that the VLM can write them.</summary>
    /// <remarks>When Step3 adds to the text of Step1 (FR-150), only a page that is read whole can: the others have the table of Step1.</remarks>
    private static bool CarriesRows(DocumentRecord document, int pageIndex, int maxLines) =>
        document.Pages.FirstOrDefault(p => p.Index == pageIndex) is { ReadsInStep3: true } page
        && BandPlanner.Plan(page.TextLines, maxLines).Count == 0
        && (document.Step3Mode == Step3Mode.Transcribe
            || ReadingPlanner.For(document, page, DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count)).Reading == PageReading.Whole);

    private static async Task<VlmResponse> ReadOnceAsync(IVlmReader reader, string prompt, byte[] png, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(prompt, png, cancellationToken).ConfigureAwait(false);
        }
        catch (VlmException ex) when (ex.TimedOut)
        {
            // A local model sometimes stalls on one request and answers the next at once: try once more (FR-013c).
            return await reader.ReadAsync(prompt, png, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] Shrink(byte[] png, int maxSide)
    {
        try
        {
            return BandImage.Shrink(png, maxSide);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            throw new VlmException(Loc.T("The page image could not be made smaller."));
        }
    }

    private static byte[] Crop(byte[] png, Band band)
    {
        try
        {
            return BandImage.Crop(png, band);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            throw new VlmException(Loc.T("The page image could not be cut into bands."));
        }
    }

    /// <summary>The usage of the bands added up; unavailable when none reported it.</summary>
    private static UsageInfo Sum(IReadOnlyList<UsageInfo> usages)
    {
        var known = usages.Where(u => u.IsAvailable).ToList();
        if (known.Count == 0)
        {
            return UsageInfo.Unavailable;
        }

        static int? Add(IEnumerable<int?> values) => values.Any(v => v.HasValue) ? values.Sum(v => v ?? 0) : null;
        return new UsageInfo(Add(known.Select(u => u.PromptTokens)), Add(known.Select(u => u.CompletionTokens)), Add(known.Select(u => u.TotalTokens)));
    }

    /// <summary>The total of the pages that reported their usage; unavailable when none did.</summary>
    private static UsageInfo Total(DocumentRecord document)
    {
        var known = document.Pages.Where(p => !p.IsDeleted && p.Usage is { IsAvailable: true }).Select(p => p.Usage!).ToList();
        if (known.Count == 0)
        {
            return UsageInfo.Unavailable;
        }

        int? Sum(Func<UsageInfo, int?> pick) => known.Any(u => pick(u).HasValue) ? known.Sum(u => pick(u) ?? 0) : null;
        return new UsageInfo(Sum(u => u.PromptTokens), Sum(u => u.CompletionTokens), Sum(u => u.TotalTokens));
    }

    /// <summary>The cost of the pages that reported it; <c>null</c> when none did.</summary>
    private static double? CostTotal(DocumentRecord document)
    {
        var known = document.Pages.Where(p => !p.IsDeleted && p.CostUsd.HasValue).Select(p => p.CostUsd!.Value).ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    private static string SecretRedact(string text) => Logging.SecretRegistry.Shared.Redact(text);
}
