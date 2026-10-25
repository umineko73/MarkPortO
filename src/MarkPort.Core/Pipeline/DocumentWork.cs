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

using Microsoft.Extensions.Logging;
using MarkPort.Core.Engines;
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;
using MarkPort.Core.Vlm;

namespace MarkPort.Core.Pipeline;

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
