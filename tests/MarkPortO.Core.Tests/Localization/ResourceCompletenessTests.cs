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

using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Tests.Localization;

/// <summary>FR-034, SC-020: every text the code asks for has a translation, and no translation is left without a use.</summary>
public partial class ResourceCompletenessTests
{
    // Loc.T("...") or Loc.M("..."): the first argument is the text.
    [GeneratedRegex(@"Loc\.(?<kind>[TM])\(\s*""(?<key>(?:[^""\\]|\\.)*)""")]
    private static partial Regex LocCall();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarkPortO.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MarkPortO.slnx was not found above the test folder.");
    }

    private static string Unescape(string literal) =>
        literal.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);

    /// <summary>The texts the code asks for, by the resource they come from.</summary>
    private static (HashSet<string> Messages, HashSet<string> Markdown) KeysInTheCode()
    {
        var messages = new HashSet<string>(StringComparer.Ordinal);
        var markdown = new HashSet<string>(StringComparer.Ordinal);
        var core = Path.Combine(RepositoryRoot(), "src", "MarkPortO.Core");
        var separator = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}")))
        {
            foreach (Match match in LocCall().Matches(File.ReadAllText(file)))
            {
                (match.Groups["kind"].Value == "T" ? messages : markdown).Add(Unescape(match.Groups["key"].Value));
            }
        }

        return (messages, markdown);
    }

    private static Dictionary<string, string> Translations(ResourceManager resources, string language)
    {
        var set = resources.GetResourceSet(CultureInfo.GetCultureInfo(language), createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No {language} resources.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
    }

    [Fact]
    public void EveryMessageInTheCode_HasAJapaneseTranslation()
    {
        var (messages, _) = KeysInTheCode();
        var translated = Translations(Loc.MessageResources, "ja");

        Assert.True(messages.Count > 60, "the test found the messages of the code");
        var missing = messages.Where(key => !translated.ContainsKey(key)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryMarkdownTextInTheCode_HasAJapaneseTranslation()
    {
        var (_, markdown) = KeysInTheCode();
        var translated = Translations(Loc.MarkdownResources, "ja");

        Assert.True(markdown.Count > 10, "the test found the Markdown texts of the code");
        var missing = markdown.Where(key => !translated.ContainsKey(key)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void NoJapaneseTranslation_IsLeftWithoutUse()
    {
        var (messages, markdown) = KeysInTheCode();

        // The page labels are written in PageLabel, from a code, with the same texts.
        var pageLabels = new[] { "Page {0}", "Slide {0}", "Sheet \"{0}\"" };
        var unusedMessages = Translations(Loc.MessageResources, "ja").Keys.Where(key => !messages.Contains(key)).ToList();
        var unusedMarkdown = Translations(Loc.MarkdownResources, "ja").Keys.Where(key => !markdown.Contains(key) && !pageLabels.Contains(key)).ToList();
        Assert.Empty(unusedMessages);
        Assert.Empty(unusedMarkdown);
    }

    [Fact]
    public void ATranslation_UsesTheSamePlaceholdersAsTheEnglishText()
    {
        var problems = new List<string>();
        foreach (var resources in new[] { Loc.MessageResources, Loc.MarkdownResources })
        {
            foreach (var (key, japanese) in Translations(resources, "ja"))
            {
                if (!Placeholders(Loc.EnglishOf(key)).SetEquals(Placeholders(japanese)))
                {
                    problems.Add(key);
                }
            }
        }

        Assert.Empty(problems);
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{(\d+)(?::[^}]*)?\}").Select(m => m.Groups[1].Value)];
}
