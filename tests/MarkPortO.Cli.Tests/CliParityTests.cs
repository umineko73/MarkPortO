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

using System.Security.Cryptography;
using MarkPortO.Core.Convert;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Cli.Tests;

/// <summary>SC-010: the same input and settings give the same output from the command line and from the Core (what the screen uses).</summary>
[Trait("Engine", "LibreOffice")]
public class CliParityTests
{
    private static Dictionary<string, string> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".markporto", StringComparison.Ordinal))
            .ToDictionary(
                f => Path.GetRelativePath(root, f).Replace('\\', '/'),
                f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [LibreOfficeFact]
    public async Task TheCommandLineAndTheCore_ProduceTheSameFilesWithTheSameContent()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("in", "mixed");
        DocxFactory.CreatePaged(Path.Combine(folder, "a.docx"), ["first page"], ["second page"]);
        PptxFactory.Create(Path.Combine(folder, "b.pptx"), new SlideSpec("Slide", ["point"]));
        XlsxFactory.Create(Path.Combine(folder, "c.xlsx"), new SheetSpec("S", [["h1", "h2"], ["v1", "v2"]]));

        // The command line
        var run = await Cli.RunAsync("convert", folder, "--out", dir.Combine("cli-out"), "--engine", "libreoffice", "--dpi", "72");
        Assert.Equal(ExitCodes.Success, run.ExitCode);

        // The Core, called the way the screen calls it
        var found = InputEnumerator.Enumerate([folder], [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx], recurse: false);
        using var engine = new LibreOfficeEngine(profileRoot: dir.Combine("profiles"));
        var core = await new ConvertService(engine).RunAsync(new ConvertRequest
        {
            Inputs = found.Inputs,
            GivenInputs = [folder],
            InputFolder = found.InputFolder,
            OutputFolder = dir.Combine("core-out"),
            Options = new OptionsSnapshot
            {
                Types = [DocumentKind.Docx, DocumentKind.Xlsx, DocumentKind.Pptx],
                Dpi = 72,
                Engine = EngineKind.LibreOffice,
                Step2 = true,
                MarkdownLanguage = "ja", // the settings file the command line uses says ja
            },
        });
        Assert.Equal(JobStatus.Completed, core.Job.Status);

        var cliTree = Tree(dir.Combine("cli-out", "mixed"));
        var coreTree = Tree(dir.Combine("core-out", "mixed"));
        Assert.NotEmpty(cliTree);
        Assert.Equal(coreTree.Keys.Order(), cliTree.Keys.Order());
        foreach (var (path, hash) in cliTree)
        {
            Assert.True(coreTree[path] == hash, $"{path} differs between the command line and the Core");
        }
    }

    [LibreOfficeFact]
    public async Task ASettingsFileGivesTheSameResultAsTheEquivalentOptions()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["only page"]);
        var settings = dir.Combine("settings.json");
        File.WriteAllText(settings, $$"""
            { "output": { "folder": {{System.Text.Json.JsonSerializer.Serialize(dir.Combine("from-file"))}} },
              "engine": { "kind": "libreoffice" },
              "images": { "dpi": 72 },
              "steps": { "step2": false } }
            """);

        var fromFile = await Cli.RunAsync("convert", source, "--settings", settings);
        var fromOptions = await Cli.RunAsync("convert", source, "--out", dir.Combine("from-options"), "--engine", "libreoffice", "--dpi", "72", "--step2", "off");

        Assert.Equal(ExitCodes.Success, fromFile.ExitCode);
        Assert.Equal(ExitCodes.Success, fromOptions.ExitCode);
        Assert.Equal(Tree(dir.Combine("from-file")), Tree(dir.Combine("from-options")));
        Assert.DoesNotContain("](images/", File.ReadAllText(dir.Combine("from-file", "a", "a.md"))); // step2 off from the file
    }
}
