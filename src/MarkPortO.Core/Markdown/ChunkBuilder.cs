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
using System.Text.RegularExpressions;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;
using MarkPortO.Core.Ooxml;

namespace MarkPortO.Core.Markdown;

/// <summary>The kind of a chunk for retrieval (FR-152), as it is written in the file.</summary>
public static class ChunkKind
{
    public const string Text = "text";
    public const string Table = "table";
    public const string Code = "code";
    public const string Slide = "slide";
    public const string Page = "page";
}

/// <summary>A piece of a document for retrieval (FR-152): its text, the headings it is under, and where it is in the document.</summary>
public sealed class Chunk
{
    public string Id { get; set; } = string.Empty;

    public IReadOnlyList<string> Headings { get; init; } = [];

    public string Kind { get; init; } = ChunkKind.Text;

    /// <summary>The first and the last page (the page numbers of the Markdown); null when the pages are not known.</summary>
    public int? FirstPage { get; set; }

    public int? LastPage { get; set; }

    public string? Sheet { get; init; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Cuts a document into chunks for retrieval (FR-152, contracts/chunks-format.md), from the records of Step1 (the blocks and the pages) and the
/// readings of Step3, not from the Markdown: the pages of each block and the joins of tables and code over pages are known there.
/// </summary>
public static partial class ChunkBuilder
{
    /// <summary>Paragraphs are put together up to this many characters; a table longer than this is cut between its rows.</summary>
    public const int MaxChars = 1500;

    /// <param name="pages">Only these pages (the pages of one sheet, FR-153); null for every page.</param>
    public static IReadOnlyList<Chunk> Build(DocumentRecord document, IReadOnlySet<int>? pages = null)
    {
        var chunks = document.Extractor is null ? FromBlocks(document, pages) : FromExtractor(document);
        var digits = Math.Max(4, chunks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i].Id = "c" + (i + 1).ToString("D" + digits, System.Globalization.CultureInfo.InvariantCulture);
        }

        return chunks;
    }

    private static List<Chunk> FromBlocks(DocumentRecord document, IReadOnlySet<int>? only)
    {
        var language = document.MarkdownLanguage;
        var effectivePages = DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count);
        var texts = MarkdownRenderer.JoinTableParts(document.Blocks, effectivePages, language);
        var lastPage = LastPages(document.Blocks, effectivePages, texts);
        var figures = Figures(document);
        var cutter = new Cutter();
        string? sheet = null;

        foreach (var page in document.Pages.OrderBy(p => p.Index))
        {
            if (page.IsDeleted || (only is not null && !only.Contains(page.Index)))
            {
                continue;
            }

            if (page.Sheet is { } info && !string.Equals(info.Name, sheet, StringComparison.Ordinal))
            {
                // A sheet is a section of its own: its name is the heading of its chunks.
                sheet = info.Name;
                cutter.Section(1, info.Name, sheet);
            }

            var hasText = false;
            for (var i = 0; i < document.Blocks.Count; i++)
            {
                var block = document.Blocks[i];
                if (effectivePages[i] != page.Index || texts[i].Length == 0 || block.IsToc || IsJoinNote(block, texts[i]))
                {
                    continue;
                }

                hasText = true;
                var pages = (page.Index, Math.Max(page.Index, lastPage[i]));
                var text = MarkdownText.ResolveMarks(texts[i]);
                switch (block.Kind)
                {
                    case BlockKind.Heading when Heading(text) is { } heading:
                        cutter.Section(heading.Level, heading.Title, sheet);
                        break;
                    case BlockKind.Table or BlockKind.SheetTable:
                        cutter.Table(text, pages, sheet);
                        break;
                    case BlockKind.SlideText:
                        cutter.Break();
                        foreach (var part in Segments(text))
                        {
                            // What the VLM read in a picture of the slide goes after the text that has the picture (FR-150).
                            var withReadings = part.Kind == ChunkKind.Text ? part with { Text = part.Text + Readings(part.Text, figures, language, null) } : part;
                            Add(cutter, withReadings, pages, sheet, ChunkKind.Slide);
                        }

                        cutter.Break();
                        break;
                    case BlockKind.Paragraph when CodeText.IsBlock(text):
                        cutter.Code(text, pages, sheet);
                        break;
                    default:
                        cutter.Text(text + Readings(text, figures, language, block.HasDrawing ? page.ImageFile : null), pages, sheet, ChunkKind.Text);
                        break;
                }
            }

            // A page with no text of Step1 that the VLM read whole: what it read is the text of the page (Decision 011: elsewhere the text of Step1 is used).
            if (!hasText && WholeReading(page) is { } reading)
            {
                cutter.Break();
                cutter.Text(reading, (page.Index, page.Index), sheet, ChunkKind.Page);
                cutter.Break();
            }
        }

        return cutter.Finish();
    }

    /// <summary>A document of an extractor (FR-112): its Markdown has no page marks, so it is cut by its headings only.</summary>
    private static List<Chunk> FromExtractor(DocumentRecord document)
    {
        var cutter = new Cutter();
        var path = Path.Combine(document.OutputFolder, MarkdownRenderer.MarkdownFileName(Path.GetFileNameWithoutExtension(document.SourcePath)));
        var markdown = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var first = true;
        foreach (var part in Segments(markdown))
        {
            // The quote at the top tells that an extractor wrote the Markdown.
            if (first && part.Kind == ChunkKind.Text && part.Text.StartsWith("> ", StringComparison.Ordinal))
            {
                first = false;
                continue;
            }

            first = false;
            Add(cutter, part, null, null, ChunkKind.Text);
        }

        // What the VLM read of a document that the extractor gave no text for.
        foreach (var page in document.Pages.OrderBy(p => p.Index).Where(p => !p.IsDeleted))
        {
            if (WholeReading(page) is { } reading)
            {
                cutter.Break();
                cutter.Text(reading, (page.Index, page.Index), null, ChunkKind.Page);
                cutter.Break();
            }
        }

        return cutter.Finish();
    }

    private static void Add(Cutter cutter, Segment part, (int, int)? pages, string? sheet, string textKind)
    {
        switch (part.Kind)
        {
            case "heading":
                cutter.Section(part.Level, part.Title, sheet);
                break;
            case ChunkKind.Table:
                cutter.Table(part.Text, pages, sheet);
                break;
            case ChunkKind.Code:
                cutter.Code(part.Text, pages, sheet);
                break;
            default:
                cutter.Text(part.Text, pages, sheet, textKind);
                break;
        }
    }

    /// <summary>The text that the VLM read on a page that it read whole, or null.</summary>
    private static string? WholeReading(PageRecord page) =>
        page.Step3 == PageStep3Status.Done && page.ReadAs is not (PageReading.Pictures or PageReading.None) && page.VlmText is { } text && text.Trim().Length > 0
            ? MarkdownText.NeutraliseMarkers(text.Trim())
            : null;

    /// <summary>What the VLM read in each picture (FR-150), by file.</summary>
    private static Dictionary<string, string> Figures(DocumentRecord document)
    {
        var figures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in document.Pages.Where(p => p.Step3 == PageStep3Status.Done && p.ReadAs == PageReading.Pictures && p.Figures is not null))
        {
            foreach (var figure in page.Figures!.Where(f => f.Text.Trim().Length > 0))
            {
                figures.TryAdd(figure.File, figure.Text.Trim());
            }
        }

        return figures;
    }

    /// <summary>The readings of the pictures of a block, as quotes, in the same words as the Markdown (without its comments). Each is used once.</summary>
    private static string Readings(string block, Dictionary<string, string> figures, string language, string? drawing)
    {
        if (figures.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var files = Pipeline.PictureFiles.References(block).Select(r => r.File);
        foreach (var file in drawing is { Length: > 0 } ? files.Append(drawing) : files)
        {
            if (!figures.Remove(file, out var text))
            {
                continue;
            }

            builder.Append("\n\n> **").Append(Loc.M("What the VLM read in the figure", language)).Append("**\n>");
            foreach (var line in MarkdownText.NeutraliseMarkers(text).Split('\n'))
            {
                builder.Append('\n').Append(line.TrimEnd('\r').Length == 0 ? ">" : "> " + line.TrimEnd('\r'));
            }
        }

        return builder.ToString();
    }

    /// <summary>The quote that took the place of a part of a table, code or paragraph that was joined to the part on an earlier page.</summary>
    private static bool IsJoinNote(Block block, string text) =>
        block.JoinedToPage is not null && !string.Equals(text, block.Markdown, StringComparison.Ordinal) && text.StartsWith("> ", StringComparison.Ordinal);

    /// <summary>
    /// For each block, the last page it runs to: the page of the last part that was joined to it. The parts are followed as
    /// <see cref="MarkdownRenderer.JoinTableParts"/> joins them: a table to the last table, a paragraph or code to the last paragraph.
    /// </summary>
    private static int[] LastPages(IReadOnlyList<Block> blocks, IReadOnlyList<int> effectivePages, IReadOnlyList<string> texts)
    {
        var last = effectivePages.ToArray();
        var tableHead = -1;
        var paragraphHead = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            var isTable = blocks[i].Kind is BlockKind.Table or BlockKind.SheetTable;
            var isParagraph = blocks[i].Kind == BlockKind.Paragraph;
            if (!IsJoinNote(blocks[i], texts[i]))
            {
                if (isTable && blocks[i].JoinedToPage is null)
                {
                    tableHead = i;
                }
                else if (isParagraph && blocks[i].JoinedToPage is null)
                {
                    paragraphHead = i;
                }

                continue;
            }

            var head = isTable ? tableHead : isParagraph ? paragraphHead : -1;
            if (head >= 0)
            {
                last[head] = Math.Max(last[head], effectivePages[i]);
            }
        }

        return last;
    }

    private static (int Level, string Title)? Heading(string markdown)
    {
        var match = HeadingLine().Match(markdown.Trim());
        return match.Success ? (match.Groups[1].Length, Unescape(match.Groups[2].Value.Trim())) : null;
    }

    /// <summary>The text of a heading without the backslashes that escape the signs of Markdown.</summary>
    internal static string Unescape(string text) => EscapedSign().Replace(text, "$1").Trim();

    private sealed record Segment(string Kind, string Text, int Level = 0, string Title = "");

    /// <summary>A piece of Markdown cut into headings, tables, code blocks and paragraphs (comments are left out).</summary>
    private static IEnumerable<Segment> Segments(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();
        IEnumerable<Segment> FlushParagraph()
        {
            if (paragraph.Count > 0 && string.Join('\n', paragraph).Trim() is { Length: > 0 } text)
            {
                paragraph.Clear();
                return [new Segment(ChunkKind.Text, text)];
            }

            paragraph.Clear();
            return [];
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                foreach (var s in FlushParagraph())
                {
                    yield return s;
                }

                var fence = trimmed[..3];
                var code = new List<string> { line };
                for (i++; i < lines.Length; i++)
                {
                    code.Add(lines[i]);
                    if (lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                yield return new Segment(ChunkKind.Code, string.Join('\n', code));
            }
            else if (trimmed.StartsWith('|'))
            {
                foreach (var s in FlushParagraph())
                {
                    yield return s;
                }

                var table = new List<string> { line };
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('|'))
                {
                    table.Add(lines[++i]);
                }

                yield return new Segment(ChunkKind.Table, string.Join('\n', table));
            }
            else if (HeadingLine().Match(line) is { Success: true } heading)
            {
                foreach (var s in FlushParagraph())
                {
                    yield return s;
                }

                yield return new Segment("heading", string.Empty, heading.Groups[1].Length, Unescape(heading.Groups[2].Value));
            }
            else if (CommentLine().IsMatch(line))
            {
                continue;
            }
            else if (line.Trim().Length == 0)
            {
                foreach (var s in FlushParagraph())
                {
                    yield return s;
                }
            }
            else
            {
                paragraph.Add(line);
            }
        }

        foreach (var s in FlushParagraph())
        {
            yield return s;
        }
    }

    /// <summary>Puts the pieces of a document into chunks: a new chunk at each heading, paragraphs together up to <see cref="MaxChars"/>, tables and code alone.</summary>
    private sealed class Cutter
    {
        private readonly List<Chunk> _chunks = [];
        private readonly List<(int Level, string Title)> _headings = [];
        private readonly StringBuilder _text = new();
        private string _kind = ChunkKind.Text;
        private string? _sheet;
        private int? _first;
        private int? _last;

        private IReadOnlyList<string> Path => [.. _headings.Select(h => h.Title)];

        public void Section(int level, string title, string? sheet)
        {
            Break();
            _headings.RemoveAll(h => h.Level >= level);
            if (sheet is not null && level == 1)
            {
                _headings.Clear();
            }

            _headings.Add((level, title));
        }

        public void Text(string text, (int First, int Last)? pages, string? sheet, string kind)
        {
            if (_text.Length > 0 && (_kind != kind || _sheet != sheet || _text.Length + 2 + text.Length > MaxChars))
            {
                Break();
            }

            if (_text.Length > 0)
            {
                _text.Append("\n\n");
            }

            _text.Append(text);
            _kind = kind;
            _sheet = sheet;
            Extend(pages);
        }

        public void Code(string text, (int First, int Last)? pages, string? sheet)
        {
            Break();
            Add(ChunkKind.Code, text, pages, sheet);
        }

        public void Table(string text, (int First, int Last)? pages, string? sheet)
        {
            Break();
            var lines = text.Split('\n');
            if (text.Length <= MaxChars || lines.Length <= 3)
            {
                Add(ChunkKind.Table, text, pages, sheet);
                return;
            }

            // A long table is cut between its rows; each part starts with the header row and the line under it.
            var header = lines[0] + "\n" + lines[1];
            var part = new StringBuilder(header);
            var rows = 0;
            foreach (var row in lines.Skip(2))
            {
                if (rows > 0 && part.Length + 1 + row.Length > MaxChars)
                {
                    Add(ChunkKind.Table, part.ToString(), pages, sheet);
                    part.Clear().Append(header);
                    rows = 0;
                }

                part.Append('\n').Append(row);
                rows++;
            }

            Add(ChunkKind.Table, part.ToString(), pages, sheet);
        }

        /// <summary>Ends the chunk of paragraphs that is being made.</summary>
        public void Break()
        {
            if (_text.Length > 0)
            {
                _chunks.Add(new Chunk { Headings = Path, Kind = _kind, FirstPage = _first, LastPage = _last, Sheet = _sheet, Text = _text.ToString() });
            }

            _text.Clear();
            _first = null;
            _last = null;
        }

        public List<Chunk> Finish()
        {
            Break();
            return _chunks;
        }

        private void Add(string kind, string text, (int First, int Last)? pages, string? sheet) =>
            _chunks.Add(new Chunk { Headings = Path, Kind = kind, FirstPage = pages?.First, LastPage = pages?.Last, Sheet = sheet, Text = text });

        private void Extend((int First, int Last)? pages)
        {
            if (pages is { } p)
            {
                _first = _first is { } f ? Math.Min(f, p.First) : p.First;
                _last = _last is { } l ? Math.Max(l, p.Last) : p.Last;
            }
        }
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*\S)\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*<!--.*-->\s*$")]
    private static partial Regex CommentLine();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|<>~^])")]
    private static partial Regex EscapedSign();
}
