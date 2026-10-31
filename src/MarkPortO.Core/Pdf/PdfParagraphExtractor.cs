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
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace MarkPortO.Core.Pdf;

/// <summary>
/// The text layer of a PDF page as paragraphs (FR-076). Only the lines are put together: the words on the same baseline are a line, lines that
/// stand close are a paragraph (a line that starts indented starts a new one). Tables and headings are not rebuilt; the columns are read one after the other (FR-108); the image of the page tells the rest.
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

        var paragraphs = new List<string>();
        var builder = new StringBuilder();

        // The page is cut into blocks (the columns, the title over them), and each block is read from the top, one after the other (FR-108).
        foreach (var block in Blocks(page, words))
        {
            var lines = new List<Line>();
            foreach (var word in block.OrderByDescending(w => w.BoundingBox.Bottom))
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

            // The line pitch of the block (the small gaps; the gap of a paragraph is larger) and its left edge, from the lines of the block (FR-108).
            var pitches = lines.Zip(lines.Skip(1), (above, below) => above.Baseline - below.Baseline).Where(d => d > 0).OrderBy(d => d).ToList();
            var pitch = pitches.Count >= 3 ? pitches[(pitches.Count - 1) / 4] : 0.0; // a few lines say nothing about the pitch
            // The left edge is the middle of the lines' starts (one line at the margin, a page number, must not move it).
            var starts = lines.Select(l => (double)l.Words.Min(w => w.BoundingBox.Left)).OrderBy(x => x).ToList();
            var left = starts.Count > 0 ? starts[starts.Count / 2] : 0.0;

            Line? previous = null;
            foreach (var line in lines)
            {
                var text = LineText(line);
                var gap = previous is null ? 0.0 : previous.Baseline - line.Baseline;
                var indented = previous is not null && line.Words.Min(w => (double)w.BoundingBox.Left) - left >= Math.Max(0.7 * Math.Max(1.0, line.Height), 0.5 * pitch);
                if (previous is not null && (gap > Math.Max(2.0 * Math.Max(previous.Height, line.Height), 1.5 * pitch) || indented))
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
        }

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

    /// <summary>
    /// The words of the page in groups, in reading order (FR-108). A page with columns has a strip of white space between them, where (almost) no letter
    /// stands. The letters are looked at, not the words: in Japanese text without spaces, the end of a line of the left column and the start of the
    /// line of the right column are one word. Each column is read from the top, left to right. A page without such a strip is one group.
    /// </summary>
    private static List<List<Word>> Blocks(Page page, List<Word> words)
    {
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        if (letters.Count < MinimumLetters)
        {
            return [words];
        }

        var cuts = Gutters(letters);
        if (cuts.Count == 0)
        {
            return [words];
        }

        var columns = new List<List<Letter>>();
        for (var i = 0; i <= cuts.Count; i++)
        {
            var left = i == 0 ? double.MinValue : cuts[i - 1];
            var right = i == cuts.Count ? double.MaxValue : cuts[i];
            columns.Add(letters.Where(l => Middle(l) >= left && Middle(l) < right).ToList());
        }

        // Every column must have lines of its own, or the strip was a gap in a table or a list, not a space between columns.
        var height = Median(letters.Select(l => (double)l.BoundingBox.Height));
        if (columns.Any(c => c.Count < MinimumShare * letters.Count || c.Select(l => Math.Round((double)l.StartBaseLine.Y / Math.Max(1.0, 0.8 * height))).Distinct().Count() < MinimumLines))
        {
            return [words];
        }

        return [.. columns.Select(c => NearestNeighbourWordExtractor.Instance.GetWords(c).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList())];
    }

    private const int MinimumLetters = 40;
    private const int MinimumLines = 6;

    /// <summary>The share of the letters that every column has at least (a few words beside a text are not a column).</summary>
    private const double MinimumShare = 0.15;

    private static double Middle(Letter letter) => (double)(letter.BoundingBox.Left + letter.BoundingBox.Right) / 2;

    /// <summary>The x of the middle of each strip between columns: a run of positions that no more than 1% of the letters cross, in the middle of the page.</summary>
    private static List<double> Gutters(List<Letter> letters)
    {
        var minX = letters.Min(l => (double)l.BoundingBox.Left);
        var maxX = letters.Max(l => (double)l.BoundingBox.Right);
        var width = maxX - minX;
        if (width <= 0)
        {
            return [];
        }

        var size = (int)Math.Ceiling(width) + 1;
        var crossing = new int[size + 1];
        foreach (var letter in letters)
        {
            var from = Math.Clamp((int)Math.Floor((double)letter.BoundingBox.Left - minX) + 1, 0, size);
            var to = Math.Clamp((int)Math.Ceiling((double)letter.BoundingBox.Right - minX) - 1, 0, size);
            if (to >= from)
            {
                crossing[from]++;
                crossing[to + 1]--;
            }
        }

        // A strip between columns is crossed by few lines: a title over the columns, the noise of a scan (a column of a scan is crossed by up to a quarter of the lines).
        // The measure is the lines, not the letters: 1% of the letters is most of the lines of a page of one column. A text of one column has no such strip that is wide.
        var lineHeight = Math.Max(1.0, 0.8 * Median(letters.Select(l => (double)l.BoundingBox.Height)));
        var lineCount = letters.Select(l => Math.Round((double)l.StartBaseLine.Y / lineHeight)).Distinct().Count();
        var allowed = Math.Max(1, (int)(0.3 * lineCount));
        var minimumGap = Math.Max(8.0, 2.5 * Median(letters.Select(l => (double)l.BoundingBox.Width)));
        var cuts = new List<double>();
        var running = 0;
        var runStart = -1;
        for (var x = 0; x <= size; x++)
        {
            running += crossing[x];
            var inside = x >= 0.15 * width && x <= 0.85 * width;
            if (inside && running <= allowed)
            {
                runStart = runStart < 0 ? x : runStart;
                continue;
            }

            if (runStart >= 0 && x - runStart >= minimumGap)
            {
                cuts.Add(minX + ((runStart + x) / 2.0));
            }

            runStart = -1;
        }

        return cuts;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 1.0 : sorted[sorted.Count / 2];
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
