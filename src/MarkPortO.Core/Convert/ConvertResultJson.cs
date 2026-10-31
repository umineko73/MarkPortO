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

namespace MarkPortO.Core.Convert;

/// <summary>The JSON written by <c>--result</c> (see contracts/cli.md). It never contains an API key.</summary>
public static class ConvertResultJson
{
    public static string Serialize(ConvertResult result) => JsonDefaults.Serialize(ToDto(result));

    public static string StateOf(DocumentRecord document)
    {
        if (document.IsFailed())
        {
            return "Failed";
        }

        if (document.IsDone())
        {
            return "Done";
        }

        return document.Step1 == StepStatus.Interrupted || document.Step2 == StepStatus.Interrupted || document.Step3 == StepStatus.Interrupted
            ? "Interrupted"
            : "Pending";
    }

    private static ResultDto ToDto(ConvertResult result) => new()
    {
        JobId = result.Job.JobId,
        Status = result.Job.Status.ToString(),
        OutputRoot = result.Job.OutputRoot,
        Documents = [.. result.Documents.Select(ToDto)],
    };

    private static DocumentDto ToDto(DocumentRecord document) => new()
    {
        Source = document.SourcePath,
        OutputFolder = document.OutputFolder,
        Markdown = Markdown.MarkdownRenderer.MarkdownFileName(Path.GetFileNameWithoutExtension(document.SourcePath)),
        VlmMarkdown = document.VlmInSeparateFile && document.Step3 != StepStatus.NotApplicable
            ? Markdown.MarkdownRenderer.VlmFileName(Path.GetFileNameWithoutExtension(document.SourcePath))
            : null,
        State = StateOf(document),
        Pages = document.Pages.Count,
        DeletedPages = [.. document.Pages.Where(p => p.IsDeleted).Select(p => p.Index)],
        Warnings = [.. document.Warnings],
        Usage = document.Step3 == StepStatus.NotApplicable ? null : document.UsageTotal ?? UsageInfo.Unavailable,
        Error = document.Error is { } e ? new ErrorDto { Step = e.Step, Message = e.Message } : null,
    };

    private sealed class ResultDto
    {
        public Guid JobId { get; set; }

        public string Status { get; set; } = string.Empty;

        public string OutputRoot { get; set; } = string.Empty;

        public List<DocumentDto> Documents { get; set; } = [];
    }

    private sealed class DocumentDto
    {
        public string Source { get; set; } = string.Empty;

        public string OutputFolder { get; set; } = string.Empty;

        /// <summary>The file name of the Markdown, in <see cref="OutputFolder"/>.</summary>
        public string Markdown { get; set; } = string.Empty;

        /// <summary>The file name of the VLM results when they are in a separate file (FR-058); otherwise null.</summary>
        public string? VlmMarkdown { get; set; }

        public string State { get; set; } = string.Empty;

        public int Pages { get; set; }

        public List<int> DeletedPages { get; set; } = [];

        public List<Warning> Warnings { get; set; } = [];

        public UsageInfo? Usage { get; set; }

        public ErrorDto? Error { get; set; }
    }

    private sealed class ErrorDto
    {
        public int Step { get; set; }

        public string Message { get; set; } = string.Empty;
    }
}
