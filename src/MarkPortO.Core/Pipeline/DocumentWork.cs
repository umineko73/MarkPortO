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

using Microsoft.Extensions.Logging;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Pipeline;

/// <summary>What the workers of a run share.</summary>
public sealed class JobContext(
    JobState job,
    StateStore store,
    IDocumentEngine engine,
    ILogger logger,
    TimeSpan engineTimeout)
{
    public JobState Job { get; } = job;

    public StateStore Store { get; } = store;

    public IDocumentEngine Engine { get; } = engine;

    public ILogger Logger { get; } = logger;

    public TimeSpan EngineTimeout { get; } = engineTimeout;

    public OptionsSnapshot Options => Job.Options;

    /// <summary>Reads page images with the VLM (step 3). <c>null</c> when step 3 is not used.</summary>
    public IVlmReader? Vlm { get; init; }

    /// <summary>The prompts of the three document types.</summary>
    public PromptSettings? Prompts { get; init; }

    /// <summary>The most lines read in one request; 0 never splits a page (FR-041).</summary>
    public int MaxLinesPerRead { get; init; }

    /// <summary>The longer side, in pixels, that a page image is scaled down to before it is sent; 0 sends it as it is (FR-064).</summary>
    public int MaxImageSide { get; init; }

    /// <summary>Decides whether another page may be started, over the whole job (FR-049, FR-057). <c>null</c>: no limit.</summary>
    public Step3Governor? Governor { get; init; }

    /// <summary>How many pages are read at the same time (FR-044).</summary>
    public int Step3Parallel { get; init; } = 1;

    /// <summary>The external commands that write Markdown (FR-110); with their environment values, which are not in the state of the job.</summary>
    public IReadOnlyList<ExtractorProfile> Extractors { get; init; } = [];

    /// <summary>The extractor chosen for a kind of file in this run; <c>null</c> for the built-in one (FR-111).</summary>
    public ExtractorProfile? ExtractorFor(DocumentKind kind)
    {
        var name = Options.Extractors.For(kind);
        return name.Length == 0 ? null : Extractors.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The scratch folder of a document (temporary copies and the PDF).</summary>
    public string WorkDirectory(DocumentRecord document) => Store.WorkDirectory(Job.JobId, document.DocId);

    /// <summary>Called after a document's state has been saved (a step started, finished, or failed).</summary>
    public Action<DocumentRecord>? Changed { get; set; }

    public void Save(DocumentRecord document)
    {
        Store.SaveDocument(Job.JobId, document);
        Changed?.Invoke(document);
    }
}

/// <summary>A document travelling through the steps.</summary>
public sealed class DocumentWork(DocumentRecord document, JobContext context)
{
    public DocumentRecord Document { get; } = document;

    public JobContext Context { get; } = context;
}
