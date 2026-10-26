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

using System.Text.Json.Serialization;

namespace MarkPortO.Core.Jobs.Models;

[JsonConverter(typeof(LowercaseEnumConverter<DocumentKind>))]
public enum DocumentKind
{
    Docx,
    Xlsx,
    Pptx,
}

[JsonConverter(typeof(LowercaseEnumConverter<EngineKind>))]
public enum EngineKind
{
    Office,
    LibreOffice,
}

/// <summary>State of one step (1 to 3) of one document.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepStatus>))]
public enum StepStatus
{
    /// <summary>The step is not selected for this run (for example step 3 is switched off).</summary>
    NotApplicable,
    Pending,
    Running,
    Done,
    Failed,
    Interrupted,
}

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    Running,
    Interrupted,
    Completed,
    CompletedWithErrors,

    /// <summary>Left interrupted, and given up when a new run was started (FR-013g). It cannot be resumed.</summary>
    Abandoned,
}

/// <summary>State of the VLM reading (step 3) of one page.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PageStep3Status>))]
public enum PageStep3Status
{
    Pending,
    Done,
    Failed,
    Skipped,
}

[JsonConverter(typeof(JsonStringEnumConverter<BlockKind>))]
public enum BlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Table,
    SheetTable,
    SlideText,
}
