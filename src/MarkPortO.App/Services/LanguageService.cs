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
using MarkPortO.Core.Localization;

namespace MarkPortO.App.Services;

/// <summary>
/// Switches the language of the screen at once, without a restart (FR-033): the dictionary of texts that the XAML refers to
/// with <c>DynamicResource</c> is replaced, and the language of the messages of the Core is set.
/// </summary>
public static class LanguageService
{
    private static ResourceDictionary? _dictionary;

    /// <summary>The language in use: <c>ja</c> or <c>en</c>.</summary>
    public static string Current { get; private set; } = LanguageResolver.English;

    /// <summary>Raised after the language was changed, so that texts the code made can be made again.</summary>
    public static event Action? Changed;

    /// <param name="setting"><c>system</c>, <c>ja</c> or <c>en</c> (the <c>ui.language</c> setting).</param>
    public static void Apply(string setting)
    {
        var language = LanguageResolver.Resolve(setting);
        LanguageResolver.Apply(language);

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"/MarkPortO;component/Resources/Strings.{language}.xaml", UriKind.Relative),
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_dictionary is not null)
        {
            merged.Remove(_dictionary);
        }

        merged.Add(dictionary);
        _dictionary = dictionary;
        Current = language;
        Changed?.Invoke();
    }
}
