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

using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Jobs;

/// <summary>A document of an earlier run, found in an output folder (FR-119).</summary>
/// <param name="Job">The run the document belongs to.</param>
/// <param name="Document">The saved state; its <see cref="DocumentRecord.OutputFolder"/> is the folder as it is now (FR-120).</param>
/// <param name="FolderFound">The document folder exists. When it does not, the preview cannot be opened.</param>
public sealed record PastDocument(JobState Job, DocumentRecord Document, bool FolderFound)
{
    /// <summary>The preview needs the pages (Step 1 has made them) and the folder.</summary>
    public bool CanPreview => FolderFound && Document.Pages.Count > 0;
}

/// <summary>Finds the output of earlier runs and reads it, to preview it later (FR-119, FR-120). Reads only; nothing is written.</summary>
public static class PastOutputs
{
    /// <summary>How many folders above the chosen one are searched for <c>.markporto</c>.</summary>
    public const int ParentLevels = 3;

    /// <summary>The output folder (the one that has <c>.markporto</c>) for a chosen folder: itself, or the nearest parent. <c>null</c> when there is none.</summary>
    public static string? FindRoot(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        string? current;
        try
        {
            current = Path.GetFullPath(folder);
        }
        catch (ArgumentException)
        {
            return null;
        }

        for (var level = 0; level <= ParentLevels && current is not null; level++)
        {
            if (Directory.Exists(Path.Combine(current, StateStore.StateFolderName, "jobs")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// Every document with pages in the runs of the output folder, newest run first. A run whose files cannot be read is skipped.
    /// </summary>
    public static IReadOnlyList<PastDocument> Load(string outputRoot)
    {
        var store = new StateStore(outputRoot);
        var result = new List<PastDocument>();
        foreach (var job in store.ListJobs())
        {
            foreach (var docId in job.Documents)
            {
                DocumentRecord? document;
                try
                {
                    document = store.LoadDocument(job.JobId, docId);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    continue;
                }

                if (document is null)
                {
                    continue;
                }

                var folder = ResolveFolder(store.OutRoot, document);
                if (folder is not null)
                {
                    document.OutputFolder = folder;
                }

                result.Add(new PastDocument(job, document, folder is not null));
            }
        }

        return result;
    }

    /// <summary>
    /// Where the document folder is now (FR-120): the saved path when it exists; else the path from the output folder (<c>outputFolderRel</c>);
    /// else, for a state without it, the longest tail of the saved path that exists below the output folder. <c>null</c> when it is not found.
    /// </summary>
    public static string? ResolveFolder(string outputRoot, DocumentRecord document)
    {
        if (document.OutputFolder.Length > 0 && Directory.Exists(document.OutputFolder))
        {
            return document.OutputFolder;
        }

        if (!string.IsNullOrEmpty(document.OutputFolderRel) && !Path.IsPathRooted(document.OutputFolderRel))
        {
            var candidate = Path.GetFullPath(Path.Combine(outputRoot, document.OutputFolderRel));
            return IsUnder(outputRoot, candidate) && Directory.Exists(candidate) ? candidate : null;
        }

        var parts = document.OutputFolder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var count = parts.Length - 1; count >= 1; count--)
        {
            var candidate = Path.GetFullPath(Path.Combine([outputRoot, .. parts[^count..]]));
            if (IsUnder(outputRoot, candidate) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
