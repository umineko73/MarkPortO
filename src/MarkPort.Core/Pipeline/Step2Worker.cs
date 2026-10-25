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

using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Markdown;

namespace MarkPort.Core.Pipeline;

/// <summary>Step 2: add the references to the page images to the Markdown.</summary>
public sealed class Step2Worker : IPipelineStep<DocumentWork>
{
    public string Name => "step2";

    public int Concurrency => 1;

    public Task ProcessAsync(DocumentWork work, CancellationToken cancellationToken)
    {
        var document = work.Document;

        // Not selected, finished earlier, or step 1 did not finish: nothing to do (step 1 records its own failure).
        if (document.Step2 is StepStatus.NotApplicable or StepStatus.Done || document.Step1 != StepStatus.Done)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        document.Step2 = StepStatus.Running;
        work.Context.Save(document);
        try
        {
            // The Markdown is built again from the model; "step 2 done" is what adds the image references.
            document.Step2 = StepStatus.Done;
            MarkdownRenderer.Write(document);
            work.Context.Save(document);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            document.Step2 = StepStatus.Failed;
            document.Error = new DocumentError(2, ex.Message);
            work.Context.Save(document);
            throw;
        }

        return Task.CompletedTask;
    }
}
