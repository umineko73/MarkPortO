// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;

namespace MarkPort.Core.Convert;

/// <summary>One document to convert.</summary>
/// <param name="Path">The document file.</param>
/// <param name="RelativeDirectory">
/// Where the document sits below the input folder (empty for a single file); the same sub-folders are made in the output.
/// </param>
public sealed record PlannedInput(string Path, string RelativeDirectory = "");

/// <summary>What to convert and how (the input of <see cref="ConvertService"/>).</summary>
public sealed class ConvertRequest
{
    /// <summary>The documents, already listed and filtered (see InputEnumerator).</summary>
    public required IReadOnlyList<PlannedInput> Inputs { get; init; }

    /// <summary>The paths the user gave (files or folders); remembered to pick the job to resume.</summary>
    public IReadOnlyList<string> GivenInputs { get; init; } = [];

    /// <summary>
    /// For a folder run: the input folder. The output goes to a folder of the same name below <see cref="OutputFolder"/> (FR-004).
    /// </summary>
    public string? InputFolder { get; init; }

    /// <summary>The name of a folder as used for the output folder: <c>D:\in\報告書</c> gives <c>報告書</c>, <c>D:\</c> gives <c>D</c>.</summary>
    public static string FolderName(string folder)
    {
        var full = Path.GetFullPath(folder);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed.TrimEnd(':');
    }

    /// <summary>The folder chosen by the user (<c>--out</c>).</summary>
    public required string OutputFolder { get; init; }

    public required OptionsSnapshot Options { get; init; }

    /// <summary>Takes the options of a run from the settings.</summary>
    public static OptionsSnapshot OptionsFrom(AppSettings settings) => new()
    {
        Types = [.. settings.Input.Types],
        Recurse = settings.Input.Recurse,
        Dpi = settings.Images.Dpi,
        IncludeHidden = settings.Input.IncludeHidden,
        Engine = settings.Engine.Kind,
        Step2 = settings.Steps.Step2,
        Step3 = settings.Steps.Step3,
        Connection = settings.Steps.Step3 ? settings.Vlm.Selected : null,
        MarkdownLanguage = LanguageResolver.Resolve(settings.Ui.Language),
    };
}

/// <summary>The outcome of a run.</summary>
public sealed record ConvertResult(JobState Job, IReadOnlyList<DocumentRecord> Documents)
{
    public bool HasFailures => Documents.Any(d => d.IsFailed() || d.HasFailedPages());

    public bool WasInterrupted => Job.Status == JobStatus.Interrupted;
}

public static class DocumentRecordExtensions
{
    /// <summary>The name for the file list and the command line's progress: the path from the given folder, or the file name (FR-013a).</summary>
    public static string Display(this DocumentRecord document) =>
        document.DisplayName.Length > 0 ? document.DisplayName : Path.GetFileName(document.SourcePath);

    /// <summary>A document has failed when any of its steps failed.</summary>
    public static bool IsFailed(this DocumentRecord document) =>
        document.Step1 == StepStatus.Failed || document.Step2 == StepStatus.Failed || document.Step3 == StepStatus.Failed;

    /// <summary>Some pages failed (the VLM could not read them, an image could not be made) although the file went on.</summary>
    public static bool HasFailedPages(this DocumentRecord document) =>
        document.Pages.Any(p => p.Step3 == PageStep3Status.Failed || p.Error is not null);

    /// <summary>A document is finished when no selected step is left to run and none failed.</summary>
    public static bool IsDone(this DocumentRecord document) =>
        !document.IsFailed()
        && document.Step1 == StepStatus.Done
        && document.Step2 is StepStatus.Done or StepStatus.NotApplicable
        && document.Step3 is StepStatus.Done or StepStatus.NotApplicable;
}

/// <summary>The run cannot start (a bad setting, an engine that is not available ...). Nothing has been written.</summary>
public class ConvertStartException(string message) : Exception(message);

/// <summary>There is no job to resume (none in the output folder, or the named one does not exist or cannot be resumed).</summary>
public sealed class JobNotFoundException(string message) : ConvertStartException(message);
