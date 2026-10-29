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

/// <summary>A line break inside a paragraph is shown as a line break in the page of the preview (the Markdown is not changed).</summary>
public class PreviewLineBreakTests
{
    [Theory]
    [InlineData("Tokyo\n1-2-3 Chiyoda\nJapan")]
    [InlineData("　一行目\n　二行目\n三行目")]
    [InlineData("目次\n2. 概要 ..... 3\n3. 方法 ..... 5")] // an ordered list that does not start with 1 does not interrupt a paragraph
    [InlineData("| 概要 | 3 |\n| 方法 | 5 |")] // lines with | but without a header row are no table
    [InlineData("a  \nb\nc")]
    [InlineData("a<br>\nb\nc")]
    public void LinesOfAParagraph_AreShownAsLines(string markdown)
    {
        var html = MarkdownHtml.Render(markdown);

        var lines = markdown.Split('\n').Length;
        Assert.True(CountOf(html, "<br") >= lines - 1, html);
    }

    [Fact]
    public void AParagraphOfOneLine_HasNoBreak()
    {
        Assert.DoesNotContain("<br", MarkdownHtml.Render("one line\n\nanother paragraph"));
    }

    [Fact]
    public void ACodeBlock_KeepsItsLinesAsTheyAre()
    {
        var html = MarkdownHtml.Render("```text\nA\nB\n```");

        Assert.DoesNotContain("<br", html);
        Assert.Contains("A\nB", html);
    }

    [Fact]
    public void ATable_AndHeadings_AreNotChanged()
    {
        var html = MarkdownHtml.Render("# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |");

        Assert.DoesNotContain("<br", html);
        Assert.Contains("<table>", html);
    }

    [Fact]
    public void TheBreakOfACellOfATable_IsStillShown()
    {
        Assert.Contains("x<br>y", MarkdownHtml.Render("| a |\n|---|\n| x<br>y |"));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
