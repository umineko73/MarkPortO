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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkPortO.Core.Jobs.Models;

[JsonConverter(typeof(LowercaseEnumConverter<DocumentKind>))]
public enum DocumentKind
{
    Docx,
    Xlsx,
    Pptx,
    Pdf,
}

[JsonConverter(typeof(LowercaseEnumConverter<EngineKind>))]
public enum EngineKind
{
    Office,
    LibreOffice,
}

/// <summary>How the pages of an Excel sheet are cut (FR-073).</summary>
[JsonConverter(typeof(ExcelRangeConverter))]
public enum ExcelRange
{
    /// <summary>All columns of the used range on the width of a page; the rows go on to more pages. A very wide sheet is still split (FR-011).</summary>
    Columns,

    /// <summary>The whole used range of the sheet on one page (width and height); never split. The text gets small on a big sheet, so the DPI is raised.</summary>
    Sheet,

    /// <summary>The print area and the page setup of the sheet, as they are saved in the workbook.</summary>
    PrintArea,
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
    Running,
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

/// <summary>Reads <see cref="ExcelRange"/> in any case; the old value <c>rows</c> (removed) is read as <see cref="ExcelRange.Columns"/>.</summary>
public sealed class ExcelRangeConverter : JsonConverter<ExcelRange>
{
    public override ExcelRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (string.Equals(text, "rows", StringComparison.OrdinalIgnoreCase))
        {
            return ExcelRange.Columns;
        }

        if (text is not null && Enum.TryParse<ExcelRange>(text, ignoreCase: true, out var value) && Enum.IsDefined(value))
        {
            return value;
        }

        throw new JsonException($"'{text}' is not a valid {nameof(ExcelRange)}.");
    }

    public override void Write(Utf8JsonWriter writer, ExcelRange value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
