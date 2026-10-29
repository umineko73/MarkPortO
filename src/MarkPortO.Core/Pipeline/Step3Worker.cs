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
        context.Save(document);

        try
        {
            var reader = context.Vlm ?? throw new InvalidOperationException("The VLM connection is not set up.");
            var prompt = (context.Prompts ?? new Settings.PromptSettings()).For(document.Kind, document.MarkdownLanguage);
            var governor = context.Governor ?? new Step3Governor();

            foreach (var page in document.Pages.Where(p => p.IsDeleted || p.IsBlank))
            {
                page.Step3 = PageStep3Status.Skipped; // a deleted page and a blank page are never read
            }

            var gate = new object();
            using var slots = new SemaphoreSlim(Math.Max(1, context.Step3Parallel));
            var readings = new List<Task>();
            foreach (var page in document.Pages.Where(p => !p.IsDeleted && !p.IsBlank && p.Step3 != PageStep3Status.Done))
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

            await Task.WhenAll(readings).ConfigureAwait(false);

            var unfinished = document.Pages.Any(p => !p.IsDeleted && !p.IsBlank && p.Step3 is PageStep3Status.Pending or PageStep3Status.Running);
            document.Step3 = unfinished ? StepStatus.Interrupted : StepStatus.Done;
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

                    var outcome = await ReadPageAsync(document, page, reader, prompt, context.MaxLinesPerRead, context.MaxImageSide, cancellationToken).ConfigureAwait(false);
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

    /// <summary>How a page ended: read, or failed with a kind of failure.</summary>
    private readonly record struct PageOutcome(bool Succeeded, VlmFailureKind Kind, string Message);

    private static async Task<PageOutcome> ReadPageAsync(DocumentRecord document, PageRecord page, IVlmReader reader, string prompt, int maxLines, int maxImageSide, CancellationToken cancellationToken)
    {
        try
        {
            if (page.ImageFile.Length == 0)
            {
                throw new VlmException(Loc.T("The page has no image to read."));
            }

            var path = Path.Combine(document.OutputFolder, page.ImageFile.Replace('/', Path.DirectorySeparatorChar));
            var png = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var language = document.MarkdownLanguage;
            var bands = BandPlanner.Plan(page.TextLines, maxLines);
            var pageText = PromptSupplement.UsesPageText(prompt) ? PageTextOf(document, page) : null;
            string text;
            UsageInfo usage;
            double? cost;
            if (bands.Count == 0)
            {
                var answer = await ReadOnceAsync(reader, PromptSupplement.Build(prompt, language, pageText: pageText), Shrink(png, maxImageSide), cancellationToken).ConfigureAwait(false);
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
                    var answer = await ReadOnceAsync(reader, PromptSupplement.Build(prompt, language, i + 1, bands.Count, pageText), strip, cancellationToken).ConfigureAwait(false);
                    texts.Add(AnswerFences.Strip(answer.Text));
                    usages.Add(answer.Usage);
                    costs.Add(answer.CostUsd);
                }

                text = BandMerger.Merge(texts);
                usage = Sum(usages);
                cost = costs.Any(c => c.HasValue) ? costs.Sum(c => c ?? 0) : null;
            }

            page.VlmText = text;
            page.Usage = usage;
            page.CostUsd = cost;
            page.Bands = bands.Count;
            page.Step3 = PageStep3Status.Done;
            page.Error = null;
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
            return new PageOutcome(false, (ex as VlmException)?.Kind ?? VlmFailureKind.Unknown, page.Error);
        }
    }

    /// <summary>
    /// The text that Step1 took from the document for this page (FR-061): the blocks of the page, in the language of the
    /// job, cut at <see cref="PromptSupplement.MaxPageTextCharacters"/> (a warning tells when).
    /// </summary>
    private static string PageTextOf(DocumentRecord document, PageRecord page)
    {
        var effective = DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count);
        var text = string.Join(
            "\n\n",
            document.Blocks
                .Where((block, i) => effective[i] == page.Index && block.Markdown.Length > 0)
                .Select(block => MarkdownText.ResolveMarks(block.Markdown)));
        var limit = PromptSupplement.MaxPageTextCharacters;
        if (text.Length <= limit)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        document.Warnings.Add(new Warning(
            "PageTextCut",
            Loc.T("{0}: the text of the page was cut at {1} characters for the prompt.", PageLabel.Text(page.SourceLabel, CultureInfo.CurrentUICulture), limit),
            page.Index));
        return text[..cut];
    }

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
