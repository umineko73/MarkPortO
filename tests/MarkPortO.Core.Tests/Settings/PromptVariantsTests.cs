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

using System.Text.Json;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Settings;

public sealed class PromptVariantsTests
{
    [Fact]
    public void ANewSettingsFile_HasTheTwoExamplesForEveryKind_AndTheStandardPromptIsUsed()
    {
        var prompts = new PromptSettings();

        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            Assert.Equal([PromptPresets.LocalName, PromptPresets.CloudName], prompts.VariantsOf(kind).Items.Select(p => p.Name));
            Assert.Equal(string.Empty, prompts.VariantsOf(kind).Selected);
            Assert.Equal(prompts.For(kind, "ja"), prompts.Resolve(kind, "ja", null));
            Assert.Equal(prompts.For(kind, "ja"), prompts.Resolve(kind, "ja", "standard"));
            Assert.Equal(prompts.For(kind, "ja"), prompts.Resolve(kind, "ja", "標準"));
        }
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void TheExamplesDifferFromTheStandardPrompt_AndAllTakeThePageText(string language)
    {
        var prompts = new PromptSettings();

        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            var standard = prompts.Resolve(kind, language, null);
            foreach (var name in new[] { PromptPresets.LocalName, PromptPresets.CloudName })
            {
                var text = prompts.Resolve(kind, language, name);
                Assert.NotEqual(standard, text);
                Assert.True(PromptSupplement.UsesPageText(text), $"{kind} {name} {language}");
            }
        }
    }

    [Fact]
    public void AnExampleThatIsNotEdited_FollowsTheLanguage_AndAKeyIsAccepted()
    {
        var prompts = new PromptSettings();

        Assert.NotEqual(prompts.Resolve(DocumentKind.Xlsx, "ja", "local"), prompts.Resolve(DocumentKind.Xlsx, "en", "local"));
        Assert.Equal(prompts.Resolve(DocumentKind.Docx, "ja", PromptPresets.CloudName), prompts.Resolve(DocumentKind.Docx, "ja", "cloud"));
    }

    [Fact]
    public void ANameThatIsNotThere_IsAnError()
    {
        var prompts = new PromptSettings();

        Assert.Throws<PromptNotFoundException>(() => prompts.Resolve(DocumentKind.Pdf, "ja", "nothing"));
    }

    [Fact]
    public void AnEditedPrompt_IsUsedAsItIs()
    {
        var prompts = new PromptSettings();
        prompts.XlsxVariants.Items.Add(new NamedPrompt { Name = "mine", Text = "my text {pagetext}" });

        Assert.Equal("my text {pagetext}", prompts.Resolve(DocumentKind.Xlsx, "ja", "MINE"));
    }

    [Fact]
    public void ASettingsFileFromBefore_IsReadAndGetsTheExamples()
    {
        // only the standard prompts, as the earlier versions wrote them
        var json = "{\"docx\":\"d {pagetext}\",\"xlsx\":\"x\",\"pptx\":\"p\",\"pdf\":\"f\"}";

        var prompts = JsonSerializer.Deserialize<PromptSettings>(json, JsonDefaults.Options)!;

        Assert.Equal("d {pagetext}", prompts.Docx);
        Assert.Equal(2, prompts.XlsxVariants.Items.Count);
        Assert.Equal(string.Empty, prompts.XlsxVariants.Selected);
    }

    [Fact]
    public void TheChoicesAndTheNames_AreSavedAndRead()
    {
        var prompts = new PromptSettings();
        prompts.PptxVariants.Items.Add(new NamedPrompt { Name = "短い", Text = "short {pagetext}" });
        prompts.PptxVariants.Selected = "短い";
        prompts.PdfVariants.Items.Clear();

        var again = JsonSerializer.Deserialize<PromptSettings>(JsonSerializer.Serialize(prompts, JsonDefaults.Options), JsonDefaults.Options)!;

        Assert.Equal("短い", again.PptxVariants.Selected);
        Assert.Equal("short {pagetext}", again.Resolve(DocumentKind.Pptx, "ja", "短い"));
        Assert.Empty(again.PdfVariants.Items); // a list that was emptied stays empty
    }

    [Fact]
    public void TheSettingsAreInvalid_WhenANameIsUsedTwiceOrTheChoiceIsMissing()
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Prompts.DocxVariants.Items.Add(new NamedPrompt { Name = PromptPresets.LocalName, Text = "x" });
        settings.Vlm.Prompts.XlsxVariants.Selected = "gone";
        settings.Vlm.Prompts.PdfVariants.Items.Add(new NamedPrompt { Name = "bad--name", Text = "x" });

        var errors = settings.Validate();

        Assert.Contains(errors, e => e.Contains("docxVariants", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("xlsxVariants.selected", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("pdfVariants", StringComparison.Ordinal));
    }
}
