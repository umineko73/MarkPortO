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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MarkPort.Core.Engines;
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Ooxml;
using MarkPort.Core.Pipeline;
using MarkPort.Core.Vlm;

namespace MarkPort.Core.Convert;

/// <summary>
/// Runs a conversion: plans the job and the output folders, runs the steps as a pipeline, and keeps the state
/// that lets an interrupted run be resumed. The screen and the command line both call this class (constitution II).
/// </summary>
public sealed class ConvertService
{
    private readonly IDocumentEngine _engine;
    private readonly ILogger _logger;
    private readonly TimeSpan _engineTimeout;
    private readonly VlmSetup? _vlm;

    /// <param name="vlm">The VLM connection, needed when step 3 is on. It is not disposed by this class.</param>
    public ConvertService(IDocumentEngine engine, ILogger? logger = null, TimeSpan? engineTimeout = null, VlmSetup? vlm = null)
    {
        _vlm = vlm;
        _engine = engine;
        _logger = logger ?? NullLogger.Instance;
        _engineTimeout = engineTimeout ?? PdfExportRequest.DefaultTimeout;
    }

    /// <summary>Raised from worker threads whenever a document changes (a step starts, ends, or fails).</summary>
    public event Action<DocumentRecord>? DocumentChanged;

    /// <summary>Raised on a resume for each document that is not run again (finished, or failed and not retried).</summary>
    public event Action<DocumentRecord>? DocumentSkipped;

    /// <summary>
    /// Raised once, before the steps start, with every document of the run in its starting state. A screen uses it
    /// to fill the file list, so files that have not started yet are shown as waiting.
    /// </summary>
    public event Action<IReadOnlyList<DocumentRecord>>? Planned;

    /// <summary>Converts the documents of the request.</summary>
    /// <exception cref="ConvertStartException">The run cannot start; nothing has been written.</exception>
    public async Task<ConvertResult> RunAsync(ConvertRequest request, CancellationToken cancellationToken = default)
    {
        CheckCanStart(request);
        await PreflightAsync(request.Options.Step3, cancellationToken).ConfigureAwait(false);

        var store = new StateStore(request.OutputFolder);
        var (job, documents) = Plan(request, store);
        store.SaveJob(job);
        foreach (var document in documents)
        {
            store.SaveDocument(job.JobId, document);
        }

        Planned?.Invoke(documents);

        // Documents that already failed (for example the file was not found) are not run.
        var toRun = documents.Where(d => !d.IsDone() && !d.IsFailed()).ToList();
        return await ExecuteAsync(job, documents, toRun, store, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes an interrupted job, or retries its failed files (FR-015). Finished files and pages are not done again;
    /// a file whose input changed starts over. The output goes to the same folders as before.
    /// </summary>
    /// <exception cref="ConvertStartException">There is no job to resume, or the engine cannot be used.</exception>
    public async Task<ConvertResult> ResumeAsync(ResumeRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OutputFolder) || !Directory.Exists(request.OutputFolder))
        {
            throw new JobNotFoundException(Loc.T("The output folder was not found: {0}", request.OutputFolder));
        }

        var store = new StateStore(request.OutputFolder);
        var retry = request.Mode == ResumeMode.RetryFailed;
        JobState job;
        if (request.JobId is { } id)
        {
            job = store.LoadJob(id) ?? throw new JobNotFoundException(Loc.T("The job {0} was not found in {1}.", id, request.OutputFolder));
            if (!store.IsResumable(job, includeFinishedWithErrors: retry))
            {
                throw new JobNotFoundException(
                    retry
                    ? Loc.T("The job {0} cannot be resumed (it is running, or finished without failures).", id)
                    : Loc.T("The job {0} cannot be resumed (it is running, or finished).", id));
            }
        }
        else
        {
            job = store.FindResumableJobs(request.GivenInputs, includeFinishedWithErrors: retry).FirstOrDefault()
                ?? throw new JobNotFoundException(Loc.T("There is no job to resume in {0}.", request.OutputFolder));
        }

        var availability = _engine.CheckAvailability();
        if (!availability.IsAvailable)
        {
            throw new ConvertStartException(availability.Reason ?? Loc.T("The conversion engine is not available."));
        }

        var documents = new List<DocumentRecord>();
        foreach (var docId in job.Documents)
        {
            documents.Add(store.LoadDocument(job.JobId, docId)
                ?? throw new ConvertStartException(Loc.T("The saved state of {0} is missing; the job cannot be resumed.", docId)));
        }

        // The step selection may differ from the earlier run; the other options stay as they were.
        var step2Before = job.Options.Step2;
        var step3Before = job.Options.Step3;
        job.Options.Step2 = request.Step2 ?? job.Options.Step2;
        job.Options.Step3 = request.Step3 ?? job.Options.Step3;
        job.Options.Connection = request.Connection ?? job.Options.Connection;

        // Step 3 needs a working VLM connection; it is checked before any step starts, also on a resume (FR-019c).
        await PreflightAsync(job.Options.Step3, cancellationToken).ConfigureAwait(false);

        var decisions = ResumePlanner.Plan(documents, request.Mode, job.Options.Step2, job.Options.Step3);
        Planned?.Invoke(documents);
        var toRun = new List<DocumentRecord>();
        foreach (var decision in decisions)
        {
            store.SaveDocument(job.JobId, decision.Document);
            if (decision.Action is ResumeAction.Run or ResumeAction.Restart)
            {
                toRun.Add(decision.Document);
            }
            else
            {
                DocumentSkipped?.Invoke(decision.Document);
            }
        }

        // A step switched off or on changes the Markdown of finished files (image references, VLM results).
        if (step2Before != job.Options.Step2 || step3Before != job.Options.Step3)
        {
            foreach (var document in documents.Where(d => d.Step1 == StepStatus.Done && !toRun.Contains(d)))
            {
                Markdown.MarkdownRenderer.Write(document);
            }
        }

        return await ExecuteAsync(job, documents, toRun, store, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the steps for planned documents (the common part of a new run and of a resumed run).</summary>
    private async Task<ConvertResult> ExecuteAsync(
        JobState job,
        IReadOnlyList<DocumentRecord> documents,
        IReadOnlyList<DocumentRecord> toRun,
        StateStore store,
        CancellationToken cancellationToken)
    {
        using var jobLock = store.TryAcquireLock(job.JobId)
            ?? throw new ConvertStartException(Loc.T("This job is already running in another process."));

        var context = new JobContext(job, store, _engine, _logger, _engineTimeout)
        {
            // Every saved change of a document is reported, so a screen or a log can follow each step.
            Changed = document => DocumentChanged?.Invoke(document),
            Vlm = _vlm?.Reader,
            Prompts = _vlm?.Prompts,
            MaxLinesPerRead = _vlm?.MaxLinesPerRead ?? 0,
        };
        var works = toRun.Select(d => new DocumentWork(d, context)).ToList();

        var host = new PipelineHost<DocumentWork>(BuildSteps(job.Options.Step3));
        host.EventRaised += e => DocumentChanged?.Invoke(e.Item.Document);

        job.Status = JobStatus.Running;
        store.SaveJob(job);
        await host.RunAsync(works, cancellationToken).ConfigureAwait(false);

        job.Status = Finish(documents, cancellationToken.IsCancellationRequested);
        store.SaveJob(job);
        return new ConvertResult(job, documents);
    }

    private IReadOnlyList<IPipelineStep<DocumentWork>> BuildSteps(bool step3)
    {
        var parallel = _engine is LibreOfficeEngine libre ? libre.MaxParallel : 1;
        var steps = new List<IPipelineStep<DocumentWork>>
        {
            new Step1Worker { Concurrency = parallel },
            new Step2Worker(),
        };
        if (step3)
        {
            steps.Add(new Step3Worker());
        }

        return steps;
    }

    /// <summary>
    /// The check that runs before step 1 when step 3 is on: the VLM connection must work. If it does not, nothing
    /// is started and nothing is written (FR-019c). The image check is not run here; the user runs it.
    /// </summary>
    private async Task PreflightAsync(bool step3, CancellationToken cancellationToken)
    {
        if (!step3)
        {
            return;
        }

        if (_vlm is null)
        {
            throw new ConvertStartException(Loc.T("Step3 is on, but no VLM connection is set up."));
        }

        CheckResult result;
        try
        {
            result = await _vlm.Preflight(cancellationToken).ConfigureAwait(false);
        }
        catch (VlmException ex)
        {
            result = new CheckResult(false, ex.Message);
        }

        if (!result.Ok)
        {
            throw new ConvertStartException(Loc.T("The VLM connection check failed, so the conversion was not started. {0}", result.Message));
        }
    }

    private static JobStatus Finish(IReadOnlyList<DocumentRecord> documents, bool cancelled)
    {
        var unfinished = documents.Any(d => !d.IsDone() && !d.IsFailed());
        if (cancelled && unfinished)
        {
            return JobStatus.Interrupted;
        }

        return documents.Any(d => d.IsFailed() || d.HasFailedPages()) ? JobStatus.CompletedWithErrors : JobStatus.Completed;
    }

    private void CheckCanStart(ConvertRequest request)
    {
        if (request.Inputs.Count == 0)
        {
            throw new ConvertStartException(Loc.T("There are no documents to convert."));
        }

        if (string.IsNullOrWhiteSpace(request.OutputFolder))
        {
            throw new ConvertStartException(Loc.T("Choose an output folder."));
        }

        foreach (var input in request.Inputs)
        {
            if (OoxmlPackage.KindOf(input.Path) is null)
            {
                throw new ConvertStartException(Loc.T("'{0}' is not a .docx, .xlsx or .pptx file.", input.Path));
            }
        }

        var availability = _engine.CheckAvailability();
        if (!availability.IsAvailable)
        {
            throw new ConvertStartException(availability.Reason ?? Loc.T("The conversion engine is not available."));
        }
    }

    /// <summary>
    /// Decides the job: the output folders (with " (2)" where a name is taken) and the starting state of every document.
    /// Names are decided once, here, and saved, so a resumed run writes to the same places.
    /// </summary>
    public static (JobState Job, List<DocumentRecord> Documents) Plan(ConvertRequest request, StateStore store)
    {
        var outRoot = Path.GetFullPath(request.OutputFolder);
        Directory.CreateDirectory(outRoot);

        var allocator = new OutputNamer.Allocator();
        var outputRoot = request.InputFolder is { } inputFolder
            ? allocator.Allocate(outRoot, ConvertRequest.FolderName(inputFolder))
            : outRoot;

        var options = request.Options;
        var documents = new List<DocumentRecord>();
        var ordered = request.Inputs
            .OrderBy(i => i.RelativeDirectory, StringComparer.Ordinal)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var input = ordered[i];
            var parent = input.RelativeDirectory.Length == 0
                ? outputRoot
                : Path.Combine([outputRoot, .. input.RelativeDirectory.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(OutputNamer.Sanitize)]);

            var record = new DocumentRecord
            {
                DocId = $"d{i + 1:0000}",
                SourcePath = Path.GetFullPath(input.Path),
                MarkdownLanguage = options.MarkdownLanguage,
                DisplayName = input.RelativeDirectory.Length == 0
                    ? Path.GetFileName(input.Path)
                    : Path.Combine(input.RelativeDirectory.Replace('/', '\\'), Path.GetFileName(input.Path)),
                Kind = OoxmlPackage.KindOf(input.Path)!.Value,
                OutputFolder = allocator.Allocate(parent, Path.GetFileNameWithoutExtension(input.Path)),
                Step2 = options.Step2 ? StepStatus.Pending : StepStatus.NotApplicable,
                Step3 = options.Step3 ? StepStatus.Pending : StepStatus.NotApplicable,
            };

            try
            {
                record.Fingerprint = StateStore.ComputeFingerprint(record.SourcePath);
            }
            catch (FileNotFoundException)
            {
                record.Step1 = StepStatus.Failed;
                record.Error = new DocumentError(1, Loc.T("The input file was not found."));
            }

            documents.Add(record);
        }

        SeparateSameDisplayNames(documents);

        var given = request.GivenInputs.Count > 0 ? request.GivenInputs : request.Inputs.Select(i => i.Path).ToList();
        var now = DateTime.UtcNow;
        var job = new JobState
        {
            JobId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            Status = JobStatus.Running,
            Inputs = [.. StateStore.NormalizeInputs(given)],
            InputRoot = request.InputFolder is { } folder ? Path.GetFullPath(folder) : string.Empty,
            OutputRoot = outputRoot,
            Options = options,
            Documents = [.. documents.Select(d => d.DocId)],
        };

        return (job, documents);
    }

    /// <summary>
    /// Files that were named one by one (no folder part in the display name) and show the same text get the name of their
    /// parent folder in front, so the list tells them apart (FR-013a).
    /// </summary>
    private static void SeparateSameDisplayNames(List<DocumentRecord> documents)
    {
        foreach (var group in documents.GroupBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var document in group.Where(d => !d.DisplayName.Contains('\\') && !d.DisplayName.Contains('/')))
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(document.SourcePath)?.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(parent))
                {
                    document.DisplayName = Path.Combine(parent, document.DisplayName);
                }
            }
        }
    }
}
