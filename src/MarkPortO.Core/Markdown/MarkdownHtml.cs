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
using Markdig;

namespace MarkPortO.Core.Markdown;

/// <summary>
/// Turns the Markdown of a page into a page of HTML for the preview (FR-068). The page has no script of its own, no outside address and
/// no link that can be followed: its pictures are looked up under <see cref="Host"/>, which the preview maps to the folder of the document.
/// The only script that it may load is <see cref="MermaidScript"/>, which the preview starts when the page has a Mermaid block (FR-083).
/// </summary>
public static partial class MarkdownHtml
{
    /// <summary>The name that the preview maps to the output folder of the document, so that <c>images/page-001.png</c> is found.</summary>
    public const string Host = "preview.local";

    /// <summary>The name that the preview maps to the folder of the program that holds Mermaid (FR-083).</summary>
    public const string MermaidHost = "mermaid.local";

    /// <summary>The one script that the pages may load: the diagram library, from the folder of the program.</summary>
    public const string MermaidScript = "https://" + MermaidHost + "/mermaid.min.js";

    // A line break in a table cell is written as <br>; the raw HTML of the text is not allowed, so it is put back after the conversion.
    private const string BreakToken = "MPBRMARK";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^[ 	]*!\[[^\]]*\]\([^)]*\)[ 	]*$", RegexOptions.Multiline)]
    private static partial Regex ImageLine();

    /// <summary>
    /// The page of HTML. The comments of the Markdown (page, sheet, VLM marks) are not shown. A line that is only a picture is left
    /// out unless <paramref name="showImages"/> is set: the preview shows the image of the page above the text.
    /// </summary>
    public static string Render(string markdown, bool showImages = false)
    {
        var text = Comment().Replace(markdown, string.Empty);
        if (!showImages)
        {
            text = ImageLine().Replace(text, string.Empty);
        }

        // A line break inside a paragraph is only a space in Markdown: in the page it is shown as a line break (the Markdown itself is not changed).
        text = LineBreaks.Mark(text);
        text = text.Replace("<br>", BreakToken, StringComparison.OrdinalIgnoreCase);
        var body = Markdig.Markdown.ToHtml(text, Pipeline).Replace(BreakToken, "<br>", StringComparison.Ordinal);
        return "<!doctype html><html><head><meta charset=\"utf-8\">"
            + "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src https://" + Host + " data:; style-src 'unsafe-inline'; script-src " + MermaidScript + "\">"
            + "<base href=\"https://" + Host + "/\">"
            + "<style>"
            + ":root{color-scheme:light dark}body{font-family:'Segoe UI','Yu Gothic UI',sans-serif;font-size:14px;line-height:1.6;margin:12px 16px}"
            + "img{max-width:100%}table{border-collapse:collapse;margin:8px 0}th,td{border:1px solid #8888;padding:4px 8px;vertical-align:top}"
            + "th{background:#8882}blockquote{margin:8px 0;padding:0 12px;border-left:4px solid #8886;color:inherit;opacity:.85}"
            + "pre,code{font-family:Consolas,'Cascadia Mono',monospace}pre{background:#8881;padding:8px;overflow:auto}"
            + ".mermaid-figure{margin:8px 0;overflow:auto}.mermaid-figure svg{max-width:100%;height:auto}"
            + "</style></head><body>" + body + "</body></html>";
    }
}
