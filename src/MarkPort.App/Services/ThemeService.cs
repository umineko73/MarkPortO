// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.Windows;
using System.Windows.Media;

namespace MarkPort.App.Services;

/// <summary>
/// The theme of the screen. The dark theme (FR-035) is on hold: the Fluent theme of WPF made the controls large and changed the
/// look of the screen too much, so the screen uses the classic theme, and only the light colours that the XAML names (a failed
/// row, a note) are set here. The <c>ui.theme</c> setting is kept in the settings file and is not used yet.
/// </summary>
public static class ThemeService
{
    /// <param name="setting">The <c>ui.theme</c> setting. Not used while the dark theme is on hold.</param>
    public static void Apply(string setting)
    {
        var resources = Application.Current.Resources;
        resources["FailedBrush"] = Frozen(Colors.Firebrick);
        resources["PartlyFailedBrush"] = Frozen(Colors.DarkOrange);
        resources["SubtleTextBrush"] = Frozen(Colors.Gray);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
