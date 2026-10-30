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

/// <summary>Bold text between ** and italic text between * (FR-101).</summary>
public class TextEmphasisTests
{
    private const char B = TextEmphasis.BoldMark;
    private const char I = TextEmphasis.ItalicMark;

    [Fact]
    public void Bold_AndItalic_AreWrittenBetweenTheirMarks()
    {
        Assert.Equal("これは**重要**です。*斜体*です", MarkdownText.ResolveMarks($"これは{B}重要{B}です。{I}斜体{I}です"));
    }

    [Fact]
    public void ThePlaceOfTheSpaces_StaysOutsideTheMarks()
    {
        Assert.Equal($" {B}a b{B} ", TextEmphasis.Bold(" a b "));
        Assert.Equal("   ", TextEmphasis.Bold("   "));
    }

    [Fact]
    public void TwoSpansNextToEachOther_AreOne()
    {
        var text = TextEmphasis.Bold("a") + TextEmphasis.Bold("b") + " " + TextEmphasis.Bold("c");

        Assert.Equal($"{B}ab c{B}", TextEmphasis.Merge(text));
    }

    [Fact]
    public void ItalicInBold_JoinsAcrossTheOuterSpans()
    {
        var text = TextEmphasis.Bold(TextEmphasis.Italic("a")) + TextEmphasis.Bold(TextEmphasis.Italic("b"));

        Assert.Equal("***ab***", MarkdownText.ResolveMarks(TextEmphasis.Merge(text)));
    }

    [Fact]
    public void ASpanWithPunctuationInsideAndALetterOutside_IsNotMarked()
    {
        // CommonMark does not take **「重要」**です for bold, so the marks are left out.
        Assert.Equal("これは「重要」です", MarkdownText.ResolveMarks($"これは{B}「重要」{B}です"));
    }

    [Fact]
    public void ASpanWithPunctuationInside_IsMarked_WhenSpacesAreOutside()
    {
        Assert.Equal("これは **「重要」** です", MarkdownText.ResolveMarks($"これは {B}「重要」{B} です"));
    }

    [Fact]
    public void AMarkWithoutAPartnerInItsLine_IsLeftOut()
    {
        Assert.Equal("a\nb c", MarkdownText.ResolveMarks($"a\n{B}b c"));
    }

    [Fact]
    public void TheMarks_AreNotInTheTextThatLooksForAPage()
    {
        Assert.Equal(DocxPageAssignerNormalise("これは重要です"), DocxPageAssignerNormalise($"これは{B}重要{B}です"));
    }

    [Fact]
    public void TheWrittenMarkdown_IsShownAsStrongAndEmphasis()
    {
        var html = MarkdownHtml.Render(MarkdownText.ResolveMarks($"これは{B}重要{B}です。次は{I}斜体{I}です。"));

        Assert.Contains("<strong>重要</strong>", html);
        Assert.Contains("<em>斜体</em>", html);
    }

    private static string DocxPageAssignerNormalise(string text) => MarkPortO.Core.Ooxml.DocxPageAssigner.Normalize(text);
}
