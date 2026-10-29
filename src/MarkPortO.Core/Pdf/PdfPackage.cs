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

using MarkPortO.Core.Localization;
using MarkPortO.Core.Ooxml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace MarkPortO.Core.Pdf;

/// <summary>Checks and opens a PDF file that is an input (FR-076).</summary>
public static class PdfPackage
{
    /// <summary>Checks that the file is a PDF that can be read. A password-protected or damaged file is an invalid document.</summary>
    /// <exception cref="InvalidDocumentException">The file is not a usable PDF.</exception>
    public static void EnsureOpens(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            _ = document.NumberOfPages;
        }
        catch (Exception ex) when (ex is PdfDocumentEncryptedException)
        {
            throw new InvalidDocumentException(Loc.T("The file cannot be opened as a .{0} document. It looks like a password-protected document.", "pdf"), ex);
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            throw new InvalidDocumentException(Loc.T("The file cannot be opened as a .{0} document.", "pdf"), ex);
        }
    }

    /// <summary>Opens the PDF to read its text; <c>null</c> when the text cannot be read (the pages then have no text, only the images).</summary>
    public static PdfDocument? OpenText(string path)
    {
        try
        {
            return PdfDocument.Open(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The paragraphs of a page (0-based); none when the page has no text layer or it cannot be read.</summary>
    public static IReadOnlyList<string> TryGetParagraphs(PdfDocument document, int pageIndex)
    {
        try
        {
            return PdfParagraphExtractor.GetParagraphs(document, pageIndex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return [];
        }
    }
}
