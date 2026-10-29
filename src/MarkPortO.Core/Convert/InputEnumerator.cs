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
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Ooxml;

namespace MarkPortO.Core.Convert;

/// <summary>The documents found for the given paths.</summary>
/// <param name="Inputs">The documents, in a fixed order.</param>
/// <param name="InputFolder">Set when exactly one folder was given: the output is then grouped in a folder of the same name.</param>
/// <param name="Warnings">Things that were skipped (a path that does not exist, a file that is not a document ...).</param>
public sealed record EnumerationResult(IReadOnlyList<PlannedInput> Inputs, string? InputFolder, IReadOnlyList<string> Warnings);

/// <summary>Lists the documents to convert from the files and folders the user gave (FR-001 to FR-003).</summary>
public static class InputEnumerator
{
    /// <summary>Office writes a file with this prefix next to a document that is open; it is not a document.</summary>
    public const string OfficeLockPrefix = "~$";

    /// <param name="paths">Files and/or folders.</param>
    /// <param name="types">Which kinds a folder is searched for. A file that is named explicitly is taken whatever the types.</param>
    /// <param name="recurse">Also search the sub-folders of a folder.</param>
    public static EnumerationResult Enumerate(IEnumerable<string> paths, IReadOnlyCollection<DocumentKind> types, bool recurse)
    {
        var given = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var warnings = new List<string>();
        var inputs = new List<PlannedInput>();

        var folders = given.Where(Directory.Exists).ToList();
        var singleFolder = given.Count == 1 && folders.Count == 1;

        foreach (var path in given)
        {
            if (Directory.Exists(path))
            {
                // With one folder the output is grouped under the folder's name already, so no extra level here.
                var prefix = singleFolder ? string.Empty : ConvertRequest.FolderName(path);
                foreach (var file in FilesIn(path, types, recurse))
                {
                    var relative = Path.GetRelativePath(path, Path.GetDirectoryName(file)!);
                    relative = relative == "." ? string.Empty : relative;
                    inputs.Add(new PlannedInput(file, Combine(prefix, relative)));
                }
            }
            else if (File.Exists(path))
            {
                if (Path.GetFileName(path).StartsWith(OfficeLockPrefix, StringComparison.Ordinal))
                {
                    warnings.Add($"'{path}' is an Office lock file, not a document. Skipped.");
                }
                else if (OoxmlPackage.KindOf(path) is null)
                {
                    warnings.Add($"'{path}' is not a .docx, .xlsx, .pptx or .pdf file. Skipped.");
                }
                else
                {
                    inputs.Add(new PlannedInput(path));
                }
            }
            else
            {
                warnings.Add($"'{path}' was not found. Skipped.");
            }
        }

        var ordered = inputs
            .OrderBy(i => i.RelativeDirectory, StringComparer.Ordinal)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();
        return new EnumerationResult(ordered, singleFolder ? folders[0] : null, warnings);
    }

    private static IEnumerable<string> FilesIn(string folder, IReadOnlyCollection<DocumentKind> types, bool recurse)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current, "*", options))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(OfficeLockPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (OoxmlPackage.KindOf(file) is { } kind && types.Contains(kind))
                {
                    yield return file;
                }
            }

            if (recurse)
            {
                foreach (var sub in Directory.EnumerateDirectories(current, "*", options))
                {
                    // MarkPortO's own state folder is never searched for documents.
                    if (!string.Equals(Path.GetFileName(sub), StateStore.StateFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Push(sub);
                    }
                }
            }
        }
    }

    private static string Combine(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : Path.Combine(a, b);
}
