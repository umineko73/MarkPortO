// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
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

namespace MarkPort.Core.Pdf;

/// <summary>Reads the text of each PDF page; used to find which page a paragraph of a docx lands on (research R3).</summary>
public static class PdfTextExtractor
{
    /// <summary>The text of every page, in page order.</summary>
    public static IReadOnlyList<string> GetPageTexts(string pdfPath)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        var texts = new List<string>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            texts.Add(page.Text);
        }

        return texts;
    }
}
