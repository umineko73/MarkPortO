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

using MarkPortO.Cli;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using UglyToad.PdfPig.Content;

namespace MarkPortO.Cli.Tests;

/// <summary>FR-151 on the command line: <c>--preset</c> and <c>--structure-note</c>, through a whole conversion of a PDF (no Office is needed).</summary>
public class PresetCliTests
{
    [Fact]
    public async Task TheRagPreset_WritesThePageImagesAndTheDescription_AndSaysItsName()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");

        var run = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--preset", "rag", "--settings", Settings(dir));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var markdown = File.ReadAllText(dir.Combine("out", "a", "a.md"));
        Assert.Contains("; preset: rag -->", markdown, StringComparison.Ordinal);
        Assert.Contains(Loc.M("**About this Markdown**", "ja"), markdown, StringComparison.Ordinal);
        Assert.Contains("<!-- page-image: images/page-001.png -->", markdown, StringComparison.Ordinal);
        Assert.True(File.Exists(dir.Combine("out", "a", "images", "page-001.png")));
        Assert.Contains("Alpha text", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRagPreset_WritesTheChunks_AndTheResultFileNamesThem()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");

        var run = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--preset", "rag", "--result", dir.Combine("result.json"), "--settings", Settings(dir));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var lines = File.ReadAllLines(dir.Combine("out", "a", "a_chunks.jsonl"));
        using var chunk = System.Text.Json.JsonDocument.Parse(Assert.Single(lines));
        Assert.Equal("a.pdf", chunk.RootElement.GetProperty("source").GetString());
        Assert.Equal("images/page-001.png", chunk.RootElement.GetProperty("pageImages")[0].GetString());
        Assert.Contains("Alpha text", chunk.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        using var result = System.Text.Json.JsonDocument.Parse(File.ReadAllText(dir.Combine("result.json")));
        Assert.Equal("a_chunks.jsonl", result.RootElement.GetProperty("documents")[0].GetProperty("chunks").GetString());
    }

    [Fact]
    public async Task TheChunksOption_TurnsThemOffOverThePreset_AndAWrongValueIsAnErrorOfTheArguments()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");

        var run = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--preset", "rag", "--chunks", "off", "--settings", Settings(dir));
        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.False(File.Exists(dir.Combine("out", "a", "a_chunks.jsonl")));

        var wrong = await Cli.RunAsync("convert", input, "--out", dir.Combine("out2"), "--chunks", "maybe", "--settings", Settings(dir));
        Assert.Equal(ExitCodes.InvalidArguments, wrong.ExitCode);
        Assert.Contains("--chunks", wrong.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHumanPreset_LeavesOutThePageImagesAndTheDescription()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");

        var run = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--preset", "HUMAN", "--settings", Settings(dir));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var markdown = File.ReadAllText(dir.Combine("out", "a", "a.md"));
        Assert.Contains("; preset: human -->", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.M("**About this Markdown**", "ja"), markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("page-image", markdown, StringComparison.Ordinal);
        Assert.StartsWith("# a.pdf\n", markdown, StringComparison.Ordinal);
        Assert.Contains("Alpha text", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOptionGivenWithThePreset_WinsOverIt_AndTheMarkdownSaysItWasChanged()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");

        var run = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--preset", "human", "--step2", "on", "--structure-note", "on", "--settings", Settings(dir));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var markdown = File.ReadAllText(dir.Combine("out", "a", "a.md"));
        Assert.Contains("; preset: human (modified) -->", markdown, StringComparison.Ordinal);
        Assert.Contains(Loc.M("**About this Markdown**", "ja"), markdown, StringComparison.Ordinal);
        Assert.Contains("<!-- page-image: images/page-001.png -->", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APresetOfTheUser_IsUsedByItsName()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");
        var settings = Settings(dir, s =>
        {
            var mine = ConversionPreset.From(s, "社内向け");
            mine.Step2 = true;
            mine.PageImage = PageImageStyle.Link;
            mine.StructureNote = false;
            s.Presets.Add(mine);
        });

        var run = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--preset", "社内向け", "--settings", settings);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var markdown = File.ReadAllText(dir.Combine("out", "a", "a.md"));
        Assert.Contains("; preset: 社内向け -->", markdown, StringComparison.Ordinal);
        Assert.Contains("](images/page-001.png)", markdown, StringComparison.Ordinal); // a link
        Assert.DoesNotContain(Loc.M("**About this Markdown**", "ja"), markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAPreset_TheMarkdownSaysNone()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");

        var run = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--step2", "off", "--settings", Settings(dir));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var markdown = File.ReadAllText(dir.Combine("out", "a", "a.md"));
        Assert.DoesNotContain("preset:", markdown, StringComparison.Ordinal);
        Assert.Contains(Loc.M("**About this Markdown**", "ja"), markdown, StringComparison.Ordinal); // the default writes it
    }

    [Fact]
    public async Task AnUnknownPreset_OrAWrongStructureNote_IsAnErrorOfTheArguments()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");

        var unknown = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--preset", "nothing", "--settings", Settings(dir));
        Assert.Equal(ExitCodes.InvalidArguments, unknown.ExitCode);
        Assert.Contains("--preset", unknown.Error, StringComparison.Ordinal);
        Assert.Contains("nothing", unknown.Error, StringComparison.Ordinal);

        var wrong = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--structure-note", "maybe", "--settings", Settings(dir));
        Assert.Equal(ExitCodes.InvalidArguments, wrong.ExitCode);
        Assert.Contains("--structure-note", wrong.Error, StringComparison.Ordinal);

        Assert.False(Directory.Exists(dir.Combine("out", "a")));
    }

    [Fact]
    public async Task ThePresetOfTheSettingsFile_IsNotChangedByTheCommandLine()
    {
        using var dir = new TempDir();
        var input = PdfFactory.Create(dir.Combine("in", "a.pdf"), PageSize.A4, "Alpha text");
        var settings = Settings(dir, s => s.PresetSelected = "human");
        var before = File.ReadAllText(settings);

        var run = await Cli.RunAsync("convert", input, "--out", dir.Combine("out"), "--preset", "rag", "--settings", settings);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(before, File.ReadAllText(settings));
    }

    /// <summary>A settings file of the test: Japanese, no VLM connection (the presets then leave Step3 off), DPI 72 only where a test changes it.</summary>
    private static string Settings(TempDir dir, Action<AppSettings>? change = null)
    {
        var settings = AppSettings.CreateDefault();
        settings.Ui.Language = "ja";
        settings.Engine.Kind = EngineKind.LibreOffice;
        settings.Steps.Step3 = false;
        change?.Invoke(settings);
        var path = dir.Combine("settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonDefaults.Serialize(settings));
        return path;
    }
}
