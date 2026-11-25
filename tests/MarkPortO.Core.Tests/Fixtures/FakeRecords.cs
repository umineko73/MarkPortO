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
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Tests.Fixtures;

/// <summary>
/// Writes the record of a finished conversion into an output folder, without converting (the screen tests use it too): a job with the
/// options given, one document per source with its folder.
/// </summary>
internal static class FakeRecords
{
    public static JobState Write(string output, IReadOnlyList<string> sources, Action<OptionsSnapshot>? options = null, DateTime? at = null)
    {
        var store = new StateStore(output);
        var job = new JobState
        {
            JobId = Guid.NewGuid(),
            Status = JobStatus.Completed,
            CreatedAt = at ?? DateTime.UtcNow,
            UpdatedAt = at ?? DateTime.UtcNow,
            OutputRoot = output,
            Options = new OptionsSnapshot
            {
                Types = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf],
                Dpi = 150,
                Step2 = true,
                Step3Mode = Step3Mode.Supplement,
            },
        };
        options?.Invoke(job.Options);
        for (var i = 0; i < sources.Count; i++)
        {
            var source = Path.GetFullPath(sources[i]);
            var folder = Path.Combine(output, $"{Path.GetFileNameWithoutExtension(source)}-{job.JobId.ToString("N")[..8]}");
            Directory.CreateDirectory(folder);
            var document = new DocumentRecord
            {
                DocId = $"d{i + 1:0000}",
                JobId = job.JobId,
                SourcePath = source,
                DisplayName = Path.GetFileName(source),
                Kind = Path.GetExtension(source).ToLowerInvariant() switch { ".xlsx" => DocumentKind.Xlsx, ".pptx" => DocumentKind.Pptx, ".pdf" => DocumentKind.Pdf, _ => DocumentKind.Docx },
                Fingerprint = StateStore.ComputeFingerprint(source),
                OutputFolder = folder,
                Step1 = StepStatus.Done,
                Step2 = job.Options.Step2 ? StepStatus.Done : StepStatus.NotApplicable,
                Step3 = job.Options.Step3 ? StepStatus.Done : StepStatus.NotApplicable,
                PageImage = job.Options.PageImage,
                VlmSeparate = job.Options.VlmSeparate,
                Step1Tables = job.Options.Step1Tables,
                DropRepeatedTables = job.Options.DropRepeatedTables,
                Step3Mode = job.Options.Step3Mode,
                StructureNote = job.Options.StructureNote,
                Chunks = job.Options.Chunks,
                ExcelPerSheet = job.Options.ExcelPerSheet,
            };
            job.Documents.Add(document.DocId);
            store.SaveDocument(job.JobId, document);
        }

        store.SaveJob(job, touch: false);
        return job;
    }
}
