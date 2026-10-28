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

public class MarkdownTextTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("<!-- page: 1 -->", "&lt;!-- page: 1 -->")]
    [InlineData("# heading", "\\# heading")]
    [InlineData("#hashtag", "#hashtag")]
    [InlineData("> quote", "\\> quote")]
    [InlineData("- item", "\\- item")]
    [InlineData("1. item", "\\1. item")]
    [InlineData("| a | b |", "\\| a | b |")]
    [InlineData("3 - 2 = 1", "3 - 2 = 1")]
    [InlineData("first\n# second", "first\n\\# second")]
    [InlineData("日本語 *強調*", "日本語 *強調*")]
    public void Escape_NeutralisesOnlyWhatWouldBeReadAsMarkup(string input, string expected)
    {
        Assert.Equal(expected, MarkdownText.Escape(input));
    }

    [Fact]
    public void EscapeCell_RemovesPipesAndLineBreaks()
    {
        Assert.Equal("a\\|b<br>c", MarkdownText.EscapeCell("a|b\nc"));
    }

    [Fact]
    public void Table_PadsShortRowsAndUsesTheFirstRowAsHeader()
    {
        var table = MarkdownText.Table([["h1", "h2", "h3"], ["a"], ["b", "c", "d"]]);

        Assert.Equal("| h1 | h2 | h3 |\n|---|---|---|\n| a |  |  |\n| b | c | d |", table);
    }

    [Fact]
    public void Table_WithoutAHeader_PutsAnEmptyHeaderBeforeTheFirstRow()
    {
        var table = MarkdownText.Table([["a", "b"], ["c", "d"]], hasHeader: false);

        Assert.Equal("|  |  |\n|---|---|\n| a | b |\n| c | d |", table);
    }

    [Fact]
    public void Table_KeepsTheMergedMark_AndDropsTheCharacterFromTextOfTheDocument()
    {
        var table = MarkdownText.Table([["a" + MarkdownText.MergedLeft, MarkdownText.MergedLeft], ["b", MarkdownText.MergedUp]], hasHeader: false);

        Assert.Equal("|  |  |\n|---|---|\n| a | " + MarkdownText.MergedLeft + " |\n| b | " + MarkdownText.MergedUp + " |", table);
        Assert.Equal("|  |  |\n|---|---|\n| a | < |\n| b | ^ |", MarkdownText.ResolveMarks(table));
    }

    [Fact]
    public void Table_RepeatsTheValue_WhereAMergeTouchesTheHeaderRow()
    {
        var table = MarkdownText.Table([["H", MarkdownText.MergedLeft, "X"], ["V", MarkdownText.MergedUp, MarkdownText.MergedUp], ["a", "b", "c"]]);

        Assert.Equal("| H | H | X |\n|---|---|---|\n| V | H | X |\n| a | b | c |", MarkdownText.ResolveMarks(table));
    }

    [Fact]
    public void Table_WritesACellThatIsOnlyAMark_WithABackslash()
    {
        var table = MarkdownText.Table([["a", "<"], ["^", "b"]], hasHeader: false);

        Assert.Equal("|  |  |\n|---|---|\n| a | \\< |\n| \\^ | b |", table);
    }

    [Fact]
    public void Table_WithNoRows_IsEmpty()
    {
        Assert.Equal(string.Empty, MarkdownText.Table([]));
    }
}
