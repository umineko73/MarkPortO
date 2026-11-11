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

/// <summary>
/// The last conversion of each file in an output folder, from the saved state of its runs (FR-126, FR-127): where it was written, and whether
/// it finished. A file is up to date when its last conversion finished, its output folder is still there, and the file has not changed since.
/// </summary>
public sealed class PreviousOutputs
{
    private readonly Dictionary<string, DocumentRecord> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);

    private PreviousOutputs()
    {
    }

    /// <summary>Reads the runs of the output folder, the newest first: the newest conversion of a file is the one that counts.</summary>
    public static PreviousOutputs Load(string outputFolder)
    {
        var result = new PreviousOutputs();
        var outRoot = Path.GetFullPath(outputFolder);
        var store = new StateStore(outRoot);
        foreach (var job in store.ListJobs())
        {
            foreach (var docId in job.Documents)
            {
                if (store.LoadDocument(job.JobId, docId) is not { } document || result._latest.ContainsKey(document.SourcePath))
                {
                    continue;
                }

                // The output folder may have been moved with its runs (FR-120): the path from the output root finds it.
                if (document.OutputFolderRel is { Length: > 0 } relative)
                {
                    document.OutputFolder = Path.GetFullPath(Path.Combine(outRoot, relative));
                }

                if (!Directory.Exists(document.OutputFolder))
                {
                    continue;
                }

                result._latest[document.SourcePath] = document;

                // The folder that a run of an input folder wrote into: the first part of the path of its documents.
                if (job.InputRoot.Length > 0 && !result._roots.ContainsKey(job.InputRoot)
                    && document.OutputFolderRel?.Split(['\\', '/'], 2)[0] is { Length: > 0 } rootName)
                {
                    var root = Path.Combine(outRoot, rootName);
                    if (Directory.Exists(root))
                    {
                        result._roots[StateStore.NormalizeInputs([job.InputRoot])[0]] = root;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>The last conversion of a file, or null when it has none whose output is still there.</summary>
    public DocumentRecord? Latest(string sourcePath) =>
        _latest.TryGetValue(Path.GetFullPath(sourcePath), out var document) ? document : null;

    /// <summary>The file was converted, the conversion finished, and the file has not changed since.</summary>
    public bool IsUpToDate(string sourcePath) =>
        Latest(sourcePath) is { } document && document.IsDone() && !StateStore.HasChanged(document.Fingerprint, document.SourcePath);

    /// <summary>The output folder of the last run of an input folder, or null.</summary>
    public string? RootOf(string? inputFolder) =>
        inputFolder is not null && _roots.TryGetValue(StateStore.NormalizeInputs([inputFolder])[0], out var root) ? root : null;

    /// <summary>Where the files that were converted before are written again: their earlier folders.</summary>
    public IReadOnlyDictionary<string, PreviousOutput> For(IEnumerable<string> sourcePaths)
    {
        var map = new Dictionary<string, PreviousOutput>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in sourcePaths)
        {
            if (Latest(path) is { } document)
            {
                map[Path.GetFullPath(path)] = new PreviousOutput(document.OutputFolder, document.DisplayName);
            }
        }

        return map;
    }

    /// <summary>
    /// The request that converts again only the files that changed since their last conversion, those never converted, and those whose last
    /// conversion did not finish (FR-126). Null when every file is up to date. Converted files go back to their earlier folders.
    /// </summary>
    public ConvertRequest? ChangedOnly(ConvertRequest request)
    {
        var inputs = request.Inputs.Where(i => !IsUpToDate(i.Path)).ToList();
        if (inputs.Count == 0)
        {
            return null;
        }

        return new ConvertRequest
        {
            Inputs = inputs,
            GivenInputs = request.GivenInputs,
            InputFolder = request.InputFolder,
            OutputFolder = request.OutputFolder,
            Options = request.Options,
            Previous = For(inputs.Select(i => i.Path)),
            OutputRootFolder = RootOf(request.InputFolder),
        };
    }

    /// <summary>
    /// The request that converts every file again and writes those converted before into their earlier folders (their Markdown and page
    /// images are replaced); a file that was never converted gets a new folder (FR-129).
    /// </summary>
    public ConvertRequest SameFolders(ConvertRequest request) => new()
    {
        Inputs = request.Inputs,
        GivenInputs = request.GivenInputs,
        InputFolder = request.InputFolder,
        OutputFolder = request.OutputFolder,
        Options = request.Options,
        Previous = For(request.Inputs.Select(i => i.Path)),
        OutputRootFolder = RootOf(request.InputFolder),
    };
}
