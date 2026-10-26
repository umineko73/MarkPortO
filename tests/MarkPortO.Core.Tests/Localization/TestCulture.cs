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
using System.Runtime.CompilerServices;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Tests.Localization;

/// <summary>
/// The messages of the tests are English, whatever the language of the computer that runs them; a test that checks
/// another language says so with <see cref="Use"/>.
/// </summary>
public static class TestCulture
{
    [ModuleInitializer]
    public static void Initialize() => LanguageResolver.Apply(LanguageResolver.English);

    /// <summary>Uses a language for the messages of the current test, and puts English back when disposed.</summary>
    public static IDisposable Use(string language)
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        return new Restore(before);
    }

    private sealed class Restore(CultureInfo before) : IDisposable
    {
        public void Dispose() => CultureInfo.CurrentUICulture = before;
    }
}
