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

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MarkPortO.Core.Tests.Localization;

/// <summary>
/// FR-034, SC-020: the dictionaries of the texts of the screen (Strings.ja.xaml, Strings.en.xaml) have the same keys, every
/// key that the XAML and the code use is in them, and no text is written into the XAML or the code of the screen directly.
/// </summary>
public partial class UiStringsTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [GeneratedRegex(@"\{DynamicResource ([A-Za-z0-9_]+)\}")]
    private static partial Regex DynamicResourceUse();

    [GeneratedRegex(@"UiText\.Get\(\s*""([A-Za-z0-9_]+)""")]
    private static partial Regex CodeUse();

    private static string AppFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarkPortO.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("MarkPortO.slnx was not found."), "src", "MarkPortO.App");
    }

    private static Dictionary<string, string> Dictionary(string language) =>
        XDocument.Load(Path.Combine(AppFolder(), "Resources", $"Strings.{language}.xaml")).Root!.Elements()
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value, StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles(params string[] extensions)
    {
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(AppFolder(), "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f)) && !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}")
                        && !f.Contains($"{separator}Resources{separator}"));
    }

    [Fact]
    public void BothLanguages_HaveTheSameKeys_AndNoEmptyText()
    {
        var japanese = Dictionary("ja");
        var english = Dictionary("en");

        Assert.True(japanese.Count > 80, "the dictionary was read");
        var onlyJapanese = japanese.Keys.Except(english.Keys).ToList();
        var onlyEnglish = english.Keys.Except(japanese.Keys).ToList();
        var empty = japanese.Concat(english).Where(p => p.Value.Length == 0).Select(p => p.Key).ToList();
        Assert.Empty(onlyJapanese);
        Assert.Empty(onlyEnglish);
        Assert.Empty(empty);
    }

    [Fact]
    public void ATranslation_UsesTheSamePlaceholdersAsTheOtherLanguage()
    {
        var japanese = Dictionary("ja");
        var english = Dictionary("en");
        var problems = japanese.Keys.Where(key => !Placeholders(japanese[key]).SetEquals(Placeholders(english[key]))).ToList();

        Assert.Empty(problems);
    }

    [Fact]
    public void EveryKeyTheScreenUses_IsInTheDictionary_AndEveryKeyIsUsed()
    {
        var keys = Dictionary("ja").Keys.ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(".xaml"))
        {
            foreach (Match match in DynamicResourceUse().Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        foreach (var file in SourceFiles(".cs"))
        {
            foreach (Match match in CodeUse().Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        // Colours that the theme service sets are resources too, but not texts: they are not in the dictionaries.
        used.ExceptWith(["FailedBrush", "PartlyFailedBrush", "SubtleTextBrush"]);
        var missing = used.Except(keys).ToList();
        var unused = keys.Except(used).ToList();
        Assert.Empty(missing);
        Assert.Empty(unused);
    }

    [Fact]
    public void NoTextIsWrittenIntoTheXamlOfTheScreen()
    {
        // A text attribute with letters or Japanese in it is a text that is not translated. The names of the file types and of
        // the steps (docx, Step1 ...) and the name of the program are the same in every language.
        var allowed = new HashSet<string>(["docx", "xlsx", "pptx", "pdf", "Step1", "Step2", "Step3", "MarkPortO", "URL", "Word (docx)", "Excel (xlsx)", "PowerPoint (pptx)", "PDF (pdf)"], StringComparer.Ordinal);
        var found = new List<string>();
        foreach (var file in SourceFiles(".xaml"))
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "Text" or "Content" or "Header" or "Title" or "ToolTip"))
                {
                    var value = attribute.Value;
                    if (value.StartsWith('{') || value.Length == 0 || allowed.Contains(value))
                    {
                        continue;
                    }

                    if (Regex.IsMatch(value, @"[A-Za-z぀-ヿ一-龥]"))
                    {
                        found.Add($"{Path.GetFileName(file)}: {attribute.Name.LocalName}=\"{value}\"");
                    }
                }
            }
        }

        Assert.Empty(found);
    }

    private static HashSet<string> Placeholders(string text) =>
        [.. Regex.Matches(text, @"\{(\d+)(?::[^}]*)?\}").Select(m => m.Groups[1].Value)];
}
