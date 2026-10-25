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

using System.Globalization;
using MarkPort.Core.Convert;
using MarkPort.Core.Jobs.Models;

namespace MarkPort.Cli;

/// <summary>
/// Prints one line whenever a step of a document changes, in the form of contracts/cli.md:
/// <c>[2026-10-25T10:15:42Z] d0003 報告書.docx  step1  done  pages=12</c>.
/// Secrets are never printed: nothing here has access to an API key.
/// </summary>
public sealed class ConsoleProgress(TextWriter output, bool quiet)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (StepStatus One, StepStatus Two, StepStatus Three)> _last = [];

    public void Report(DocumentRecord document)
    {
        lock (_gate)
        {
            var now = (document.Step1, document.Step2, document.Step3);
            _last.TryGetValue(document.DocId, out var before);
            var known = _last.ContainsKey(document.DocId);
            _last[document.DocId] = now;

            Line(document, 1, now.Step1, known ? before.One : (StepStatus?)null);
            Line(document, 2, now.Step2, known ? before.Two : (StepStatus?)null);
            Line(document, 3, now.Step3, known ? before.Three : (StepStatus?)null);
        }
    }

    /// <summary>A file that a resume does not run again (finished, or failed and not retried).</summary>
    public void Skipped(DocumentRecord document)
    {
        lock (_gate)
        {
            var state = document.IsFailed() ? "failed" : "done";
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] {document.DocId} {document.Display()}  skipped ({state})"));
        }
    }

    private void Line(DocumentRecord document, int step, StepStatus now, StepStatus? before)
    {
        if (before == now || now == StepStatus.NotApplicable || (before is null && now == StepStatus.Pending))
        {
            return;
        }

        if (quiet && now == StepStatus.Running)
        {
            return; // --quiet: only the end of a step is shown
        }

        var detail = now switch
        {
            StepStatus.Done when step == 1 => $"  pages={document.Pages.Count}",
            StepStatus.Failed => $"  {document.Error?.Message}",
            _ => string.Empty,
        };

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] {document.DocId} {document.Display()}  step{step}  {now.ToString().ToLowerInvariant()}{detail}"));
    }
}
