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

using MarkPort.Core.Localization;
using MarkPort.Core.Convert;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.IO;

namespace MarkPort.Core.Jobs;

/// <summary>What a resume does.</summary>
public enum ResumeMode
{
    /// <summary>Run what is not finished. Files that failed stay failed.</summary>
    Unfinished,

    /// <summary>Run only the files that failed.</summary>
    RetryFailed,
}

/// <summary>What was decided for one document.</summary>
public enum ResumeAction
{
    /// <summary>Nothing to do (finished, or failed and not retried).</summary>
    Skip,

    /// <summary>Run the steps that are not done.</summary>
    Run,

    /// <summary>The input changed since the earlier run: the earlier result was discarded and everything runs again.</summary>
    Restart,

    /// <summary>The input file is gone or moved: the document is marked failed.</summary>
    Fail,
}

public sealed record ResumeDecision(DocumentRecord Document, ResumeAction Action);

/// <summary>
/// Decides, for each document of an interrupted job, what a resume does (FR-015, data-model "state transitions").
/// Finished steps and pages are never done again, unless the input file changed.
/// </summary>
public static class ResumePlanner
{
    public const string RestartedCode = "RestartedInputChanged";

    /// <summary>
    /// Decides and applies the changes to the documents (statuses, discarded results). The caller saves them.
    /// </summary>
    /// <param name="documents">The saved documents of the job.</param>
    /// <param name="mode">Resume what is unfinished, or retry what failed.</param>
    /// <param name="step2">Whether step 2 is selected now (it may differ from the earlier run).</param>
    /// <param name="step3">Whether step 3 is selected now.</param>
    public static IReadOnlyList<ResumeDecision> Plan(
        IReadOnlyList<DocumentRecord> documents,
        ResumeMode mode,
        bool step2,
        bool step3)
    {
        var decisions = new List<ResumeDecision>(documents.Count);
        foreach (var document in documents)
        {
            decisions.Add(new ResumeDecision(document, Decide(document, mode, step2, step3)));
        }

        return decisions;
    }

    private static ResumeAction Decide(DocumentRecord document, ResumeMode mode, bool step2, bool step3)
    {
        // A run that died leaves "running" behind: it was interrupted.
        document.Step1 = Settle(document.Step1);
        document.Step2 = Settle(document.Step2);
        document.Step3 = Settle(document.Step3);

        var wasFailed = document.IsFailed();
        var wasFinished = document.IsDone();

        // The earlier run's input: gone, moved, or changed.
        if (!File.Exists(document.SourcePath))
        {
            if (wasFinished && mode == ResumeMode.RetryFailed)
            {
                return ResumeAction.Skip;
            }

            document.Step1 = StepStatus.Failed;
            document.Error = new DocumentError(1, Loc.T("The input file was not found. It may have been deleted or moved since the earlier run."));
            return ResumeAction.Fail;
        }

        if (StateStore.HasChanged(document.Fingerprint, document.SourcePath))
        {
            if (mode == ResumeMode.RetryFailed && !wasFailed)
            {
                return ResumeAction.Skip;
            }

            DiscardResult(document, step2, step3);
            document.Warnings.Add(new Warning(
                RestartedCode,
                Loc.T("The input file changed since the earlier run, so the earlier result was discarded and the file is converted again.")));
            return ResumeAction.Restart;
        }

        // The step selection of this run (it may differ from the earlier run).
        var selectionChanged = ApplySelection(document, step2, step3);

        if (mode == ResumeMode.RetryFailed)
        {
            if (!wasFailed)
            {
                // A finished file whose pages failed in Step3: only those pages are read again (FR-013b).
                if (step3 && ResetFailedPages(document))
                {
                    return ResumeAction.Run;
                }

                return selectionChanged ? ResumeAction.Run : ResumeAction.Skip;
            }

            // Failed again from the failed step; the earlier error is cleared.
            document.Error = null;
            ResetFailedSteps(document);
            return ResumeAction.Run;
        }

        if (wasFailed)
        {
            return ResumeAction.Skip; // plain resume leaves failed files alone
        }

        return document.IsDone() ? ResumeAction.Skip : ResumeAction.Run;
    }

    private static StepStatus Settle(StepStatus status) =>
        status is StepStatus.Running or StepStatus.Interrupted ? StepStatus.Pending : status;

    /// <summary>Starts the document again: the earlier pages, text, and output files are dropped.</summary>
    private static void DiscardResult(DocumentRecord document, bool step2, bool step3)
    {
        DeleteOwnOutput(document);
        document.Step1 = StepStatus.Pending;
        document.Step2 = step2 ? StepStatus.Pending : StepStatus.NotApplicable;
        document.Step3 = step3 ? StepStatus.Pending : StepStatus.NotApplicable;
        document.Pages = [];
        document.Blocks = [];
        document.UsageTotal = null;
        document.Error = null;
        document.Warnings = [];
        try
        {
            document.Fingerprint = StateStore.ComputeFingerprint(document.SourcePath);
        }
        catch (FileNotFoundException)
        {
            // Checked by the caller before; a race with a deletion is reported at the next run.
        }
    }

    /// <summary>
    /// A step that is selected now and was not before becomes pending; one that is not selected now is switched
    /// off. Returns true if anything changed.
    /// </summary>
    private static bool ApplySelection(DocumentRecord document, bool step2, bool step3)
    {
        var changed = false;
        document.Step2 = Apply(document.Step2, step2, ref changed);
        document.Step3 = Apply(document.Step3, step3, ref changed);
        return changed;
    }

    private static StepStatus Apply(StepStatus step, bool selected, ref bool changed)
    {
        if (selected && step == StepStatus.NotApplicable)
        {
            changed = true;
            return StepStatus.Pending;
        }

        if (!selected && step != StepStatus.NotApplicable)
        {
            changed = true;
            return StepStatus.NotApplicable;
        }

        return step;
    }

    /// <summary>Makes the pages whose VLM reading failed pending again, so Step3 reads only those. True if there were any.</summary>
    private static bool ResetFailedPages(DocumentRecord document)
    {
        var failed = document.Pages.Where(p => p.Step3 == PageStep3Status.Failed).ToList();
        if (failed.Count == 0)
        {
            return false;
        }

        foreach (var page in failed)
        {
            page.Step3 = PageStep3Status.Pending;
            page.Error = null;
        }

        document.Warnings = [.. document.Warnings.Where(w => w.Code != "VlmPageFailed")];
        document.Step3 = StepStatus.Pending;
        return true;
    }

    private static void ResetFailedSteps(DocumentRecord document)
    {
        if (document.Step1 == StepStatus.Failed)
        {
            document.Step1 = StepStatus.Pending;
        }

        if (document.Step2 == StepStatus.Failed)
        {
            document.Step2 = StepStatus.Pending;
        }

        if (document.Step3 == StepStatus.Failed)
        {
            document.Step3 = StepStatus.Pending;
        }
    }

    /// <summary>Removes the Markdown file and page images this job wrote for the document. Nothing else is touched.</summary>
    private static void DeleteOwnOutput(DocumentRecord document)
    {
        try
        {
            var markdown = Path.Combine(
                document.OutputFolder,
                Markdown.MarkdownRenderer.MarkdownFileName(Path.GetFileNameWithoutExtension(document.SourcePath)));
            if (File.Exists(markdown))
            {
                File.Delete(markdown);
            }

            var images = Path.Combine(document.OutputFolder, "images");
            if (Directory.Exists(images))
            {
                foreach (var file in Directory.EnumerateFiles(images, "page-*.png"))
                {
                    File.Delete(file);
                }
            }

            AtomicFile.DeleteStaleTemps(document.OutputFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Files that cannot be removed are overwritten by the new run.
        }
    }
}
