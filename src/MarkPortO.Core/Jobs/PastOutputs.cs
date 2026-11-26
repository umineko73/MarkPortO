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
using System.Text.Json;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Jobs;

/// <summary>A document of an earlier run, found in an output folder (FR-119).</summary>
/// <param name="Job">The run the document belongs to.</param>
/// <param name="Document">The saved state; its <see cref="DocumentRecord.OutputFolder"/> is the folder as it is now (FR-120).</param>
/// <param name="FolderFound">The document folder exists. When it does not, the preview cannot be opened.</param>
/// <param name="FromRecordOnly">
/// Read from <c>conversion.json</c> alone (FR-155): the folder has no <c>.markporto</c>, so the pages are known by their images and the
/// conversion cannot be resumed.
/// </param>
public sealed record PastDocument(JobState Job, DocumentRecord Document, bool FolderFound, bool FromRecordOnly = false)
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
    /// The documents of a folder that has no <c>.markporto</c> (copied, or its state removed), read from the <c>conversion.json</c> of
    /// each document folder under it (FR-155), newest first. A record that cannot be read is skipped. Reads only.
    /// </summary>
    public static IReadOnlyList<PastDocument> LoadRecords(string folder)
    {
        var result = new List<PastDocument>();
        string root;
        List<string> files;
        try
        {
            root = Path.GetFullPath(folder);
            files = [.. Directory.EnumerateFiles(root, Markdown.ConversionRecord.FileName, SearchOption.AllDirectories)
                .Where(f => !f.Split(Path.DirectorySeparatorChar).Contains(StateStore.StateFolderName, StringComparer.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return result;
        }

        foreach (var file in files)
        {
            try
            {
                if (FromRecord(file, root) is { } past)
                {
                    result.Add(past);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                // broken, or not a record of MarkPortO: left out
            }
        }

        return [.. result.OrderByDescending(p => p.Job.UpdatedAt)];
    }

    private static PastDocument? FromRecord(string file, string chosenFolder)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var r = json.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || version.GetInt32() > Markdown.ConversionRecord.SchemaVersion)
        {
            return null;
        }

        var folder = Path.GetDirectoryName(file)!;
        var source = r.GetProperty("source");
        var steps = r.GetProperty("steps");
        var c = r.GetProperty("conditions");
        var files = r.GetProperty("files");
        var updated = Time(r, "updatedAt") ?? File.GetLastWriteTimeUtc(file);
        var job = new JobState
        {
            JobId = Guid.TryParse(Text(r, "jobId"), out var id) ? id : Guid.NewGuid(),
            CreatedAt = Time(r, "startedAt") ?? updated,
            UpdatedAt = updated,
            OutputRoot = chosenFolder,
            Status = Text(r, "status") switch
            {
                "completed" => JobStatus.Completed,
                "failed" => JobStatus.CompletedWithErrors,
                _ => JobStatus.Interrupted,
            },
        };
        var document = new DocumentRecord
        {
            DocId = Text(r, "docId") ?? "d0001",
            SourcePath = Text(source, "path") ?? string.Empty,
            DisplayName = (Text(source, "relativePath") ?? string.Empty).Replace('/', Path.DirectorySeparatorChar),
            Kind = Enum.TryParse<DocumentKind>(Text(source, "kind"), ignoreCase: true, out var kind) ? kind : DocumentKind.Docx,
            OutputFolder = folder,
            Step1 = Step(steps, "step1"),
            Step2 = Step(steps, "step2"),
            Step3 = Step(steps, "step3"),
            Preset = Text(c, "preset"),
            VlmSeparate = Flag(c, "vlmSeparate"),
            Chunks = Flag(c, "chunks"),
            ExcelPerSheet = Flag(c, "excelPerSheet"),
            StructureNote = Flag(c, "structureNote"),
            Extractor = Text(c, "extractor"),
            MarkdownLanguage = Text(c, "markdownLanguage") ?? "en",
            VlmModel = c.TryGetProperty("step3Connection", out var connection) && connection.ValueKind == JsonValueKind.Object ? Text(connection, "model") : null,
        };
        if (Enum.TryParse<PageImageStyle>(Text(c, "pageImage"), ignoreCase: true, out var pageImage))
        {
            document.PageImage = pageImage;
        }

        if (Enum.TryParse<Step3Mode>(Text(c, "step3Mode"), ignoreCase: true, out var mode))
        {
            document.Step3Mode = mode;
        }

        if (r.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var step = Text(error, "step") is { Length: > 4 } s && int.TryParse(s[4..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
            document.Error = new DocumentError(step, Text(error, "message") ?? string.Empty);
        }

        // The pages are known by their images; the sheets of a book written per sheet, by the record (contracts/conversion-json.md).
        var sheetOf = new Dictionary<int, string>();
        if (files.TryGetProperty("sheets", out var sheets) && sheets.ValueKind == JsonValueKind.Array)
        {
            foreach (var sheet in sheets.EnumerateArray())
            {
                foreach (var page in sheet.GetProperty("pages").EnumerateArray())
                {
                    sheetOf[page.GetInt32()] = Text(sheet, "sheet") ?? string.Empty;
                }
            }
        }

        var images = Path.Combine(folder, "images");
        if (Directory.Exists(images))
        {
            foreach (var image in Directory.EnumerateFiles(images, "page-*.png").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(image);
                if (int.TryParse(name["page-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    document.Pages.Add(new PageRecord
                    {
                        Index = index,
                        ImageFile = "images/" + Path.GetFileName(image),
                        Sheet = sheetOf.TryGetValue(index, out var sheetName) ? new SheetInfo(sheetName, string.Empty, 1, 1) : null,
                    });
                }
            }
        }

        if (document.ExcelPerSheet && sheetOf.Count > 0)
        {
            document.SheetFiles = [.. Markdown.MarkdownRenderer.SheetOutputs(document).Select(o => o.BaseName)];
        }

        return new PastDocument(job, document, FolderFound: true, FromRecordOnly: true);
    }

    private static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Time(JsonElement e, string property) =>
        Text(e, property) is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static StepStatus Step(JsonElement steps, string property) =>
        Enum.TryParse<StepStatus>(Text(steps, property), ignoreCase: true, out var status) ? status : StepStatus.Pending;

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

    /// <summary>
    /// The folder of the document in the output folder it is in now (it must be there): a copy that was moved with its <c>.markporto</c> has
    /// its folders where it is, not where it was (a folder of the old place that is still there is not taken). A record that knows its folder
    /// in the output folder (<c>OutputFolderRel</c>) has only that one: another folder of the same name is never taken for it. An older
    /// record without it is found as <see cref="ResolveFolder"/> does.
    /// </summary>
    public static string? ResolveFolderInRoot(string outputRoot, DocumentRecord document, bool allowMissing = false) =>
        FolderInRoot(outputRoot, document) is { } folder ? allowMissing || Directory.Exists(folder) ? folder : null : ResolveFolder(outputRoot, document);

    /// <summary>
    /// Where a resume writes the document (FR-157): its folder in the output folder it is in now, also when that folder is not there any more
    /// (it is made again there, not in the old place). An older record without <c>OutputFolderRel</c> keeps the folder that it has.
    /// </summary>
    public static string WriteFolderInRoot(string outputRoot, DocumentRecord document) =>
        FolderInRoot(outputRoot, document) ?? document.OutputFolder;

    // The folder of the record in the output folder: the stored one when it is in it, else the one its relative path names; null when the
    // record has no relative path (older records) and its stored folder is elsewhere.
    private static string? FolderInRoot(string outputRoot, DocumentRecord document)
    {
        var root = Path.GetFullPath(outputRoot);
        if (document.OutputFolder.Length > 0 && IsUnder(root, Path.GetFullPath(document.OutputFolder)))
        {
            return document.OutputFolder;
        }

        if (!string.IsNullOrEmpty(document.OutputFolderRel) && !Path.IsPathRooted(document.OutputFolderRel))
        {
            var candidate = Path.GetFullPath(Path.Combine(root, document.OutputFolderRel));
            return IsUnder(root, candidate) ? candidate : null;
        }

        return null;
    }

    /// <summary>
    /// The output root of a job (the folder of a folder input may be below the output folder) in the output folder it is in now: kept when it
    /// is in it; moved with the same place below it when the copy was moved (known by a document that has its relative path).
    /// </summary>
    public static string OutputRootInRoot(string outputRoot, JobState job, IEnumerable<DocumentRecord> documents)
    {
        var root = Path.GetFullPath(outputRoot);
        if (job.OutputRoot.Length == 0 || IsUnder(root, Path.GetFullPath(job.OutputRoot)) || string.Equals(Path.GetFullPath(job.OutputRoot).TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return job.OutputRoot;
        }

        foreach (var document in documents)
        {
            if (string.IsNullOrEmpty(document.OutputFolderRel) || Path.IsPathRooted(document.OutputFolderRel) || document.OutputFolder.Length == 0)
            {
                continue;
            }

            // The output folder of then: the stored folder without its relative path.
            var stored = Path.GetFullPath(document.OutputFolder);
            var rel = document.OutputFolderRel.Replace('/', '\\').TrimEnd('\\');
            if (!stored.EndsWith("\\" + rel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var before = stored[..^(rel.Length + 1)];
            var below = Path.GetRelativePath(before, Path.GetFullPath(job.OutputRoot));
            if (below == ".")
            {
                return root;
            }

            if (!below.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(below))
            {
                return Path.Combine(root, below);
            }
        }

        return job.OutputRoot;
    }

    private static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
