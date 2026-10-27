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

using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Vlm;

/// <summary>FR-042: the fixed words that follow the prompt of the user.</summary>
public class PromptSupplementTests
{
    [Theory]
    [InlineData(PromptSettings.DefaultDocx)]
    [InlineData(PromptSettings.DefaultXlsx)]
    [InlineData(PromptSettings.DefaultPptx)]
    public void TheDefaultPrompts_TakeInTheTextOfThePage(string prompt)
    {
        Assert.True(PromptSupplement.UsesPageText(prompt));

        var text = PromptSupplement.Build(prompt, "ja", pageText: "PAGE-TEXT-HERE");

        Assert.Contains("PAGE-TEXT-HERE", text);
        Assert.DoesNotContain(PromptSupplement.PageTextPlaceholder, text);
    }

    [Theory]
    [InlineData(DocumentKind.Docx)]
    [InlineData(DocumentKind.Xlsx)]
    [InlineData(DocumentKind.Pptx)]
    public void ADefaultPrompt_FollowsTheLanguage_AndAnEditedOneDoesNot(DocumentKind kind)
    {
        foreach (var language in new[] { "ja", "en" })
        {
            Assert.True(PromptSupplement.UsesPageText(PromptSettings.DefaultFor(kind, language)));
            Assert.Contains("ermaid", PromptSettings.DefaultFor(kind, language));
        }

        Assert.NotEqual(PromptSettings.DefaultFor(kind, "ja"), PromptSettings.DefaultFor(kind, "en"));

        // The settings file keeps the default of the language it was created in; it is read in the language in use.
        var prompts = new PromptSettings();
        Set(prompts, kind, PromptSettings.DefaultFor(kind, "ja"));
        Assert.Equal(PromptSettings.DefaultFor(kind, "en"), prompts.For(kind, "en"));

        Set(prompts, kind, "my own prompt {pagetext}");
        Assert.Equal("my own prompt {pagetext}", prompts.For(kind, "en"));
        Assert.Equal("my own prompt {pagetext}", prompts.For(kind, "ja"));
    }

    private static void Set(PromptSettings prompts, DocumentKind kind, string text)
    {
        switch (kind)
        {
            case DocumentKind.Docx: prompts.Docx = text; break;
            case DocumentKind.Xlsx: prompts.Xlsx = text; break;
            default: prompts.Pptx = text; break;
        }
    }

    [Theory]
    [InlineData("\n\n")]
    [InlineData("\r\n\r\n")]
    public void TheSupplement_GoesBeforeTheParagraphWithThePageText(string blankLine)
    {
        var prompt = "Read the page." + blankLine + "Text:" + blankLine.Substring(0, blankLine.Length / 2) + "{pagetext}";

        var text = PromptSupplement.Build(prompt, "en", pageText: "PAGE-TEXT");

        Assert.True(text.IndexOf("header row", StringComparison.Ordinal) < text.IndexOf("PAGE-TEXT", StringComparison.Ordinal));
        Assert.StartsWith("Read the page. ", text);
        Assert.EndsWith("PAGE-TEXT", text);
    }

    [Fact]
    public void TheSupplement_FollowsThePromptOfTheUser()
    {
        var text = PromptSupplement.Build("USER-PROMPT", "en");

        Assert.StartsWith("USER-PROMPT ", text);
        Assert.Contains("header row", text);
    }

    [Fact]
    public void TheSupplement_IsWrittenInTheLanguageOfTheConversion()
    {
        Assert.Contains("見出し行", PromptSupplement.Build("p", "ja"));
        Assert.DoesNotContain("見出し行", PromptSupplement.Build("p", "en"));
    }

    [Fact]
    public void ABand_SaysWhichPartOfThePageItIs()
    {
        var text = PromptSupplement.Build("p", "en", band: 2, bands: 3);

        Assert.Contains("part 2 of 3", text);
    }

    [Fact]
    public void AWholePage_DoesNotTalkAboutParts()
    {
        Assert.DoesNotContain("part", PromptSupplement.Build("p", "en"));
        Assert.DoesNotContain("part", PromptSupplement.Build("p", "en", band: 1, bands: 1));
    }
}
