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

using MarkPort.Cli;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Tests.Localization;

namespace MarkPort.Cli.Tests;

/// <summary>FR-037: the messages of the command line are English; the language of the Markdown comes from ui.language.</summary>
public class LanguageCliTests
{
    private static string Settings(TempDir dir, string language, string name = "settings.json")
    {
        var path = dir.Combine(name);
        File.WriteAllText(path, $$"""{ "engine": { "kind": "libreoffice" }, "ui": { "language": "{{language}}" } }""");
        return path;
    }

    [Fact]
    public async Task TheMessages_AreEnglish_EvenWhenTheComputerIsJapanese()
    {
        using var dir = new TempDir();
        using var japanese = TestCulture.Use("ja");

        // No output folder: the message names it.
        var run = await Cli.RunAsync("convert", dir.Combine("a.docx"), "--settings", Settings(dir, "ja"));

        Assert.NotEqual(ExitCodes.Success, run.ExitCode);
        var text = run.Output + run.Error;
        Assert.DoesNotMatch("[ぁ-んァ-ン一-龥]", text.Replace(dir.Path, string.Empty, StringComparison.Ordinal));
        Assert.Contains("output folder", text, StringComparison.OrdinalIgnoreCase);
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task TheMarkdown_FollowsUiLanguageOfTheSettings_NotTheLanguageOfTheMessages()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);

        var english = await Cli.RunAsync("convert", source, "--settings", Settings(dir, "en"), "--out", dir.Combine("out-en"));
        var japanese = await Cli.RunAsync("convert", source, "--settings", Settings(dir, "ja", "ja.json"), "--out", dir.Combine("out-ja"));

        Assert.Equal(ExitCodes.Success, english.ExitCode);
        Assert.Equal(ExitCodes.Success, japanese.ExitCode);
        Assert.StartsWith("# a.docx\n\n> **About this Markdown**", File.ReadAllText(dir.Combine("out-en", "a", "a.md")).Replace("\r\n", "\n"));
        Assert.StartsWith("# a.docx\n\n> **このMarkdownの構造**", File.ReadAllText(dir.Combine("out-ja", "a", "a.md")).Replace("\r\n", "\n"));
        // The progress lines are English in both runs.
        Assert.Contains("step1  done", japanese.Output);
    }
}
