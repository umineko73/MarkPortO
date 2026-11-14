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

namespace MarkPortO.Core.Jobs;

/// <summary>
/// The pages that the user marked to be read again by the VLM (FR-138). The marks are kept in the saved state of the document,
/// so the preview and the file list see the same ones, and they stay when a past output is opened.
/// </summary>
public static class RereadMarks
{
    /// <summary>The pages that a mark can be put on: the pages that Step 3 reads.</summary>
    public static IEnumerable<PageRecord> Markable(DocumentRecord document) => document.Pages.Where(p => p.ReadsInStep3);

    /// <summary>How many of the markable pages have a mark, and how many pages are markable.</summary>
    public static (int Marked, int Markable) Count(DocumentRecord document)
    {
        var pages = Markable(document).ToList();
        return (pages.Count(p => p.RereadMark), pages.Count);
    }

    /// <summary>
    /// Puts a mark on the pages (their numbers in the output), or takes it off, and saves the document. Pages that cannot be marked are left alone.
    /// Returns false when the document is not in the saved state of <paramref name="store"/> (nothing is changed then).
    /// </summary>
    public static bool Set(StateStore store, DocumentRecord document, IEnumerable<int> pageIndexes, bool marked)
    {
        if (document.JobId == Guid.Empty || store.LoadJob(document.JobId) is null)
        {
            return false;
        }

        var wanted = pageIndexes.ToHashSet();
        foreach (var page in Markable(document).Where(p => wanted.Contains(p.Index)))
        {
            page.RereadMark = marked;
        }

        store.SaveDocument(document.JobId, document);
        return true;
    }

    /// <summary>Marks every page (or none) of the document: the check box of a file in the list.</summary>
    public static bool SetAll(StateStore store, DocumentRecord document, bool marked) =>
        Set(store, document, Markable(document).Select(p => p.Index), marked);

    /// <summary>
    /// The pages that were read with other settings than those of now (FR-139): the model, or the prompt, differs from what the page records (a page read before the
    /// pages recorded this has the record of its file). A setting that is not known (no connection, or nothing recorded) is not compared. Pages that failed are not
    /// out of date: they were not read.
    /// </summary>
    /// <param name="model">The model that a reading would use now (<see cref="Step3Settings.ModelText(Settings.ConnectionProfile?)"/>); null: not compared.</param>
    /// <param name="prompt">The prompt of the kind of this document now (<see cref="Step3Settings.PromptText"/>).</param>
    public static IEnumerable<PageRecord> Stale(DocumentRecord document, string? model, string prompt) =>
        Markable(document).Where(p =>
        {
            if (p.Step3 != PageStep3Status.Done)
            {
                return false;
            }

            var readModel = p.VlmModel ?? document.VlmModel;
            var readPrompt = p.VlmPrompt ?? document.VlmPrompt;
            return (model is not null && readModel is not null && !string.Equals(readModel, model, StringComparison.Ordinal))
                || (readPrompt is not null && !string.Equals(readPrompt, prompt, StringComparison.Ordinal));
        });

    /// <summary>Marks the pages that are out of date (see <see cref="Stale"/>), and saves the document.</summary>
    public static bool MarkStale(StateStore store, DocumentRecord document, string? model, string prompt) =>
        Set(store, document, Stale(document, model, prompt).Select(p => p.Index), marked: true);

    /// <summary>Marks the pages that could not be read.</summary>
    public static bool MarkFailed(StateStore store, DocumentRecord document) =>
        Set(store, document, Markable(document).Where(p => p.Step3 == PageStep3Status.Failed).Select(p => p.Index), marked: true);
}
