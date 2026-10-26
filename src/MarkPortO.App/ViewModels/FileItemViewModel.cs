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
using MarkPortO.Core.Pipeline;

namespace MarkPortO.App.ViewModels;

/// <summary>One row of the file list (FR-013): the file and the state of each of its steps.</summary>
public sealed class FileItemViewModel : ObservableObject
{
    private FileRow _row;

    public FileItemViewModel(FileRow row) => _row = row;

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

    /// <summary>The reason of a failure.</summary>
    public string Message => _row.Message;

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
