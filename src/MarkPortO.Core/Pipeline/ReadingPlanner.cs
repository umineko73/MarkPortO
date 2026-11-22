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
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Ooxml;

namespace MarkPortO.Core.Pipeline;

/// <summary>
/// How Step3 reads a page when it adds to the text of Step1 (FR-150): the page whole, its pictures one by one, or not at all.
/// </summary>
internal static class ReadingPlanner
{
    /// <summary>A page whose text of Step1 has fewer characters than this (blanks not counted), and no picture, is read whole: Step1 has (almost) nothing of it.</summary>
    public const int MinStep1Characters = 20;

    /// <summary>A picture to read, and the block of the document that it is in.</summary>
    /// <param name="Drawing">The picture is the page image, read for the drawn shapes of the block that Step1 has only the place of (a Word drawing).</param>
    public readonly record struct Picture(string File, int Block, bool Drawing = false);

    /// <summary>How the page is read, and the pictures to read when they are read one by one.</summary>
    public readonly record struct Plan(PageReading Reading, IReadOnlyList<Picture> Pictures);

    public static Plan For(DocumentRecord document, PageRecord page, IReadOnlyList<int> effectivePages)
    {
        // A PDF has no structure in Step1 (FR-076), and the Markdown of an extractor has no pages (FR-112): the image is the source.
        if (document.Step3Mode == Step3Mode.Transcribe || document.Kind == DocumentKind.Pdf || document.Extractor is not null)
        {
            return new Plan(PageReading.Whole, []);
        }

        var pictures = new List<Picture>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var characters = 0;
        var drawing = -1;
        var blocks = 0;
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            var block = document.Blocks[i];
            if (effectivePages[i] != page.Index || block.Markdown.Length == 0)
            {
                continue;
            }

            // A picture that is only its text: what it shows is only in the page image.
            // The marks are resolved first: a code span of Step1 is written with marks, not backquotes.
            if (PictureFiles.HasUnsavedPicture(MarkdownText.ResolveMarks(block.Markdown)))
            {
                return new Plan(PageReading.Whole, []);
            }

            foreach (var (_, file) in PictureFiles.References(block.Markdown))
            {
                if (seen.Add(file) && File.Exists(Path.Combine(document.OutputFolder, file.Replace('/', Path.DirectorySeparatorChar))))
                {
                    pictures.Add(new Picture(file, i));
                }
            }

            if (block.JoinedToPage is null)
            {
                blocks++; // a part joined to a block of an earlier page is written there
            }

            characters += MarkdownText.ResolveMarks(PictureFiles.WithoutFiles(block.Markdown)).Count(c => !char.IsWhiteSpace(c));
            if (block.HasDrawing && drawing < 0)
            {
                drawing = i;
            }
        }

        // The drawn shapes of a Word page are only in the page image: it is read for them, once, and what is read goes after the first block that has them.
        if (drawing >= 0 && page.ImageFile.Length > 0)
        {
            pictures.Add(new Picture(page.ImageFile, drawing, Drawing: true));
        }

        if (pictures.Count > 0)
        {
            return new Plan(PageReading.Pictures, pictures);
        }

        if (characters >= MinStep1Characters)
        {
            return new Plan(PageReading.None, []);
        }

        // No block of Step1 starts on the page: what it has is the rest of a block of the page before (a paragraph, rows, code joined to it), or,
        // when the page prints only a few lines, the end of a table row pushed over the page break (Step1 writes it with the row), and the header or
        // the footer. Read whole, the VLM makes a table or a text out of the piece.
        if (blocks == 0 && (characters > 0 || page.TextLines is { Count: <= MaxLinesOfAPageOfPieces }))
        {
            return new Plan(PageReading.None, []);
        }

        return new Plan(PageReading.Whole, []);
    }

    /// <summary>The most lines (in the PDF) of a page with no block of Step1 that is taken as the rest of the page before (FR-150).</summary>
    public const int MaxLinesOfAPageOfPieces = 3;
}
