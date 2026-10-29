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

using MarkPortO.Core.Markdown;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-068: the page of HTML that the preview shows.</summary>
public class MarkdownHtmlTests
{
    [Fact]
    public void Render_WritesHeadingsTablesAndPictures()
    {
        var html = MarkdownHtml.Render("<!-- page: 1 -->\n\n![Page 1](images/page-001.png)\n\n# Title\n\n| a | b |\n|---|---|\n| 1 | 2<br>3 |\n", showImages: true);

        Assert.Contains(">Title</h1>", html);
        Assert.Contains("<th>a</th>", html);
        Assert.Contains("<td>2<br>3</td>", html);
        Assert.Contains("<img src=\"images/page-001.png\" alt=\"Page 1\" />", html);
        Assert.Contains("<base href=\"https://preview.local/\">", html);
        Assert.DoesNotContain("page: 1", html);
    }

    [Fact]
    public void Render_LeavesOutALineThatIsOnlyAPicture_UnlessAskedToShowIt()
    {
        var html = MarkdownHtml.Render("![Page 1](images/page-001.png)\n\ntext with ![inline](a.png) picture\n");

        Assert.DoesNotContain("page-001.png", html);
        Assert.Contains("<img src=\"a.png\"", html);
    }

    [Fact]
    public void Render_DoesNotLetTheTextOfADocumentRunOrReachOut()
    {
        var html = MarkdownHtml.Render("<script>alert(1)</script>\n\n<img src=\"https://evil.example/x.png\" onerror=\"alert(1)\">\n\n[link](https://evil.example)");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img src=\"https://evil", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("img-src https://preview.local data:", html);
    }

    [Fact]
    public void Render_ShowsTheMarksOfMergedCellsAsText()
    {
        var html = MarkdownHtml.Render("| a | b |\n|---|---|\n| c | < |\n| ^ | d |");

        Assert.Contains("<td>&lt;</td>", html);
        Assert.Contains("<td>^</td>", html);
    }

    [Fact]
    public void Render_AllowsOnlyTheOneScriptOfTheDiagrams_AndWritesNoScriptOfItsOwn()
    {
        var html = MarkdownHtml.Render("# T\n\n```mermaid\nflowchart LR\n  A --> B\n```\n\n<script>alert(1)</script>\n");

        Assert.Contains("script-src https://mermaid.local/mermaid.min.js", html);
        Assert.DoesNotContain("script-src 'unsafe-inline'", html);
        Assert.DoesNotContain("<script", html); // the text of a page cannot bring a script
        Assert.Contains("<pre class=\"mermaid\">flowchart LR", html); // the preview draws this block with the library
    }
}
