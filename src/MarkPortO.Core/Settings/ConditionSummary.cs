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
/// The conditions of the folded part of the main window (the kinds of file with the extractor that is chosen for each, the sub-folders, the engine, the DPI, bold and italic) in one line (FR-102).
/// It is shown when the part is closed, so that the choices can be seen without opening it. The steps, the connection and the page range are
/// not in it: they are always on the screen.
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

        parts.Add(settings.Engine.Kind == EngineKind.Office ? "Microsoft Office" : "LibreOffice");
        parts.Add($"{settings.Images.Dpi} DPI");

        if (!settings.Input.Emphasis)
        {
            parts.Add(Loc.T("No bold or italic"));
        }

        return string.Join(Separator, parts);
    }
}
