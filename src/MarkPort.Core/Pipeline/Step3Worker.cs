// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
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
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Markdown;
using MarkPort.Core.Vlm;

namespace MarkPort.Core.Pipeline;

/// <summary>
/// Step 3: read every page image with the VLM and add the text to the Markdown, page by page. A page that fails is
/// recorded and the others go on (FR-029). The state is saved after each page, so a resume continues with the next one.
/// </summary>
public sealed class Step3Worker : IPipelineStep<DocumentWork>
{
    public string Name => "step3";

    /// <summary>Pages are read one at a time: a local model gets slower when it is asked in parallel.</summary>
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
            var prompt = (context.Prompts ?? new Settings.PromptSettings()).For(document.Kind);

            foreach (var page in document.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.IsDeleted)
                {
                    page.Step3 = PageStep3Status.Skipped; // a deleted page is never read
                    continue;
                }

                if (page.Step3 == PageStep3Status.Done)
                {
                    continue; // read in an earlier run
                }

                await ReadPageAsync(document, page, reader, prompt, context.MaxLinesPerRead, cancellationToken).ConfigureAwait(false);
                document.UsageTotal = Total(document);
                MarkdownRenderer.Write(document);
                context.Save(document);
            }

            document.Step3 = StepStatus.Done;
            document.UsageTotal = Total(document);
            MarkdownRenderer.Write(document);
            context.Save(document);
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

    private static async Task ReadPageAsync(DocumentRecord document, PageRecord page, IVlmReader reader, string prompt, int maxLines, CancellationToken cancellationToken)
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
            string text;
            UsageInfo usage;
            if (bands.Count == 0)
            {
                var answer = await ReadOnceAsync(reader, PromptSupplement.Build(prompt, language), png, cancellationToken).ConfigureAwait(false);
                text = answer.Text;
                usage = answer.Usage;
            }
            else
            {
                // A page with many lines is read in bands, from the top; one band that fails fails the page (FR-041).
                var texts = new List<string>(bands.Count);
                var usages = new List<UsageInfo>(bands.Count);
                for (var i = 0; i < bands.Count; i++)
                {
                    var strip = Crop(png, bands[i]);
                    var answer = await ReadOnceAsync(reader, PromptSupplement.Build(prompt, language, i + 1, bands.Count), strip, cancellationToken).ConfigureAwait(false);
                    texts.Add(answer.Text);
                    usages.Add(answer.Usage);
                }

                text = BandMerger.Merge(texts);
                usage = Sum(usages);
            }

            page.VlmText = text;
            page.Usage = usage;
            page.Bands = bands.Count;
            page.Step3 = PageStep3Status.Done;
            page.Error = null;
        }
        catch (Exception ex) when (ex is VlmException or IOException)
        {
            // This page is recorded as failed; the others are still read.
            page.Step3 = PageStep3Status.Failed;
            page.Error = SecretRedact(ex.Message);
            document.Warnings.Add(new Warning("VlmPageFailed", $"{PageLabel.Text(page.SourceLabel, CultureInfo.CurrentUICulture)}: {page.Error}", page.Index));
        }
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

    private static string SecretRedact(string text) => Logging.SecretRegistry.Shared.Redact(text);
}
