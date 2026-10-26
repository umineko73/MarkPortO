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

using MarkPort.Core.Localization;

namespace MarkPort.Core.Jobs.Models;

/// <summary>Size and last write time (UTC ticks) of an input file; used to detect a change before resuming.</summary>
public sealed record Fingerprint(long Size, long LastWriteUtcTicks);

/// <summary>Token usage of an VLM call. A value is <c>null</c> when the server did not report it.</summary>
public sealed record UsageInfo(int? PromptTokens, int? CompletionTokens, int? TotalTokens)
{
    public static readonly UsageInfo Unavailable = new(null, null, null);

    public bool IsAvailable => PromptTokens.HasValue || CompletionTokens.HasValue || TotalTokens.HasValue;
}

/// <summary>Where a line of text is on the page: fractions of the page height from the top (FR-041).</summary>
public sealed record LineSpan(double Top, double Bottom);

public sealed class Step3Totals
{
    public int Pages { get; set; }

    public double CostUsd { get; set; }
}

/// <summary>What Step 3 of a job used: the kind of connection, the command and its version, the model (FR-055).</summary>
public sealed record Step3ConnectionInfo(string Kind, string? Command, string? CommandVersion, string? Model);

public sealed record Warning(string Code, string Message, int? PageIndex = null);

public sealed record DocumentError(int Step, string Message);

/// <summary>Where a page image of an xlsx sheet comes from.</summary>
public sealed record SheetInfo(string Name, string ColumnRange, int Part, int PartCount);

/// <summary>Document body block that is already rendered as Markdown and assigned to the page it starts on.</summary>
public sealed class Block
{
    public BlockKind Kind { get; set; }

    public string Markdown { get; set; } = string.Empty;

    /// <summary>Start page (the <see cref="PageRecord.Index"/>); <c>null</c> when it could not be determined.</summary>
    public int? PageIndex { get; set; }
}

public sealed class PageRecord
{
    /// <summary>Serial number in the output, starting at 1. Each image of a split xlsx sheet is one page.</summary>
    public int Index { get; set; }

    /// <summary>Where the page is in the source, worded for a sentence: <c>3ページ目</c>, <c>3枚目のスライド</c>, <c>シート「売上」</c>.</summary>
    public string SourceLabel { get; set; } = string.Empty;

    /// <summary>Relative path from the document folder, for example <c>images/page-003.png</c>.</summary>
    public string ImageFile { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public SheetInfo? Sheet { get; set; }

    public PageStep3Status Step3 { get; set; } = PageStep3Status.Pending;

    public string? VlmText { get; set; }

    /// <summary>The lines of text of the page, from the top, taken from the PDF. <c>null</c> when they are not known (FR-041).</summary>
    public List<LineSpan>? TextLines { get; set; }

    /// <summary>How many bands the page was read in; 0 when it was read whole (FR-041).</summary>
    public int Bands { get; set; }

    public UsageInfo? Usage { get; set; }

    /// <summary>What reading the page cost in US dollars, when the command says so (FR-054).</summary>
    public double? CostUsd { get; set; }

    public string? Error { get; set; }
}

public sealed class DocumentRecord
{
    public string DocId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    /// <summary>What the file list shows (FR-013a): the path from the given folder, or the file name. Empty in old state: the file name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The language of the texts in this document's Markdown, taken from the job when it started and kept when it is resumed.</summary>
    public string MarkdownLanguage { get; set; } = "en";

    public DocumentKind Kind { get; set; }

    public Fingerprint Fingerprint { get; set; } = new(0, 0);

    /// <summary>The document folder (absolute path) under the output root.</summary>
    public string OutputFolder { get; set; } = string.Empty;

    public StepStatus Step1 { get; set; } = StepStatus.Pending;

    public StepStatus Step2 { get; set; } = StepStatus.Pending;

    public StepStatus Step3 { get; set; } = StepStatus.Pending;

    public List<PageRecord> Pages { get; set; } = [];

    public List<Block> Blocks { get; set; } = [];

    public List<Warning> Warnings { get; set; } = [];

    public UsageInfo? UsageTotal { get; set; }

    /// <summary>The cost of the pages that reported it, in US dollars; <c>null</c> when none did.</summary>
    public double? CostTotalUsd { get; set; }

    public DocumentError? Error { get; set; }
}

/// <summary>The options of a run that influence the result. The API key is deliberately not part of it.</summary>
public sealed class OptionsSnapshot
{
    public List<DocumentKind> Types { get; set; } = [];

    public bool Recurse { get; set; }

    public int Dpi { get; set; }

    public bool IncludeHidden { get; set; }

    public EngineKind Engine { get; set; }

    public bool Step2 { get; set; }

    public bool Step3 { get; set; }

    public string? Connection { get; set; }

    /// <summary>The language of the texts in the output Markdown (<c>ja</c> / <c>en</c>), fixed when the job starts (FR-036).</summary>
    public string MarkdownLanguage { get; set; } = "en";
}

public sealed class JobState
{
    public Guid JobId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Running;

    /// <summary>The inputs given at start (normalized absolute paths); used to pick the job to resume.</summary>
    public List<string> Inputs { get; set; } = [];

    /// <summary>The input folder for a folder run; empty for individual files.</summary>
    public string InputRoot { get; set; } = string.Empty;

    /// <summary>The decided output root (including any " (2)" suffix). Not changed on resume.</summary>
    public string OutputRoot { get; set; } = string.Empty;

    public OptionsSnapshot Options { get; set; } = new();

    /// <summary>Document ids in the order they were listed at start.</summary>
    public List<string> Documents { get; set; } = [];

    /// <summary>The pages read and the cost spent by Step 3 in this job, over resumes (FR-057).</summary>
    public Step3Totals Step3Totals { get; set; } = new();

    /// <summary>What Step 3 used (FR-055). Not the key or the path of the command.</summary>
    public Step3ConnectionInfo? Step3Connection { get; set; }

    /// <summary>Why the job was stopped by Step 3 (a limit, a login, repeated failures); <c>null</c> otherwise.</summary>
    public string? StopReason { get; set; }
}
