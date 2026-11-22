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
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Settings;

/// <summary>
/// A preset of the conversion (FR-151): the settings of Step1 to Step3 that make the Markdown fit a purpose. Choosing it puts its values into the
/// settings; they can be changed afterwards. The inputs, the output folder, the engine, the VLM connection, the language and the options of each
/// kind of file are not in it: they depend on the machine and on the documents, not on the purpose.
/// </summary>
public sealed class ConversionPreset
{
    /// <summary>The name of the built-in preset for people who read the Markdown in place of the Office file.</summary>
    public const string Human = "human";

    /// <summary>The name of the built-in preset for a language model (retrieval-augmented generation).</summary>
    public const string Rag = "rag";

    public string Name { get; set; } = string.Empty;

    public bool Emphasis { get; set; }

    public bool ExtractPictures { get; set; }

    public bool ExtractShapes { get; set; }

    public bool GroupNearShapes { get; set; } = true;

    public int Dpi { get; set; } = SettingsLimits.DefaultDpi;

    public bool Step2 { get; set; }

    public PageImageStyle PageImage { get; set; } = PageImageStyle.Reference;

    public bool Step3 { get; set; }

    public Step3Mode Step3Mode { get; set; } = Step3Mode.Supplement;

    public bool VlmSeparate { get; set; }

    public Step1TablesMode Step1Tables { get; set; }

    public bool DropRepeatedTables { get; set; }

    /// <summary>The prompt of each kind of file, by name; empty is the standard one, and so is a name that the settings do not have.</summary>
    public PromptNames PromptNames { get; set; } = new();

    public bool StructureNote { get; set; } = true;

    /// <summary>The built-in presets, in the order the list shows them.</summary>
    public static IReadOnlyList<ConversionPreset> BuiltIn { get; } =
    [
        new()
        {
            Name = Human,
            Emphasis = true,
            ExtractPictures = true,
            ExtractShapes = true,
            GroupNearShapes = true,
            Dpi = 150,
            Step2 = false,
            PageImage = PageImageStyle.Reference,
            Step3 = true,
            Step3Mode = Step3Mode.Supplement,
            VlmSeparate = false,
            Step1Tables = Step1TablesMode.Replace,
            DropRepeatedTables = true,
            StructureNote = false,
        },
        new()
        {
            Name = Rag,
            Emphasis = false,
            ExtractPictures = true,
            ExtractShapes = true,
            GroupNearShapes = true,
            Dpi = 150,
            Step2 = true,
            PageImage = PageImageStyle.Reference,
            Step3 = true,
            Step3Mode = Step3Mode.Supplement,
            VlmSeparate = false,
            Step1Tables = Step1TablesMode.Replace,
            DropRepeatedTables = true,
            StructureNote = true,
        },
    ];

    /// <summary>A built-in preset of this name (the names are not case-sensitive), or null.</summary>
    public static ConversionPreset? FindBuiltIn(string? name) =>
        BuiltIn.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset of this name: a built-in one, or one of the settings; null when there is none.</summary>
    public static ConversionPreset? Find(AppSettings settings, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : FindBuiltIn(name) ?? settings.Presets.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The name that the screen shows: the built-in ones in the language of the screen, the others as they are.</summary>
    public static string DisplayName(string name) => name switch
    {
        _ when string.Equals(name, Human, StringComparison.OrdinalIgnoreCase) => Loc.T("For people to read"),
        _ when string.Equals(name, Rag, StringComparison.OrdinalIgnoreCase) => Loc.T("For a language model (RAG)"),
        _ => name,
    };

    /// <summary>The values of the settings now, as a preset with this name.</summary>
    public static ConversionPreset From(AppSettings settings, string name) => new()
    {
        Name = name,
        Emphasis = settings.Input.Emphasis,
        ExtractPictures = settings.Input.ExtractPictures,
        ExtractShapes = settings.Input.ExtractShapes,
        GroupNearShapes = settings.Input.GroupNearShapes,
        Dpi = settings.Images.Dpi,
        Step2 = settings.Steps.Step2,
        PageImage = settings.Steps.PageImage,
        Step3 = settings.Steps.Step3,
        Step3Mode = settings.Steps.Step3Mode,
        VlmSeparate = settings.Steps.VlmSeparate,
        Step1Tables = settings.Steps.Step1Tables,
        DropRepeatedTables = settings.Steps.DropRepeatedTables,
        PromptNames = new PromptNames
        {
            Docx = settings.Vlm.Prompts.DocxVariants.Selected,
            Xlsx = settings.Vlm.Prompts.XlsxVariants.Selected,
            Pptx = settings.Vlm.Prompts.PptxVariants.Selected,
            Pdf = settings.Vlm.Prompts.PdfVariants.Selected,
        },
        StructureNote = settings.Output.StructureNote,
    };

    /// <summary>Puts the values of the preset into the settings. A prompt name that the settings do not have becomes the standard prompt.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.Input.Emphasis = Emphasis;
        settings.Input.ExtractPictures = ExtractPictures;
        settings.Input.ExtractShapes = ExtractShapes;
        settings.Input.GroupNearShapes = GroupNearShapes;
        settings.Images.Dpi = Dpi;
        settings.Steps.Step2 = Step2;
        settings.Steps.PageImage = PageImage;
        settings.Steps.Step3 = Step3For(settings);
        settings.Steps.Step3Mode = Step3Mode;
        settings.Steps.VlmSeparate = VlmSeparate;
        settings.Steps.Step1Tables = Step1Tables;
        settings.Steps.DropRepeatedTables = DropRepeatedTables;
        settings.Output.StructureNote = StructureNote;
        foreach (var kind in new[] { DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf })
        {
            var variants = settings.Vlm.Prompts.VariantsOf(kind);
            var name = PromptNames.For(kind);
            variants.Selected = variants.Find(name) is { } found ? found.Name : string.Empty;
        }
    }

    /// <summary>
    /// Step3 of the preset for these settings: on only when a VLM connection is chosen. A preset that reads with the VLM must not stop a conversion on a
    /// machine without one; with a connection set up later, choosing the preset again turns Step3 on.
    /// </summary>
    private bool Step3For(AppSettings settings) => Step3 && settings.FindConnection(settings.Vlm.Selected) is not null;

    /// <summary>The settings have the values of the preset: nothing was changed after it was chosen.</summary>
    public bool Matches(AppSettings settings)
    {
        var now = From(settings, Name);
        bool SamePrompt(DocumentKind kind)
        {
            // A name that the settings do not have is the standard prompt, as when it is applied.
            var wanted = settings.Vlm.Prompts.VariantsOf(kind).Find(PromptNames.For(kind)) is { } found ? found.Name : string.Empty;
            return string.Equals(wanted, now.PromptNames.For(kind), StringComparison.OrdinalIgnoreCase);
        }

        return Emphasis == now.Emphasis && ExtractPictures == now.ExtractPictures && ExtractShapes == now.ExtractShapes && GroupNearShapes == now.GroupNearShapes
            && Dpi == now.Dpi && Step2 == now.Step2 && PageImage == now.PageImage && Step3For(settings) == now.Step3 && Step3Mode == now.Step3Mode
            && VlmSeparate == now.VlmSeparate && Step1Tables == now.Step1Tables && DropRepeatedTables == now.DropRepeatedTables
            && StructureNote == now.StructureNote
            && SamePrompt(DocumentKind.Docx) && SamePrompt(DocumentKind.Xlsx) && SamePrompt(DocumentKind.Pptx) && SamePrompt(DocumentKind.Pdf);
    }

    /// <summary>
    /// What the Markdown and the job record of the preset that the settings were made with: its name, with <c>(modified)</c> when the settings were changed
    /// after it was chosen; null when no preset is chosen or it is not there any more.
    /// </summary>
    public static string? RecordText(AppSettings settings) =>
        Find(settings, settings.PresetSelected) is { } preset
            ? preset.Matches(settings) ? preset.Name : preset.Name + " (modified)"
            : null;

    /// <summary>A name that a preset of the user can have: not empty, not a built-in name, no line break and no <c>--</c> (it goes into a comment).</summary>
    public static bool IsValidUserName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && FindBuiltIn(name) is null
        && !name.Contains('\n', StringComparison.Ordinal) && !name.Contains('\r', StringComparison.Ordinal) && !name.Contains("--", StringComparison.Ordinal);
}
