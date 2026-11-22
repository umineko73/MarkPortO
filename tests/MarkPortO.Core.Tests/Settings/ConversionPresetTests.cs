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
using MarkPortO.Core.Tests.Localization;

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

    [Fact]
    public void TheBuiltInPresets_HaveTheValuesOfTheSpecification()
    {
        var human = ConversionPreset.FindBuiltIn(ConversionPreset.Human)!;
        var rag = ConversionPreset.FindBuiltIn(ConversionPreset.Rag)!;
        Assert.Equal([ConversionPreset.Human, ConversionPreset.Rag], ConversionPreset.BuiltIn.Select(p => p.Name));

        // The table of FR-151: what differs between the two.
        Assert.True(human.Emphasis);
        Assert.False(rag.Emphasis);
        Assert.False(human.Step2);
        Assert.True(rag.Step2);
        Assert.False(human.StructureNote);
        Assert.True(rag.StructureNote);

        // What they share.
        foreach (var preset in new[] { human, rag })
        {
            Assert.True(preset.ExtractPictures);
            Assert.True(preset.ExtractShapes);
            Assert.True(preset.GroupNearShapes);
            Assert.Equal(150, preset.Dpi);
            Assert.Equal(PageImageStyle.Reference, preset.PageImage);
            Assert.True(preset.Step3);
            Assert.Equal(Step3Mode.Supplement, preset.Step3Mode);
            Assert.False(preset.VlmSeparate);
            Assert.Equal(Step1TablesMode.Replace, preset.Step1Tables);
            Assert.True(preset.DropRepeatedTables);
            foreach (var kind in new[] { DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf })
            {
                Assert.Equal(string.Empty, preset.PromptNames.For(kind)); // the standard prompt
            }
        }
    }

    [Theory]
    [InlineData("human", "human")]
    [InlineData(" RAG ", "rag")]
    [InlineData("Mine", "mine")]
    [InlineData("MINE", "mine")]
    [InlineData(" mine ", "mine")]
    public void APreset_IsFoundByItsName_WithoutCapitalsAndSpacesAround(string asked, string found)
    {
        var settings = AppSettings.CreateDefault();
        settings.Presets.Add(ConversionPreset.From(settings, "mine"));

        Assert.Equal(found, ConversionPreset.Find(settings, asked)?.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nothing")]
    public void NoPreset_IsFoundForNoNameOrAnUnknownOne(string? name)
    {
        var settings = AppSettings.CreateDefault();
        settings.Presets.Add(ConversionPreset.From(settings, "mine"));

        Assert.Null(ConversionPreset.Find(settings, name));
    }

    [Fact]
    public void ABuiltInName_InTheSettingsFile_FindsTheBuiltInPreset()
    {
        // A file written by hand may have a preset of the user with a built-in name: it is an error, and it is never used.
        var settings = AppSettings.CreateDefault();
        var mine = ConversionPreset.From(settings, "rag");
        mine.Dpi = 300;
        settings.Presets.Add(mine);

        Assert.Equal(150, ConversionPreset.Find(settings, "rag")!.Dpi);
        Assert.Contains(settings.Validate(), e => e.StartsWith("presets:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBuiltInPresets_HaveNamesInTheLanguageOfTheScreen()
    {
        Assert.Equal("For reading", ConversionPreset.DisplayName("human"));
        Assert.Equal("For RAG", ConversionPreset.DisplayName("RAG"));
        Assert.Equal("Mine", ConversionPreset.DisplayName("Mine"));

        using (TestCulture.Use("ja"))
        {
            Assert.Equal("閲覧用", ConversionPreset.DisplayName("human"));
            Assert.Equal("RAG用", ConversionPreset.DisplayName("rag"));
            Assert.Equal("社内向け", ConversionPreset.DisplayName("社内向け"));
        }
    }

    [Fact]
    public void APresetMadeFromTheSettings_PutsTheSameValuesBack()
    {
        var settings = WithAConnection();
        settings.Input.Emphasis = false;
        settings.Input.ExtractPictures = false;
        settings.Input.ExtractShapes = true;
        settings.Input.GroupNearShapes = false;
        settings.Images.Dpi = 222;
        settings.Steps.Step2 = true;
        settings.Steps.PageImage = PageImageStyle.Link;
        settings.Steps.Step3 = true;
        settings.Steps.Step3Mode = Step3Mode.Transcribe;
        settings.Steps.VlmSeparate = true;
        settings.Steps.Step1Tables = Step1TablesMode.Off;
        settings.Steps.DropRepeatedTables = false;
        settings.Output.StructureNote = false;
        settings.Vlm.Prompts.DocxVariants.Selected = PromptPresets.CloudName;
        settings.Vlm.Prompts.XlsxVariants.Selected = PromptPresets.LocalName;
        var expected = JsonDefaults.Serialize(settings);

        var preset = ConversionPreset.From(settings, "all");
        var other = WithAConnection();
        preset.ApplyTo(other);
        Assert.True(preset.Matches(other));

        // Through the settings file too.
        var read = JsonDefaults.Deserialize<ConversionPreset>(JsonDefaults.Serialize(preset))!;
        var again = WithAConnection();
        read.ApplyTo(again);
        Assert.Equal(expected, JsonDefaults.Serialize(again));
        Assert.Equal(expected, JsonDefaults.Serialize(other));
    }

    [Fact]
    public void APreset_ChangesOnlyTheSettingsOfTheConversion()
    {
        var settings = WithAConnection();
        settings.Input.Paths = ["D:\\docs"];
        settings.Input.Recurse = true;
        settings.Input.Types = [DocumentKind.Pptx];
        settings.Input.IncludeHiddenSheets = true;
        settings.Input.IncludeHiddenSlides = true;
        settings.Input.IncludeOffSlideText = false;
        settings.Input.UsePdfText = false;
        settings.Output.Folder = "D:\\out";
        settings.Ui.Language = "en";
        settings.Ui.Theme = "dark";
        settings.Engine.Kind = EngineKind.LibreOffice;
        settings.Images.ExcelRange = ExcelRange.PrintArea;
        settings.PresetSelected = "something";
        var before = WithoutThePresetValues(settings);

        foreach (var preset in ConversionPreset.BuiltIn)
        {
            preset.ApplyTo(settings);
            Assert.Equal(before, WithoutThePresetValues(settings));
            Assert.Equal("local", settings.Vlm.Selected);
            Assert.Equal("something", settings.PresetSelected); // the caller chooses the name
        }
    }

    [Fact]
    public void AConnectionThatIsNotThere_DoesNotTurnStep3On()
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Selected = "gone"; // chosen, but deleted from the connections

        ConversionPreset.FindBuiltIn("rag")!.ApplyTo(settings);

        Assert.False(settings.Steps.Step3);
        Assert.True(ConversionPreset.FindBuiltIn("rag")!.Matches(settings));
    }

    [Fact]
    public void APresetWithStep3Off_TurnsStep3Off_AlsoWithAConnection()
    {
        var settings = WithAConnection();
        settings.Steps.Step3 = true;
        var mine = ConversionPreset.From(settings, "text only");
        mine.Step3 = false;

        mine.ApplyTo(settings);

        Assert.False(settings.Steps.Step3);
        Assert.True(mine.Matches(settings));
    }

    public static TheoryData<string> Changes => [.. ChangesOfTheSettings.Keys];

    [Theory]
    [MemberData(nameof(Changes))]
    public void EveryValueOfThePreset_IsChecked_WhenItIsChangedAfterwards(string change)
    {
        var settings = WithAConnection();
        var rag = ConversionPreset.FindBuiltIn("rag")!;
        rag.ApplyTo(settings);
        settings.PresetSelected = "rag";
        Assert.True(rag.Matches(settings));

        ChangesOfTheSettings[change](settings);

        Assert.False(rag.Matches(settings));
        Assert.Equal("rag (modified)", ConversionPreset.RecordText(settings));
    }

    [Fact]
    public void ASettingThatIsNotInThePreset_DoesNotMakeItModified()
    {
        var settings = WithAConnection();
        var human = ConversionPreset.FindBuiltIn("human")!;
        human.ApplyTo(settings);
        settings.PresetSelected = "human";

        settings.Output.Folder = "D:\\elsewhere";
        settings.Engine.Kind = EngineKind.LibreOffice;
        settings.Input.Types = [DocumentKind.Docx];
        settings.Images.ExcelRange = ExcelRange.Sheet;

        Assert.Equal("human", ConversionPreset.RecordText(settings));
    }

    [Fact]
    public void ThePresetOfTheUser_IsRecordedByItsName_AndADeletedOneIsNotRecorded()
    {
        var settings = AppSettings.CreateDefault();
        settings.Presets.Add(ConversionPreset.From(settings, "社内向け"));
        settings.PresetSelected = "社内向け";
        Assert.Equal("社内向け", ConversionPreset.RecordText(settings));
        Assert.Equal("社内向け", ConvertRequest.OptionsFrom(settings).Preset);

        settings.Images.Dpi = 96;
        Assert.Equal("社内向け (modified)", ConversionPreset.RecordText(settings));

        settings.Presets.Clear();
        Assert.Null(ConversionPreset.RecordText(settings));
        Assert.Null(ConvertRequest.OptionsFrom(settings).Preset);
    }

    [Theory]
    [InlineData("社内向け")]
    [InlineData("Scans 300 dpi")]
    [InlineData("a-b")]
    [InlineData("ragged")]
    public void ANameThatCanBeUsed(string name)
    {
        Assert.True(ConversionPreset.IsValidUserName(name));
        var settings = AppSettings.CreateDefault();
        settings.Presets.Add(ConversionPreset.From(settings, name));
        Assert.Empty(settings.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("--")]
    [InlineData(" human ")]
    public void ANameThatCannotBeUsed(string? name) => Assert.False(ConversionPreset.IsValidUserName(name));

    [Fact]
    public void APresetOfTheFile_WithoutSomeValues_HasTheDefaults()
    {
        // A file from an older version, or written by hand, may leave values out.
        var read = JsonDefaults.Deserialize<AppSettings>("{ \"presets\": [ { \"name\": \"Old\", \"dpi\": 200 } ], \"presetSelected\": \"Old\" }")!;

        var preset = Assert.Single(read.Presets);
        Assert.Equal(200, preset.Dpi);
        Assert.True(preset.StructureNote);
        Assert.True(preset.GroupNearShapes);
        Assert.Equal(Step3Mode.Supplement, preset.Step3Mode);
        Assert.Equal(PageImageStyle.Reference, preset.PageImage);
        Assert.Equal(string.Empty, preset.PromptNames.Docx);
        Assert.Empty(read.Validate());
    }

    [Fact]
    public void ThePresetsAreWrittenWithTheNamesOfTheirValues()
    {
        var settings = AppSettings.CreateDefault();
        var mine = ConversionPreset.From(settings, "Mine");
        mine.Step3Mode = Step3Mode.Transcribe;
        mine.PageImage = PageImageStyle.Link;
        settings.Presets.Add(mine);

        var json = JsonDefaults.Serialize(settings);

        Assert.Contains("\"step3Mode\": \"transcribe\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pageImage\": \"link\"", json, StringComparison.Ordinal);
        Assert.Contains("\"presetSelected\": \"\"", json, StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, Action<AppSettings>> ChangesOfTheSettings = new()
    {
        ["emphasis"] = s => s.Input.Emphasis = !s.Input.Emphasis,
        ["extract pictures"] = s => s.Input.ExtractPictures = !s.Input.ExtractPictures,
        ["extract shapes"] = s => s.Input.ExtractShapes = !s.Input.ExtractShapes,
        ["group near shapes"] = s => s.Input.GroupNearShapes = !s.Input.GroupNearShapes,
        ["dpi"] = s => s.Images.Dpi = 96,
        ["step2"] = s => s.Steps.Step2 = !s.Steps.Step2,
        ["page image"] = s => s.Steps.PageImage = PageImageStyle.Image,
        ["step3"] = s => s.Steps.Step3 = !s.Steps.Step3,
        ["step3 mode"] = s => s.Steps.Step3Mode = Step3Mode.Transcribe,
        ["vlm separate"] = s => s.Steps.VlmSeparate = !s.Steps.VlmSeparate,
        ["step1 tables"] = s => s.Steps.Step1Tables = Step1TablesMode.Off,
        ["repeated tables"] = s => s.Steps.DropRepeatedTables = !s.Steps.DropRepeatedTables,
        ["structure note"] = s => s.Output.StructureNote = !s.Output.StructureNote,
        ["prompt docx"] = s => s.Vlm.Prompts.DocxVariants.Selected = PromptPresets.CloudName,
        ["prompt xlsx"] = s => s.Vlm.Prompts.XlsxVariants.Selected = PromptPresets.LocalName,
        ["prompt pptx"] = s => s.Vlm.Prompts.PptxVariants.Selected = PromptPresets.CloudName,
        ["prompt pdf"] = s => s.Vlm.Prompts.PdfVariants.Selected = PromptPresets.LocalName,
    };

    private static AppSettings WithAConnection()
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections = [new ConnectionProfile { Name = "local", BaseUrl = "http://localhost:1234/v1", Model = "m" }];
        settings.Vlm.Selected = "local";
        return settings;
    }

    /// <summary>The settings as JSON, with the values that a preset has put back to those of a fresh preset of the defaults.</summary>
    private static string WithoutThePresetValues(AppSettings settings)
    {
        var copy = JsonDefaults.Deserialize<AppSettings>(JsonDefaults.Serialize(settings))!;
        var selected = copy.PresetSelected;
        ConversionPreset.From(AppSettings.CreateDefault(), "x").ApplyTo(copy);
        copy.PresetSelected = selected;
        return JsonDefaults.Serialize(copy);
    }
}
