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

using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Vlm;

/// <summary>FR-067: the code block that a VLM puts around the Markdown it wrote.</summary>
public class AnswerFencesTests
{
    private const string Table = "| a | b |\n|---|---|\n| 1 | 2 |";

    [Theory]
    [InlineData("```markdown")]
    [InlineData("```Markdown")]
    [InlineData("```md")]
    [InlineData("````markdown")]
    public void AMarkdownBlock_IsTakenOff_AndItsContentStays(string open)
    {
        var close = open.StartsWith("````", StringComparison.Ordinal) ? "````" : "```";

        Assert.Equal("Title\n" + Table + "\nAfter", AnswerFences.Strip("Title\n" + open + "\n" + Table + "\n" + close + "\nAfter"));
    }

    [Fact]
    public void ABlockOfOnlyTableRows_WithoutALanguage_IsTakenOff()
    {
        Assert.Equal("Before\n" + Table + "\nAfter", AnswerFences.Strip("Before\n```\n" + Table + "\n```\nAfter"));
    }

    [Fact]
    public void ABlockWithoutALanguage_ThatIsNotATable_Stays()
    {
        var text = "Before\n```\nint x = 1;\n```\nAfter";

        Assert.Equal(text, AnswerFences.Strip(text));
    }

    [Theory]
    [InlineData("mermaid", "graph TD; A-->B;")]
    [InlineData("csharp", "var x = 1;")]
    public void ABlockOfAnotherLanguage_Stays(string language, string code)
    {
        var text = "Before\n```" + language + "\n" + code + "\n```\nAfter";

        Assert.Equal(text, AnswerFences.Strip(text));
    }

    [Fact]
    public void AMermaidBlock_InsideAMarkdownBlock_Stays_AndTheOuterOneIsTakenOff()
    {
        var inner = "```mermaid\ngraph TD; A-->B;\n```";

        Assert.Equal("Text\n" + inner + "\nMore", AnswerFences.Strip("```markdown\nText\n" + inner + "\nMore\n```"));
    }

    [Fact]
    public void AMarkdownBlock_ThatWasCut_LosesItsOpeningLine()
    {
        Assert.Equal(Table, AnswerFences.Strip("```markdown\n" + Table));
    }

    [Fact]
    public void ATableInAMarkdownBlock_AndAMermaidBlock_AreHandledInTheSameAnswer()
    {
        var mermaid = "```mermaid\nA-->B\n```";

        Assert.Equal(Table + "\n" + mermaid, AnswerFences.Strip("```markdown\n" + Table + "\n```\n" + mermaid));
    }

    [Fact]
    public void AnAnswerWithoutAFence_IsReturnedAsItIs()
    {
        Assert.Same("plain\n", AnswerFences.Strip("plain\n"));
    }
}
