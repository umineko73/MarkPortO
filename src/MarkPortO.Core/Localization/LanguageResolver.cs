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

namespace MarkPortO.Core.Localization;

/// <summary>
/// Decides the language to use from the <c>ui.language</c> setting (FR-033): <c>ja</c> and <c>en</c> are used as they are;
/// <c>system</c> follows the language of the operating system, and any language the app has no translation for is English.
/// </summary>
public static class LanguageResolver
{
    // An explicit static constructor makes the first use of any member run the initializers first, so the operating
    // system's language is read before Apply changes the culture.
    static LanguageResolver()
    {
    }

    public const string System = "system";
    public const string Japanese = "ja";
    public const string English = "en";

    /// <summary>The languages the app has translations for. A new language needs its resource files; no code changes.</summary>
    public static readonly IReadOnlyList<string> Supported = [Japanese, English];

    /// <summary>
    /// The display language of the operating system, read once at the start of the process, before the app changes
    /// the culture of its own threads.
    /// </summary>
    public static CultureInfo OperatingSystemCulture { get; } = CultureInfo.CurrentUICulture;

    /// <param name="setting"><c>system</c>, <c>ja</c> or <c>en</c> (anything else is taken as <c>system</c>).</param>
    /// <param name="operatingSystem">The OS language; the one read at the start of the process when not given.</param>
    /// <returns>The language code to use: <c>ja</c> or <c>en</c>.</returns>
    public static string Resolve(string? setting, CultureInfo? operatingSystem = null)
    {
        if (setting is Japanese or English)
        {
            return setting;
        }

        var language = (operatingSystem ?? OperatingSystemCulture).TwoLetterISOLanguageName;
        return Supported.Contains(language, StringComparer.OrdinalIgnoreCase) ? language.ToLowerInvariant() : English;
    }

    /// <summary>Makes the messages of this process, on every thread, use the language (for the screen and the command line).</summary>
    public static void Apply(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
