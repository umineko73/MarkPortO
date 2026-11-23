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
/// The conditions of the folded part of the main window (the kinds of file with the extractor that is chosen for each, the sub-folders, and the options that are not the usual ones) in one line (FR-102).
/// It is shown when the part is closed, so that the choices can be seen without opening it. The steps, the connection and the page range are
/// not in it: they are always on the screen, and so are the engine, the DPI, bold and italic, and the pictures (they are in the row of Step1).
/// </summary>
public static class ConditionSummary
{
    public const string Separator = " | ";

    /// <summary>The line, in the language of the thread (<see cref="System.Globalization.CultureInfo.CurrentUICulture"/>).</summary>
    public static string Build(AppSettings settings)
    {
        var parts = new List<string>();

        // A kind that an extractor writes (FR-111) shows its name: docx(pandoc).
        var types = settings.Input.Types.Distinct().OrderBy(t => t).Select(t =>
        {
            var name = t.ToString().ToLowerInvariant();
            var extractor = settings.Input.Extractors.For(t);
            return extractor.Length > 0 ? $"{name}({extractor})" : name;
        }).ToList();
        parts.Add(types.Count > 0 ? string.Join(' ', types) : Loc.T("No kind of file"));

        if (settings.Input.Recurse)
        {
            parts.Add(Loc.T("Sub-folders"));
        }

        // The options of the folded part that are not the usual ones (the usual ones are not written, so the line stays short).
        var kinds = settings.Input.Types;
        if (kinds.Contains(DocumentKind.Xlsx) && settings.Images.ExcelRange != ExcelRange.Columns)
        {
            parts.Add(settings.Images.ExcelRange == ExcelRange.Sheet ? Loc.T("Excel: whole sheet on one page") : Loc.T("Excel: print area"));
        }

        if (kinds.Contains(DocumentKind.Xlsx) && settings.Input.IncludeHiddenSheets)
        {
            parts.Add(Loc.T("Hidden sheets"));
        }

        if (kinds.Contains(DocumentKind.Pptx) && settings.Input.IncludeHiddenSlides)
        {
            parts.Add(Loc.T("Hidden slides"));
        }

        if (kinds.Contains(DocumentKind.Pptx) && !settings.Input.IncludeOffSlideText)
        {
            parts.Add(Loc.T("No off-slide text"));
        }

        if (kinds.Contains(DocumentKind.Pdf) && !settings.Input.UsePdfText)
        {
            parts.Add(Loc.T("No PDF text"));
        }

        // The details of the steps, which are in the folded part too: the engine and the DPI always, the others when they are not the usual ones.
        parts.Add((settings.Engine.Kind == EngineKind.Office ? "Office" : "LibreOffice") + " " + settings.Images.Dpi.ToString(System.Globalization.CultureInfo.InvariantCulture) + "dpi");
        if (!settings.Input.Emphasis)
        {
            parts.Add(Loc.T("No bold and italic"));
        }

        if (!settings.Output.StructureNote)
        {
            parts.Add(Loc.T("No description at the top"));
        }

        if (settings.Output.Chunks)
        {
            parts.Add(Loc.T("Chunks for RAG"));
        }

        if (settings.Steps.Step2 && settings.Steps.PageImage != PageImageStyle.Reference)
        {
            parts.Add(settings.Steps.PageImage == PageImageStyle.Image ? Loc.T("Page images: image") : Loc.T("Page images: link"));
        }

        if (settings.Steps.Step3)
        {
            if (settings.Steps.VlmSeparate)
            {
                parts.Add(Loc.T("VLM results in a separate file"));
            }

            if (settings.Steps.Step1Tables != Step1TablesMode.Off)
            {
                parts.Add(settings.Steps.Step1Tables == Step1TablesMode.Replace ? Loc.T("Tables: the extracted text") : Loc.T("Tables: not in the VLM result"));
            }

            if (settings.Steps.DropRepeatedTables)
            {
                parts.Add(Loc.T("No repeated tables"));
            }
        }

        return string.Join(Separator, parts);
    }
}
