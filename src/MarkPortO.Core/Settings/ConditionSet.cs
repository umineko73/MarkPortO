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

using System.Globalization;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Settings;

/// <summary>
/// "The items of the conditions" (FR-158): what a conversion was made with, and what the screen is set to. The conditions are put back from
/// the record of an earlier conversion (FR-158), compared when inputs are added (FR-160), and kept in a work set (FR-159). The inputs, the
/// output folder, the connections themselves (keys, commands) and the look of the screen are not in it.
/// </summary>
public sealed class ConditionSet
{
    private static readonly DocumentKind[] Kinds = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx, DocumentKind.Pdf];

    public List<DocumentKind> Types { get; set; } = [];

    public bool Recurse { get; set; }

    public ExtractorChoice Extractors { get; set; } = new();

    public EngineKind Engine { get; set; }

    public int Dpi { get; set; } = SettingsLimits.DefaultDpi;

    public ExcelRange ExcelRange { get; set; }

    public bool IncludeHiddenSheets { get; set; }

    public bool IncludeHiddenSlides { get; set; }

    public bool IncludeOffSlideText { get; set; } = true;

    public bool Emphasis { get; set; } = true;

    public bool UsePdfText { get; set; } = true;

    public bool ExtractPictures { get; set; }

    public bool ExtractShapes { get; set; }

    public bool GroupNearShapes { get; set; } = true;

    /// <summary><see cref="InputSettings.ShapeGroupGapPoints"/> (a set saved before it was one of the conditions has the default).</summary>
    public double ShapeGroupGapPoints
    {
        get => _shapeGroupGapPoints;
        set => _shapeGroupGapPoints = value is >= 0 and <= 720 ? value : 24;
    }

    private double _shapeGroupGapPoints = 24;

    public bool Step2 { get; set; } = true;

    public PageImageStyle PageImage { get; set; }

    public bool Step3 { get; set; }

    public Step3Mode Step3Mode { get; set; } = Step3Mode.Supplement;

    public bool VlmSeparate { get; set; }

    public Step1TablesMode Step1Tables { get; set; }

    public bool DropRepeatedTables { get; set; }

    public bool StructureNote { get; set; } = true;

    public bool Chunks { get; set; }

    public bool ExcelPerSheet { get; set; }

    public FolderLayout FolderLayout { get; set; }

    /// <summary>The preset the conditions were chosen with; empty when none (or it was changed after).</summary>
    public string Preset { get; set; } = string.Empty;

    /// <summary>The name of the VLM connection (only the name).</summary>
    public string Connection { get; set; } = string.Empty;

    public PromptNames PromptNames { get; set; } = new();

    /// <summary>The conditions the settings (the screen) have now.</summary>
    public static ConditionSet From(AppSettings settings) => new()
    {
        Types = [.. settings.Input.Types],
        Recurse = settings.Input.Recurse,
        Extractors = settings.Input.Extractors.Clone(),
        Engine = settings.Engine.Kind,
        Dpi = settings.Images.Dpi,
        ExcelRange = settings.Images.ExcelRange,
        IncludeHiddenSheets = settings.Input.IncludeHiddenSheets,
        IncludeHiddenSlides = settings.Input.IncludeHiddenSlides,
        IncludeOffSlideText = settings.Input.IncludeOffSlideText,
        Emphasis = settings.Input.Emphasis,
        UsePdfText = settings.Input.UsePdfText,
        ExtractPictures = settings.Input.ExtractPictures,
        ExtractShapes = settings.Input.ExtractShapes,
        GroupNearShapes = settings.Input.GroupNearShapes,
        ShapeGroupGapPoints = settings.Input.ShapeGroupGapPoints,
        Step2 = settings.Steps.Step2,
        PageImage = settings.Steps.PageImage,
        Step3 = settings.Steps.Step3,
        Step3Mode = settings.Steps.Step3Mode,
        VlmSeparate = settings.Steps.VlmSeparate,
        Step1Tables = settings.Steps.Step1Tables,
        DropRepeatedTables = settings.Steps.DropRepeatedTables,
        StructureNote = settings.Output.StructureNote,
        Chunks = settings.Output.Chunks,
        ExcelPerSheet = settings.Output.ExcelPerSheet,
        FolderLayout = settings.Output.FolderLayout,
        Preset = settings.PresetSelected,
        Connection = settings.Vlm.Selected,
        PromptNames = new PromptNames
        {
            Docx = settings.Vlm.Prompts.DocxVariants.Selected,
            Xlsx = settings.Vlm.Prompts.XlsxVariants.Selected,
            Pptx = settings.Vlm.Prompts.PptxVariants.Selected,
            Pdf = settings.Vlm.Prompts.PdfVariants.Selected,
        },
    };

    /// <summary>
    /// The conditions of a document of an earlier conversion (FR-158): those of its job, with what the document has of its own (the steps it
    /// has, also one that was added, and the conditions of writing that a resume changed).
    /// </summary>
    public static ConditionSet FromRecord(JobState job, DocumentRecord document)
    {
        var o = job.Options;
        return new ConditionSet
        {
            Types = [.. o.Types],
            Recurse = o.Recurse,
            Extractors = o.Extractors.Clone(),
            Engine = o.Engine,
            Dpi = o.Dpi,
            ExcelRange = o.ExcelRange,
            IncludeHiddenSheets = o.IncludeHiddenSheets,
            IncludeHiddenSlides = o.IncludeHiddenSlides,
            IncludeOffSlideText = o.IncludeOffSlideText,
            Emphasis = o.Emphasis,
            UsePdfText = o.UsePdfText,
            ExtractPictures = o.ExtractPictures,
            ExtractShapes = o.ExtractShapes,
            GroupNearShapes = o.GroupNearShapes,
            ShapeGroupGapPoints = o.ShapeGroupGapPoints,
            Step2 = document.Step2 != StepStatus.NotApplicable,
            PageImage = document.PageImage,
            Step3 = document.Step3 != StepStatus.NotApplicable,
            Step3Mode = document.Step3Mode,
            VlmSeparate = document.VlmSeparate,
            Step1Tables = document.Step1Tables,
            DropRepeatedTables = document.DropRepeatedTables,
            StructureNote = document.StructureNote,
            Chunks = document.Chunks,
            ExcelPerSheet = document.ExcelPerSheet,
            FolderLayout = o.FolderLayout,
            Preset = PresetName(o.Preset),
            Connection = o.Connection ?? string.Empty,
            PromptNames = new PromptNames { Docx = o.PromptNames.Docx, Xlsx = o.PromptNames.Xlsx, Pptx = o.PromptNames.Pptx, Pdf = o.PromptNames.Pdf },
        };
    }

    // The record keeps "name (modified)" when the conditions were changed after the preset was chosen: then there is no preset.
    private static string PresetName(string? recorded) =>
        string.IsNullOrEmpty(recorded) || recorded.EndsWith(" (modified)", StringComparison.Ordinal) ? string.Empty : recorded;

    /// <summary>
    /// Puts the conditions into the settings; the inputs, the output folder and everything else stay. A connection is chosen only when the
    /// settings have one of that name, and a prompt only when the kind has one of that name (else the standard prompt). The preset is kept
    /// only when the conditions are still those of the preset.
    /// </summary>
    /// <returns>False when the connection of the conditions is not in the settings (the one chosen now stays).</returns>
    public bool ApplyTo(AppSettings settings)
    {
        settings.Input.Types = [.. Types];
        settings.Input.Recurse = Recurse;
        settings.Input.Extractors = Extractors.Clone();
        settings.Engine.Kind = Engine;
        settings.Images.Dpi = Dpi;
        settings.Images.ExcelRange = ExcelRange;
        settings.Input.IncludeHiddenSheets = IncludeHiddenSheets;
        settings.Input.IncludeHiddenSlides = IncludeHiddenSlides;
        settings.Input.IncludeOffSlideText = IncludeOffSlideText;
        settings.Input.Emphasis = Emphasis;
        settings.Input.UsePdfText = UsePdfText;
        settings.Input.ExtractPictures = ExtractPictures;
        settings.Input.ExtractShapes = ExtractShapes;
        settings.Input.GroupNearShapes = GroupNearShapes;
        settings.Input.ShapeGroupGapPoints = ShapeGroupGapPoints;
        settings.Steps.Step2 = Step2;
        settings.Steps.PageImage = PageImage;
        settings.Steps.Step3 = Step3;
        settings.Steps.Step3Mode = Step3Mode;
        settings.Steps.VlmSeparate = VlmSeparate;
        settings.Steps.Step1Tables = Step1Tables;
        settings.Steps.DropRepeatedTables = DropRepeatedTables;
        settings.Output.StructureNote = StructureNote;
        settings.Output.Chunks = Chunks;
        settings.Output.ExcelPerSheet = ExcelPerSheet;
        settings.Output.FolderLayout = FolderLayout;
        foreach (var kind in Kinds)
        {
            var variants = settings.Vlm.Prompts.VariantsOf(kind);
            variants.Selected = variants.Find(PromptNames.For(kind)) is { } found ? found.Name : string.Empty;
        }

        var connectionFound = true;
        if (Connection.Length > 0)
        {
            if (settings.FindConnection(Connection) is { } connection)
            {
                settings.Vlm.Selected = connection.Name;
            }
            else
            {
                connectionFound = false;
            }
        }

        settings.PresetSelected = ConversionPreset.Find(settings, Preset) is { } preset && preset.Matches(settings) ? preset.Name : string.Empty;
        return connectionFound;
    }

    /// <summary>A copy.</summary>
    public ConditionSet Clone() => JsonDefaults.Deserialize<ConditionSet>(JsonDefaults.Serialize(this))!;

    /// <summary>The same conditions (the preset is only how they were chosen, so it is not compared).</summary>
    public bool SameAs(ConditionSet other) => Differences(other).Count == 0;

    /// <summary>
    /// The items whose values differ from <paramref name="other"/>, each with the value of this one (for a message: "DPI 200", "Step3 off").
    /// </summary>
    public IReadOnlyList<string> Differences(ConditionSet other)
    {
        var list = new List<string>();
        void Add(bool differs, string text)
        {
            if (differs)
            {
                list.Add(text);
            }
        }

        string OnOff(bool value) => value ? Loc.T("on") : Loc.T("off");
        Add(!Types.OrderBy(t => t).SequenceEqual(other.Types.OrderBy(t => t)), Loc.T("Kinds {0}", string.Join("/", Types.OrderBy(t => t).Select(t => t.ToString().ToLowerInvariant()))));
        Add(Recurse != other.Recurse, Loc.T("Subfolders {0}", OnOff(Recurse)));
        Add(!SameExtractors(Extractors, other.Extractors), Loc.T("Extraction"));
        Add(Engine != other.Engine, Loc.T("Engine {0}", Engine));
        Add(Dpi != other.Dpi, "DPI " + Dpi.ToString(CultureInfo.InvariantCulture));
        Add(ExcelRange != other.ExcelRange, Loc.T("Excel range {0}", ExcelRange));
        Add(
            IncludeHiddenSheets != other.IncludeHiddenSheets || IncludeHiddenSlides != other.IncludeHiddenSlides || IncludeOffSlideText != other.IncludeOffSlideText
            || Emphasis != other.Emphasis || UsePdfText != other.UsePdfText || ExtractPictures != other.ExtractPictures || ExtractShapes != other.ExtractShapes
            || GroupNearShapes != other.GroupNearShapes || ShapeGroupGapPoints != other.ShapeGroupGapPoints,
            Loc.T("Options of Step1"));
        Add(Step2 != other.Step2, "Step2 " + OnOff(Step2));
        Add(Step2 && other.Step2 && PageImage != other.PageImage, Loc.T("Page image {0}", PageImage));
        Add(Step3 != other.Step3, "Step3 " + OnOff(Step3));
        Add(Step3 && other.Step3 && Step3Mode != other.Step3Mode, Loc.T("Use of Step3 {0}", Step3Mode));
        Add(Step3 && other.Step3 && !string.Equals(Connection, other.Connection, StringComparison.OrdinalIgnoreCase), Loc.T("Connection {0}", Connection));
        Add(Step3 && other.Step3 && !SamePrompts(PromptNames, other.PromptNames), Loc.T("Prompts"));
        Add(VlmSeparate != other.VlmSeparate || Step1Tables != other.Step1Tables || DropRepeatedTables != other.DropRepeatedTables, Loc.T("Writing of the VLM results"));
        Add(StructureNote != other.StructureNote, Loc.T("Structure note {0}", OnOff(StructureNote)));
        Add(Chunks != other.Chunks, Loc.T("Chunks {0}", OnOff(Chunks)));
        Add(ExcelPerSheet != other.ExcelPerSheet, Loc.T("Excel per sheet {0}", OnOff(ExcelPerSheet)));
        Add(FolderLayout != other.FolderLayout, Loc.T("Folder layout {0}", FolderLayout));
        return list;
    }

    private static bool SameExtractors(ExtractorChoice a, ExtractorChoice b) =>
        Kinds.All(k => string.Equals(a.For(k), b.For(k), StringComparison.OrdinalIgnoreCase));

    private static bool SamePrompts(PromptNames a, PromptNames b) =>
        Kinds.All(k => string.Equals(a.For(k), b.For(k), StringComparison.OrdinalIgnoreCase));
}
