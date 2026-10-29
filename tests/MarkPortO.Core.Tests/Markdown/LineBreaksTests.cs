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

/// <summary>A line break inside a paragraph of the result of the VLM gets a &lt;br&gt;, so that a viewer does not show the lines as one.</summary>
public class LineBreaksTests
{
    [Fact]
    public void LinesOfOneParagraph_GetABreakAtTheEnd_ExceptTheLastOne()
    {
        Assert.Equal("Tokyo<br>\n1-2-3 Chiyoda<br>\nJapan", LineBreaks.Mark("Tokyo\n1-2-3 Chiyoda\nJapan"));
    }

    [Fact]
    public void ABlankLineSeparatesParagraphs_AndAParagraphOfOneLineIsLeftAlone()
    {
        Assert.Equal("a<br>\nb\n\nc\n\nd<br>\ne", LineBreaks.Mark("a\nb\n\nc\n\nd\ne"));
    }

    [Fact]
    public void Headings_Lists_Tables_Quotes_AndComments_AreLeftAlone()
    {
        const string Text = "# Title\nbody line\n- one\n- two\n1. first\n2. second\n| a | b |\n|---|---|\n| 1 | 2 |\n> quote\n> more\n<!-- note -->";

        Assert.Equal(Text, LineBreaks.Mark(Text));
    }

    [Fact]
    public void ACodeBlock_IsNotChanged_AndTextAroundItIs()
    {
        var text = "before 1\nbefore 2\n```mermaid\nA-->B\nB-->C\n```\nafter 1\nafter 2";

        Assert.Equal("before 1<br>\nbefore 2\n```mermaid\nA-->B\nB-->C\n```\nafter 1<br>\nafter 2", LineBreaks.Mark(text));
    }

    [Fact]
    public void ABreakThatIsAlreadyThere_IsNotDoubled()
    {
        Assert.Equal("a<br>\nb<br>\nc", LineBreaks.Mark("a<br>\nb  \nc"));
        Assert.Equal("a<br>\nb", LineBreaks.Mark("a<br>\nb"));
    }

    [Fact]
    public void AContinuedListItem_IsLeftAlone()
    {
        const string Text = "- item\n  continued\n  more";

        Assert.Equal(Text, LineBreaks.Mark(Text));
    }

    [Fact]
    public void Html_ShowsTheBreaks()
    {
        var html = MarkdownHtml.Render("a\nb\n\nc");

        Assert.Contains("a<br>", html);
        Assert.DoesNotContain("c<br>", html);
        Assert.DoesNotContain("c<br>", html);
        Assert.DoesNotContain("&lt;br&gt;", html);
    }
}
