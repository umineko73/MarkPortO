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

using System.Text.RegularExpressions;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Markdown;

/// <summary>One page of a converted document, for the preview (FR-068): its image and its part of the Markdown.</summary>
/// <param name="ImagePath">The full path of the PNG, or null when the page has no image (yet).</param>
/// <param name="Markdown">What the Markdown of the document has for the page: from its <c>&lt;!-- page: N --&gt;</c> to the next one.</param>
/// <param name="Vlm">The result of Step 3 for the page: from the separate file, or the part of the page between <c>vlm:begin</c> and <c>vlm:end</c>. Null when there is none.</param>
public sealed record PreviewPage(
    int Index,
    string Label,
    bool IsDeleted,
    PageStep3Status Step3,
    string? ImagePath,
    string Markdown,
    string? Vlm,
    bool IsOutOfRange = false);

/// <summary>Reads the pages of a converted document from the files of the output folder (FR-068).</summary>
public static partial class PagePreview
{
    [GeneratedRegex(@"^<!-- page: (\d+) -->[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex PageMarker();

    [GeneratedRegex(@"<!-- vlm:begin[^>]*-->(.*?)<!-- vlm:end -->", RegexOptions.Singleline)]
    private static partial Regex VlmBlock();

    /// <summary>
    /// The pages of the document with their images and Markdown. A file that is not there yet gives empty text: a conversion in
    /// progress writes the Markdown after Step 1, and the results of Step 3 page by page.
    /// </summary>
    public static IReadOnlyList<PreviewPage> Load(DocumentRecord document)
    {
        var name = Path.GetFileNameWithoutExtension(document.SourcePath);
        var markdown = SplitPages(ReadOrEmpty(Path.Combine(document.OutputFolder, MarkdownRenderer.MarkdownFileName(name))));
        var vlmFile = SplitPages(ReadOrEmpty(Path.Combine(document.OutputFolder, MarkdownRenderer.VlmFileName(name))));

        // The Markdown of an extractor has no pages: the whole of it is shown with every page, and it stays where it is when the page changes (FR-114).
        var whole = document.Extractor is null ? null : ReadOrEmpty(Path.Combine(document.OutputFolder, MarkdownRenderer.MarkdownFileName(name))).Trim();

        var result = new List<PreviewPage>();
        foreach (var page in document.Pages)
        {
            var text = whole ?? markdown.GetValueOrDefault(page.Index, string.Empty);
            string? vlm = null;
            if (vlmFile.TryGetValue(page.Index, out var separate))
            {
                vlm = separate;
            }
            else if (VlmBlock().Match(text) is { Success: true } block)
            {
                vlm = block.Groups[1].Value.Trim();
            }

            string? image = null;
            if (page.ImageFile.Length > 0)
            {
                var full = Path.Combine(document.OutputFolder, page.ImageFile.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    image = full;
                }
            }

            result.Add(new PreviewPage(page.Index, page.SourceLabel, page.IsDeleted, page.Step3, image, text, vlm, page.IsOutOfRange));
        }

        return result;
    }

    /// <summary>The Markdown of a page without the result of the VLM that is written in it (it is shown on its own).</summary>
    public static string WithoutVlm(string pageMarkdown) => VlmBlock().Replace(pageMarkdown, string.Empty).Trim();

    /// <summary>The text of each page of a Markdown file: after its <c>&lt;!-- page: N --&gt;</c> line, to the next one. The words before the first page are not a page.</summary>
    public static IReadOnlyDictionary<int, string> SplitPages(string markdown)
    {
        var result = new Dictionary<int, string>();
        var markers = PageMarker().Matches(markdown);
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Index;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : markdown.Length;
            if (int.TryParse(markers[i].Groups[1].Value, out var number))
            {
                result[number] = markdown[start..end].Trim();
            }
        }

        return result;
    }

    // The file may be written at the same moment (the writer replaces it as a whole, but a slow disk can still fail the read).
    private static string ReadOrEmpty(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }

        return string.Empty;
    }
}
