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

using MarkPortO.Core.Convert;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Tests.Settings;

/// <summary>FR-151: a preset puts the recommended settings of Step1 to Step3 for a purpose; they can be changed afterwards.</summary>
public class ConversionPresetTests
{
    [Fact]
    public void TheBuiltInPresets_PutTheirValues_AndLeaveTheOtherSettingsAlone()
    {
        var settings = AppSettings.CreateDefault();
        settings.Input.Paths = ["D:\\docs"];
        settings.Output.Folder = "D:\\out";
        settings.Engine.Kind = EngineKind.LibreOffice;
        settings.Input.IncludeHiddenSheets = true;
        settings.Vlm.Connections = [new ConnectionProfile { Name = "local", BaseUrl = "http://localhost:1234/v1", Model = "m" }];
        settings.Vlm.Selected = "local";

        ConversionPreset.FindBuiltIn("human")!.ApplyTo(settings);
        Assert.True(settings.Input.Emphasis);
        Assert.True(settings.Input.ExtractPictures);
        Assert.True(settings.Input.ExtractShapes);
        Assert.False(settings.Steps.Step2);
        Assert.True(settings.Steps.Step3);
        Assert.Equal(Step3Mode.Supplement, settings.Steps.Step3Mode);
        Assert.False(settings.Steps.VlmSeparate);
        Assert.Equal(Step1TablesMode.Replace, settings.Steps.Step1Tables);
        Assert.True(settings.Steps.DropRepeatedTables);
        Assert.False(settings.Output.StructureNote);

        ConversionPreset.FindBuiltIn("RAG")!.ApplyTo(settings);
        Assert.False(settings.Input.Emphasis);
        Assert.True(settings.Steps.Step2);
        Assert.Equal(PageImageStyle.Reference, settings.Steps.PageImage);
        Assert.True(settings.Output.StructureNote);

        // Not in a preset: the inputs, the output folder, the engine, the options of each kind of file.
        Assert.Equal(["D:\\docs"], settings.Input.Paths);
        Assert.Equal("D:\\out", settings.Output.Folder);
        Assert.Equal(EngineKind.LibreOffice, settings.Engine.Kind);
        Assert.True(settings.Input.IncludeHiddenSheets);
    }

    [Fact]
    public void WithoutAVlmConnection_APresetDoesNotTurnStep3On_AndIsNotModifiedByThat()
    {
        var settings = AppSettings.CreateDefault();
        var human = ConversionPreset.FindBuiltIn("human")!;
        human.ApplyTo(settings);

        Assert.False(settings.Steps.Step3); // a conversion can start on a machine without a VLM
        Assert.True(human.Matches(settings));

        settings.Vlm.Connections = [new ConnectionProfile { Name = "local", BaseUrl = "http://localhost:1234/v1", Model = "m" }];
        settings.Vlm.Selected = "local";
        Assert.False(human.Matches(settings)); // with a connection, the preset would turn Step3 on
        human.ApplyTo(settings);
        Assert.True(settings.Steps.Step3);
    }

    [Fact]
    public void AChangeAfterThePresetWasChosen_IsRecordedAsModified()
    {
        var settings = AppSettings.CreateDefault();
        var rag = ConversionPreset.FindBuiltIn("rag")!;
        rag.ApplyTo(settings);
        settings.PresetSelected = rag.Name;
        Assert.True(rag.Matches(settings));
        Assert.Equal("rag", ConversionPreset.RecordText(settings));

        settings.Steps.VlmSeparate = true;
        Assert.False(rag.Matches(settings));
        Assert.Equal("rag (modified)", ConversionPreset.RecordText(settings));

        settings.PresetSelected = string.Empty;
        Assert.Null(ConversionPreset.RecordText(settings));
    }

    [Fact]
    public void APromptNameThatTheSettingsDoNotHave_IsTheStandardPrompt()
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Prompts.DocxVariants.Selected = PromptPresets.CloudName;
        var preset = ConversionPreset.From(settings, "mine");
        preset.PromptNames.Docx = "a prompt that is not there";
        preset.PromptNames.Pptx = PromptPresets.LocalName;

        preset.ApplyTo(settings);

        Assert.Equal(string.Empty, settings.Vlm.Prompts.DocxVariants.Selected);
        Assert.Equal(PromptPresets.LocalName, settings.Vlm.Prompts.PptxVariants.Selected);
        Assert.True(preset.Matches(settings));
    }

    [Fact]
    public void APresetOfTheUser_IsSavedInTheSettingsFile_AndFoundByItsName()
    {
        var settings = AppSettings.CreateDefault();
        settings.Images.Dpi = 200;
        settings.Steps.Step3Mode = Step3Mode.Transcribe;
        settings.Presets.Add(ConversionPreset.From(settings, "Scans"));
        settings.PresetSelected = "Scans";

        var read = JsonDefaults.Deserialize<AppSettings>(JsonDefaults.Serialize(settings))!;

        var preset = ConversionPreset.Find(read, "scans");
        Assert.NotNull(preset);
        Assert.Equal(200, preset.Dpi);
        Assert.Equal(Step3Mode.Transcribe, preset.Step3Mode);
        Assert.Equal("Scans", read.PresetSelected);
        Assert.Empty(read.Validate());
    }

    [Theory]
    [InlineData("rag")]
    [InlineData("Human")]
    [InlineData(" ")]
    [InlineData("a--b")]
    public void ANameThatCannotBeUsed_IsAnErrorOfTheSettings(string name)
    {
        var settings = AppSettings.CreateDefault();
        settings.Presets.Add(ConversionPreset.From(settings, name));

        Assert.Contains(settings.Validate(), e => e.StartsWith("presets:", StringComparison.Ordinal));
        Assert.False(ConversionPreset.IsValidUserName(name));
    }

    [Fact]
    public void TheSameNameTwice_IsAnErrorOfTheSettings()
    {
        var settings = AppSettings.CreateDefault();
        settings.Presets.Add(ConversionPreset.From(settings, "Mine"));
        settings.Presets.Add(ConversionPreset.From(settings, "mine"));

        Assert.Contains(settings.Validate(), e => e.Contains("twice", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRun_RecordsThePreset_AndTheDescriptionAtTheTop()
    {
        var settings = AppSettings.CreateDefault();
        ConversionPreset.FindBuiltIn("human")!.ApplyTo(settings);
        settings.PresetSelected = "human";

        var options = ConvertRequest.OptionsFrom(settings);

        Assert.Equal("human", options.Preset);
        Assert.False(options.StructureNote);
        Assert.True(JsonDefaults.Deserialize<OptionsSnapshot>("{}")!.StructureNote); // a job from before wrote the description
        Assert.True(JsonDefaults.Deserialize<DocumentRecord>("{}")!.StructureNote);
    }

    [Fact]
    public void WithoutTheDescription_TheMarkdownStartsWithItsTitle_AndSaysThePreset()
    {
        var document = new DocumentRecord
        {
            SourcePath = "D:\\in\\report.docx",
            Kind = DocumentKind.Docx,
            MarkdownLanguage = "en",
            Step1 = StepStatus.Done,
            Step2 = StepStatus.NotApplicable,
            Step3 = StepStatus.NotApplicable,
            Converter = "MarkPortO 0.3.7",
            Preset = "human (modified)",
            Pages = [new PageRecord { Index = 1, SourceLabel = "page 1" }],
            Blocks = [new Block { Kind = BlockKind.Paragraph, Markdown = "Hello.", PageIndex = 1 }],
        };

        var with = MarkdownRenderer.Render(document);
        Assert.Contains("About this Markdown", with, StringComparison.Ordinal);

        document.StructureNote = false;
        var without = MarkdownRenderer.Render(document);
        Assert.DoesNotContain("About this Markdown", without, StringComparison.Ordinal);
        Assert.StartsWith("# report.docx\n", without, StringComparison.Ordinal);
        Assert.Contains("<!-- converter: MarkPortO 0.3.7; preset: human (modified) -->", without, StringComparison.Ordinal);
        Assert.Contains("<!-- page: 1 -->", without, StringComparison.Ordinal);
        Assert.Contains("Hello.", without, StringComparison.Ordinal);
    }
}
