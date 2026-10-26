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

using System.Windows;
#pragma warning disable WPF0001 // Application.ThemeMode is experimental; the warning is allowed in this project only (research.md R15).
using Microsoft.Win32;
using System.Windows.Media;
using MarkPortO.Core.Settings;

namespace MarkPortO.App.Services;

/// <summary>
/// The theme of the screen (FR-035). The Fluent theme of WPF (<see cref="Application.ThemeMode"/>) gives the colours and the
/// shapes of the controls; <c>Resources/Compact.xaml</c> is merged after it and brings the sizes back to the classic ones, so that
/// the same information needs no more room. The colours that the XAML names (a failed row, a note) are set here for the theme in use.
/// </summary>
public static class ThemeService
{
    private const string CompactUri = "/MarkPortO;component/Resources/Compact.xaml";

    private static ResourceDictionary? _compact;
    private static string _setting = UiSettings.SystemTheme;
    private static bool _watching;

    /// <summary>True while the dark theme is in use.</summary>
    public static bool IsDark { get; private set; }

    /// <param name="setting">The <c>ui.theme</c> setting: <c>system</c>, <c>light</c> or <c>dark</c>.</param>
    public static void Apply(string setting)
    {
        _setting = setting;
        var app = Application.Current;
        IsDark = setting switch
        {
            UiSettings.DarkTheme => true,
            UiSettings.LightTheme => false,
            _ => OsUsesDarkTheme(),
        };

        // "system" is handed to WPF, which follows the OS by itself; the colours below follow it through UserPreferenceChanged.
        app.ThemeMode = setting switch
        {
            UiSettings.DarkTheme => ThemeMode.Dark,
            UiSettings.LightTheme => ThemeMode.Light,
            _ => ThemeMode.System,
        };

        // The compact sizes must come after the theme's dictionaries, which are added when the theme mode is set.
        var merged = app.Resources.MergedDictionaries;
        if (_compact is not null)
        {
            merged.Remove(_compact);
        }

        _compact = new ResourceDictionary { Source = new Uri(CompactUri, UriKind.Relative) };
        merged.Add(_compact);

        var resources = app.Resources;
        resources["FailedBrush"] = Frozen(IsDark ? Color.FromRgb(0xFF, 0x6B, 0x6B) : Colors.Firebrick);
        resources["PartlyFailedBrush"] = Frozen(IsDark ? Color.FromRgb(0xFF, 0xB3, 0x47) : Colors.DarkOrange);
        resources["SubtleTextBrush"] = Frozen(IsDark ? Color.FromRgb(0xA0, 0xA0, 0xA0) : Colors.Gray);

        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_setting == UiSettings.SystemTheme && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Application.Current.Dispatcher.BeginInvoke(() => Apply(UiSettings.SystemTheme));
        }
    }

    private static bool OsUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
