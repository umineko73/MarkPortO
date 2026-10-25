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

using MarkPort.Core.Convert;
using MarkPort.Core.Jobs.Models;

namespace MarkPort.Core.Jobs;

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
/// Looks in an output folder for the newest run that can be resumed or has failures, so the screen can show it when it
/// starts and enable "resume" and "retry failed only" only when they can run (FR-013d, FR-013e). The input list of the
/// screen plays no part (FR-013f).
/// </summary>
public static class PreviousRunFinder
{
    public static PreviousRun? Find(string? outputFolder)
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
            // An interrupted run comes first; without one, the newest run that finished with failures.
            var job = store.FindResumableJobs(null, includeFinishedWithErrors: false).FirstOrDefault()
                ?? store.FindResumableJobs(null, includeFinishedWithErrors: true).FirstOrDefault();
            if (job is null)
            {
                return null;
            }

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
                CanResume: store.IsResumable(job, includeFinishedWithErrors: false),
                CanRetryFailed: documents.Any(d => d.IsFailed() || d.HasFailedPages()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // an output folder that cannot be read has no earlier run to show
        }
    }
}
