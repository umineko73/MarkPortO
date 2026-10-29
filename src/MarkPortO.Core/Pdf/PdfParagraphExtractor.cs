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

using System.Text;
using MarkPortO.Core.Markdown;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MarkPortO.Core.Pdf;

/// <summary>
/// The text layer of a PDF page as paragraphs (FR-076). Only the lines are put together: the words on the same baseline are a line, lines that
/// stand close are a paragraph. Tables, columns and headings are not rebuilt; the image of the page tells them.
/// </summary>
public static class PdfParagraphExtractor
{
    /// <summary>The paragraphs of a page (0-based) in reading order from the top. Empty when the page has no text layer.</summary>
    public static IReadOnlyList<string> GetParagraphs(PdfDocument document, int pageIndex)
    {
        var page = document.GetPage(pageIndex + 1);
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var lines = new List<Line>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var height = Math.Max(1.0, (double)word.BoundingBox.Height);
            var line = lines.Count > 0 ? lines[^1] : null;
            if (line is not null && Math.Abs(line.Baseline - (double)word.BoundingBox.Bottom) <= 0.5 * line.Height)
            {
                line.Words.Add(word);
                line.Height = Math.Max(line.Height, height);
            }
            else
            {
                lines.Add(new Line { Baseline = word.BoundingBox.Bottom, Height = height, Words = [word] });
            }
        }

        var paragraphs = new List<string>();
        var builder = new StringBuilder();
        Line? previous = null;
        foreach (var line in lines)
        {
            var text = LineText(line);
            if (previous is not null && previous.Baseline - line.Baseline > 2.0 * Math.Max(previous.Height, line.Height))
            {
                Flush();
            }

            if (builder.Length > 0)
            {
                builder.Append(ParagraphJoin.Separator(builder.ToString(), text));
            }

            builder.Append(text);
            previous = line;
        }

        Flush();
        return paragraphs;

        void Flush()
        {
            if (builder.Length > 0)
            {
                paragraphs.Add(MarkdownText.Escape(builder.ToString().Trim()));
                builder.Clear();
            }
        }
    }

    private static string LineText(Line line)
    {
        var builder = new StringBuilder();
        Word? before = null;
        foreach (var word in line.Words.OrderBy(w => w.BoundingBox.Left))
        {
            if (before is not null)
            {
                builder.Append(ParagraphJoin.Separator(before.Text, word.Text));
            }

            builder.Append(word.Text);
            before = word;
        }

        return builder.ToString();
    }

    private sealed class Line
    {
        public double Baseline { get; init; }

        public double Height { get; set; }

        public required List<Word> Words { get; init; }
    }
}
