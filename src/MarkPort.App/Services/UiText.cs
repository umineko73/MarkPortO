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

using System.Globalization;
using System.Windows;

namespace MarkPort.App.Services;

/// <summary>
/// The texts of the screen that the code needs (messages, dialog titles), taken from the dictionary of the language that is
/// in use (<c>Resources/Strings.&lt;language&gt;.xaml</c>). The XAML uses the same keys with <c>DynamicResource</c>.
/// </summary>
public static class UiText
{
    public static string Get(string key, params object?[] args)
    {
        var text = Application.Current?.TryFindResource(key) as string ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }
}
