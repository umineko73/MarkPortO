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
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Jobs;

/// <summary>What the list shows for a file of the inputs, from its last record in the output folder (FR-157).</summary>
public enum FileState
{
    /// <summary>No record: not converted into this output folder yet.</summary>
    NotConverted,

    /// <summary>Finished.</summary>
    Done,

    /// <summary>Finished, but Step3 could not read some pages (FR-013b).</summary>
    DonePartlyFailed,

    /// <summary>The conversion of the file failed.</summary>
    Failed,

    /// <summary>Interrupted, or the run died: it can go on from where it stopped.</summary>
    Unfinished,

    /// <summary>Finished without Step2 or Step3, which is on now: only that step is to run (FR-157, adding a step).</summary>
    StepPending,

    /// <summary>The source changed since its last conversion: it is converted again from Step1 (FR-015).</summary>
    Changed,

    /// <summary>Its last conversion is being run by another process (another window, the CLI): no action takes it.</summary>
    RunningElsewhere,
}

/// <summary>A file of the inputs, and its last record in the output folder when it has one.</summary>
/// <param name="Path">The full path of the source.</param>
/// <param name="DisplayName">What the list shows: the path from the given folder, or the file name.</param>
/// <param name="Kind">The kind of the file.</param>
/// <param name="Job">The job of its last record; <c>null</c> when it has none.</param>
/// <param name="Document">Its last record; <c>null</c> when it has none.</param>
/// <param name="State">What the list shows.</param>
public sealed record FileEntry(string Path, string DisplayName, DocumentKind Kind, JobState? Job, DocumentRecord? Document, FileState State)
{
    /// <summary>The steps that adding a step runs (Step2, Step3) for <see cref="FileState.StepPending"/>.</summary>
    public bool AddsStep2 { get; init; }

    public bool AddsStep3 { get; init; }

    /// <summary>
    /// "Resume" goes on with it (FR-157): go on, retry, add a step; a changed file that was not finished is converted again from Step1.
    /// </summary>
    public bool CanResume =>
        State is FileState.Unfinished or FileState.Failed or FileState.DonePartlyFailed or FileState.StepPending
        || (State == FileState.Changed && Document is { } document && !document.IsDone());
}

/// <summary>
/// The list of the inputs as files, each with its last record in the output folder (FR-157): always per file, never per set of inputs.
/// The last record of a file is the newest job that has it (an abandoned or interrupted one too) whose document folder is still there.
/// Reads only.
/// </summary>
public static class FileStates
{
    /// <summary>The last record of every source file of the output folder, by its full path (case-insensitive).</summary>
    public static IReadOnlyDictionary<string, (JobState Job, DocumentRecord Document)> LastRecords(string outputFolder)
    {
        var last = new Dictionary<string, (JobState, DocumentRecord)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
        {
            return last;
        }

        var store = new StateStore(outputFolder);
        foreach (var job in store.ListJobs().Where(j => j is not null).OrderByDescending(j => j.CreatedAt)) // the newest conversion first
        {
            foreach (var docId in job.Documents ?? []) // a broken record (no list of documents) has none
            {
                if (string.IsNullOrEmpty(docId))
                {
                    continue;
                }

                DocumentRecord? document;
                try
                {
                    document = store.LoadDocument(job.JobId, docId);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    continue;
                }

                if (document is null || string.IsNullOrEmpty(document.SourcePath))
                {
                    continue;
                }

                string key;
                try
                {
                    key = System.IO.Path.GetFullPath(document.SourcePath);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (last.ContainsKey(key))
                {
                    continue; // a newer job has it
                }

                // A document whose Step1 did not end yet has no output to lose: a resume converts it into its folder. One with Step1 done needs its folder.
                var canResumeWithoutOutput = document.Step1 != StepStatus.Done && !document.IsDone() && job.Status != JobStatus.Completed;
                if (PastOutputs.ResolveFolderInRoot(store.OutRoot, document, canResumeWithoutOutput) is not { } folder)
                {
                    continue; // its folder is gone (a document not finished keeps its record: a resume writes it again): an older record may still have one
                }

                document.OutputFolder = folder;
                last[key] = (job, document);
            }
        }

        return last;
    }

    /// <summary>
    /// The last records of the files under the given inputs (a file itself, or the files below a folder), whatever their kind: the first
    /// step of FR-157, before the conditions are known.
    /// </summary>
    public static IReadOnlyList<(JobState Job, DocumentRecord Document)> RecordsUnder(string outputFolder, IReadOnlyList<string> inputs) =>
        RecordsUnder(LastRecords(outputFolder), inputs);

    /// <summary>The same, from records already read (<see cref="LastRecords"/>).</summary>
    public static IReadOnlyList<(JobState Job, DocumentRecord Document)> RecordsUnder(
        IReadOnlyDictionary<string, (JobState Job, DocumentRecord Document)> records,
        IReadOnlyList<string> inputs)
    {
        var roots = inputs.Select(Full).OfType<string>().ToList();
        return [.. records.Where(r => roots.Any(root => IsSameOrUnder(r.Key, root))).Select(r => r.Value)];
    }

    /// <summary>
    /// The files of the inputs with the conditions of the screen (kinds, subfolders, layout), each with its state in the output folder.
    /// </summary>
    /// <param name="step2">Step2 is on now (a record without it is <see cref="FileState.StepPending"/>).</param>
    /// <param name="step3">Step3 is on now.</param>
    public static IReadOnlyList<FileEntry> Find(
        string outputFolder,
        IReadOnlyList<string> inputs,
        IReadOnlyCollection<DocumentKind> types,
        bool recurse,
        FolderLayout layout,
        bool step2,
        bool step3)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        var found = InputEnumerator.Enumerate(inputs, types, recurse, layout);
        var records = LastRecords(outputFolder);
        var store = string.IsNullOrWhiteSpace(outputFolder) ? null : new StateStore(outputFolder);
        var result = new List<FileEntry>(found.Inputs.Count);
        foreach (var input in found.Inputs)
        {
            var path = Full(input.Path) ?? input.Path;
            var display = string.IsNullOrEmpty(input.RelativeDirectory)
                ? System.IO.Path.GetFileName(path)
                : System.IO.Path.Combine(input.RelativeDirectory, System.IO.Path.GetFileName(path));
            var kind = KindOf(path);
            if (!records.TryGetValue(path, out var record))
            {
                result.Add(new FileEntry(path, display, kind, null, null, FileState.NotConverted));
                continue;
            }

            var (job, document) = record;
            result.Add(Entry(path, display, kind, job, document, store, step2, step3));
        }

        return result;
    }

    private static FileEntry Entry(string path, string display, DocumentKind kind, JobState job, DocumentRecord document, StateStore? store, bool step2, bool step3)
    {
        FileEntry Make(FileState state) => new(path, display, kind, job, document, state);

        if (store is not null && store.IsLocked(job.JobId))
        {
            return Make(FileState.RunningElsewhere);
        }

        if (StateStore.HasChanged(document.Fingerprint, path))
        {
            return Make(FileState.Changed); // before adding a step: the page images are of the old file (FR-015)
        }

        if (document.IsFailed())
        {
            return Make(FileState.Failed);
        }

        if (!document.IsDone())
        {
            return Make(FileState.Unfinished);
        }

        var adds2 = step2 && document.Step2 == StepStatus.NotApplicable;
        var adds3 = step3 && document.Step3 == StepStatus.NotApplicable;
        if (adds2 || adds3)
        {
            return Make(FileState.StepPending) with { AddsStep2 = adds2, AddsStep3 = adds3 };
        }

        return Make(document.Pages.Any(p => p.Step3 == PageStep3Status.Failed) ? FileState.DonePartlyFailed : FileState.Done);
    }

    /// <summary>The text of the state in the list.</summary>
    public static string Text(FileEntry entry) => entry.State switch
    {
        FileState.NotConverted => Loc.T("Not converted"),
        FileState.Done => Loc.T("Done"),
        FileState.DonePartlyFailed => Loc.T("Done (some pages failed)"),
        FileState.Failed => Loc.T("Failed"),
        FileState.Unfinished => Loc.T("Not finished"),
        FileState.StepPending => entry.AddsStep2 ? Loc.T("Step2 not run") : Loc.T("Step3 not run"),
        FileState.Changed => Loc.T("Changed since the conversion"),
        FileState.RunningElsewhere => Loc.T("Running elsewhere"),
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };

    /// <summary>
    /// The files that "Resume" takes (FR-157): those it goes on with, and the files not converted yet when the list has records too.
    /// A file that another process runs is never taken.
    /// </summary>
    public static IReadOnlyList<FileEntry> ToResume(IReadOnlyList<FileEntry> entries)
    {
        var resumable = entries.Where(e => e.CanResume).ToList();
        var hasRecords = entries.Any(e => e.Document is not null && e.State != FileState.RunningElsewhere);
        var fresh = hasRecords ? entries.Where(e => e.State == FileState.NotConverted) : [];
        return resumable.Count == 0 && !fresh.Any() ? [] : [.. resumable, .. fresh];
    }

    /// <summary>
    /// Whether a "Resume" of these files (<see cref="ToResume"/>) reads pages with the VLM: a file not converted with Step3 on, a file that
    /// gets Step3 (on now) or has it unfinished, failed pages, or is converted again from Step1 with it. Step3 on the screen is not enough
    /// to tell: a file goes on with its own steps (FR-157), so the question before sending (FR-114) asks also when it is off.
    /// </summary>
    public static bool ReadsStep3(IEnumerable<FileEntry> entries, bool step3On) => entries.Any(e =>
    {
        if (e.Document is not { } document)
        {
            return e.State == FileState.NotConverted && step3On;
        }

        if (!step3On && document.Step3 == StepStatus.NotApplicable)
        {
            return false;
        }

        return e.State is FileState.Changed or FileState.DonePartlyFailed or FileState.Failed
            || document.Step3 != StepStatus.Done
            || document.Pages.Any(p => p.Step3 == PageStep3Status.Failed);
    });

    private static DocumentKind KindOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xlsx" => DocumentKind.Xlsx,
        ".pptx" => DocumentKind.Pptx,
        ".pdf" => DocumentKind.Pdf,
        _ => DocumentKind.Docx,
    };

    private static string? Full(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSameOrUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
