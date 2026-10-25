// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
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
using System.Resources;

namespace MarkPort.Core.Localization;

/// <summary>
/// The texts a person reads, in the language of the moment (FR-034). The English text is the key and the default:
/// <c>Loc.T("The file was not found: {0}", path)</c> gives that text in English, and the translation from
/// <c>Resources/Messages.&lt;language&gt;.resx</c> when the culture has one. A new language is a new resource file.
/// </summary>
/// <remarks>
/// A key may start with a context and a bar (<c>alt|Page {0}</c>) when the same English text needs different
/// translations. The context is dropped when the English text is used.
/// </remarks>
public static class Loc
{
    public static ResourceManager MessageResources { get; } = new("MarkPort.Core.Resources.Messages", typeof(Loc).Assembly);

    public static ResourceManager MarkdownResources { get; } = new("MarkPort.Core.Resources.Markdown", typeof(Loc).Assembly);

    /// <summary>A message for the screen or the command line, in the language of the current thread (<see cref="CultureInfo.CurrentUICulture"/>).</summary>
    public static string T(string key, params object?[] args) =>
        Format(Lookup(MessageResources, key, CultureInfo.CurrentUICulture), args);

    /// <summary>A text of the output Markdown, in the language that was fixed when the job started (FR-036).</summary>
    public static string M(string key, string language, params object?[] args) =>
        Format(Lookup(MarkdownResources, key, CultureInfo.GetCultureInfo(language)), args);

    /// <summary>A text of the output Markdown, in a culture.</summary>
    public static string M(string key, CultureInfo culture, params object?[] args) =>
        Format(Lookup(MarkdownResources, key, culture), args);

    /// <summary>The key without its context: the English text.</summary>
    public static string EnglishOf(string key)
    {
        var bar = key.IndexOf('|', StringComparison.Ordinal);
        return bar > 0 && bar < 16 && !key.AsSpan(0, bar).Contains(' ') ? key[(bar + 1)..] : key;
    }

    private static string Lookup(ResourceManager resources, string key, CultureInfo culture)
    {
        string? text = null;
        try
        {
            text = resources.GetString(key, culture);
        }
        catch (MissingManifestResourceException)
        {
            // No resources for this culture: the English text is used.
        }

        return text is { Length: > 0 } ? text : EnglishOf(key);
    }

    private static string Format(string text, object?[] args) =>
        args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
}
