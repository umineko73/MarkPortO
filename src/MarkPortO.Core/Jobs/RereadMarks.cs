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

    /// <summary>Marks the pages that could not be read.</summary>
    public static bool MarkFailed(StateStore store, DocumentRecord document) =>
        Set(store, document, Markable(document).Where(p => p.Step3 == PageStep3Status.Failed).Select(p => p.Index), marked: true);
}
