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
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Jobs;

/// <summary>The earlier run found in an output folder (FR-013e).</summary>
/// <param name="Job">The job.</param>
/// <param name="Documents">Its documents, with the state that was saved.</param>
/// <param name="CanResume">The job was interrupted (or the app died): "resume" can run it.</param>
/// <param name="CanRetryFailed">Some file or page failed: "retry failed only" can run it.</param>
public sealed record PreviousRun(JobState Job, IReadOnlyList<DocumentRecord> Documents, bool CanResume, bool CanRetryFailed)
{
    public int DoneCount => Documents.Count(d => d.IsDone());

    public int FailedCount => Documents.Count(d => d.IsFailed());

    public int UnfinishedCount => Documents.Count - DoneCount - FailedCount;
}

/// <summary>
/// Looks in an output folder for the newest run that can be resumed or has failures (none when the newest run finished without failures), so the screen can show it when it
/// starts and enable "resume" and "retry failed only" only when they can run (FR-013d, FR-013e). When inputs are given, only the runs of
/// those same inputs count (FR-130: the list of the screen shows the earlier conversion of the inputs of the screen); without them, every run
/// of the folder counts (FR-013f).
/// </summary>
public static class PreviousRunFinder
{
    public static PreviousRun? Find(string? outputFolder, IReadOnlyCollection<string>? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(outputFolder))
            {
                return null;
            }

            var store = new StateStore(outputFolder);
            // The newest run decides: when it finished without a failure, the earlier runs that were interrupted or had failures are
            // done with (a later run of other files has gone through), and nothing is shown.
            if (store.ListJobs().FirstOrDefault(j => j.Status != JobStatus.Abandoned && HasInputs(j, inputs)) is { Status: JobStatus.Completed })
            {
                return null;
            }

            // An interrupted run comes first; without one, the newest run that finished with failures.
            var job = store.FindResumableJobs(inputs, includeFinishedWithErrors: false).FirstOrDefault()
                ?? store.FindResumableJobs(inputs, includeFinishedWithErrors: true).FirstOrDefault();
            if (job is null)
            {
                return null;
            }

            return ForJob(store, job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // an output folder that cannot be read has no earlier run to show
        }
    }

    /// <summary>
    /// The newest run of the output folder when it finished without a failure (the case in which <see cref="Find"/> has nothing): the screen
    /// shows its files, so that what was converted is seen and "changed files only" can be chosen (FR-129). Nothing of it can be resumed.
    /// </summary>
    public static PreviousRun? LatestCompleted(string? outputFolder, IReadOnlyCollection<string>? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(outputFolder))
            {
                return null;
            }

            var store = new StateStore(outputFolder);
            return store.ListJobs().FirstOrDefault(j => j.Status != JobStatus.Abandoned && HasInputs(j, inputs)) is { Status: JobStatus.Completed } job
                ? ForJob(store, job)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The job is a run of these inputs (every job, when no inputs are given).</summary>
    private static bool HasInputs(JobState job, IReadOnlyCollection<string>? inputs) =>
        inputs is not { Count: > 0 }
        || StateStore.NormalizeInputs(job.Inputs).SequenceEqual(StateStore.NormalizeInputs(inputs), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The run of one job, with its documents. <c>null</c> when the saved state of a document is missing (the job cannot be resumed). A job that was
    /// given up can be resumed here too, because the caller names it (FR-121).
    /// </summary>
    public static PreviousRun? ForJob(StateStore store, JobState job)
    {
        var documents = new List<DocumentRecord>();
        foreach (var docId in job.Documents)
        {
            // A job whose state is partly missing cannot be resumed, so it is not offered.
            var document = store.LoadDocument(job.JobId, docId);
            if (document is null)
            {
                return null;
            }

            documents.Add(document);
        }

        return new PreviousRun(
            job,
            documents,
            CanResume: store.IsResumable(job, includeFinishedWithErrors: false, includeAbandoned: true),
            CanRetryFailed: documents.Any(d => d.IsFailed() || d.HasFailedPages()));
    }

    /// <summary>
    /// Every run of the output folder that can be resumed or retried, also the ones that were given up when a later run was started (FR-121),
    /// newest first. A run that is running in another process is left out.
    /// </summary>
    public static IReadOnlyList<PreviousRun> Resumable(string outputFolder)
    {
        try
        {
            var store = new StateStore(outputFolder);
            return
            [
                .. store.ListJobs()
                    .Where(job => store.IsResumable(job, includeFinishedWithErrors: true, includeAbandoned: true))
                    .Select(job => ForJob(store, job))
                    .OfType<PreviousRun>()
                    .Where(run => run.CanResume || run.CanRetryFailed),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
