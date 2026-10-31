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

using System.IO;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Pipeline;

namespace MarkPortO.App.ViewModels;

/// <summary>One row of the file list (FR-013): the file and the state of each of its steps.</summary>
public sealed class FileItemViewModel : ObservableObject
{
    private FileRow _row;

    private readonly Func<string> _outputRoot;

    /// <param name="outputRoot">The output folder that is set now; the column of the output shows the folder of the document under it.</param>
    public FileItemViewModel(FileRow row, Func<string>? outputRoot = null)
    {
        _row = row;
        _outputRoot = outputRoot ?? (() => string.Empty);
    }

    public string DocId => _row.DocId;

    public string FileName => _row.FileName;

    public string FullPath => _row.FullPath;

    public string KindText => _row.Kind.ToString().ToLowerInvariant();

    public string StateText => FileStatusProjector.Text(_row.State);

    public bool IsFailed => _row.State == FileDisplayState.Failed;

    public bool HasPageFailures => _row.State == FileDisplayState.DoneWithPageFailures;

    public string Step1Text => FileStatusProjector.StepText(_row.StepStates[0]);

    public string Step2Text => FileStatusProjector.StepText(_row.StepStates[1]);

    public string Step3Text => FileStatusProjector.Step3Text(_row);

    public int PageCount => _row.PageCount;

    public int DeletedPageCount => _row.DeletedPageCount;

    /// <summary>Total VLM usage of the file; empty when the VLM is not used.</summary>
    public string UsageText => _row.UsageText;

    /// <summary>The folder of the document in the output (empty until the run has decided it).</summary>
    public string OutputFolderPath => _row.Source?.OutputFolder ?? string.Empty;

    /// <summary>The folder, as the path under the output folder (the whole path when it is not under it).</summary>
    public string OutputText
    {
        get
        {
            var folder = OutputFolderPath;
            var root = _outputRoot();
            if (folder.Length == 0 || root.Length == 0)
            {
                return folder;
            }

            var relative = Path.GetRelativePath(root, folder);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? folder : relative;
        }
    }

    public string MarkdownPath => NamedFile(MarkdownRenderer.MarkdownFileName);

    public string VlmPath => NamedFile(MarkdownRenderer.VlmFileName);

    public bool HasOutputFolder => OutputFolderPath.Length > 0 && Directory.Exists(OutputFolderPath);

    public bool HasMarkdown => MarkdownPath.Length > 0 && File.Exists(MarkdownPath);

    public bool HasVlm => VlmPath.Length > 0 && File.Exists(VlmPath);

    private string NamedFile(Func<string, string> fileName) =>
        _row.Source is { } document && document.OutputFolder.Length > 0
            ? Path.Combine(document.OutputFolder, fileName(Path.GetFileNameWithoutExtension(document.SourcePath)))
            : string.Empty;

    /// <summary>The reason of a failure.</summary>
    public string Message => _row.Message;

    /// <summary>The state of the document that the row was made from, for the preview (FR-068). Null for a row of the file list that has no record yet.</summary>
    public MarkPortO.Core.Jobs.Models.DocumentRecord? Source => _row.Source;

    /// <summary>The preview can be opened when the document has pages (Step 1 has made them).</summary>
    public bool CanPreview => _row.Source is { Pages.Count: > 0 };

    /// <summary>The pages, for the detail view.</summary>
    public IReadOnlyList<PageRow> Pages => _row.Pages;

    /// <summary>Makes the texts of the row again from its document, after the language was changed.</summary>
    public void Reproject()
    {
        if (_row.Source is { } document)
        {
            Update(FileStatusProjector.Project(document));
        }
    }

    public void Update(FileRow row)
    {
        _row = row;
        Raise(string.Empty); // every property may have changed
    }
}
