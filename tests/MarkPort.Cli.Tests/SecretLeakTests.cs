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

using System.Text.Json;
using MarkPort.Cli;
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Cli.Tests;

/// <summary>SC-012, constitution IV: the access key appears nowhere a run writes or prints.</summary>
public class SecretLeakTests
{
    private const string Key = "sk-leak-canary-9f8e7d6c5b4a";

    private static string Settings(TempDir dir, string baseUrl, string apiKeyEnv)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["engine"] = new { kind = "libreoffice" },
            ["images"] = new { dpi = 72 },
            ["vlm"] = new
            {
                selected = "fake",
                connections = new[] { new { name = "fake", baseUrl, model = "vision-model", apiKeyEnv, timeoutSeconds = 10 } },
            },
        });
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static void AssertNoKeyIn(TempDir dir, params string[] texts)
    {
        foreach (var text in texts)
        {
            Assert.DoesNotContain(Key, text);
        }

        // Everything the run wrote: Markdown, the job state, the result, logs. A run that stops early may write nothing.
        Directory.CreateDirectory(dir.Combine("out"));
        foreach (var file in Directory.EnumerateFiles(dir.Combine("out"), "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(dir.Path, "*.json").Where(f => !f.EndsWith("settings.json"))))
        {
            if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.False(File.ReadAllText(file).Contains(Key), $"the key is in {file}");
        }
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task AFullRunWithTheKey_LeavesItOutOfEveryOutput()
    {
        const string variable = "MARKPORT_LEAK_TEST_KEY";
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { RequiredKey = Key };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"]);
        var resultPath = dir.Combine("result.json");
        Environment.SetEnvironmentVariable(variable, Key);
        try
        {
            var run = await Cli.RunAsync("convert", source, "--settings", Settings(dir, server.BaseUrl, variable), "--out", dir.Combine("out"), "--step3", "on", "--result", resultPath);

            Assert.Equal(ExitCodes.Success, run.ExitCode);
            Assert.All(server.Requests.Where(r => r.Path.EndsWith("chat/completions")), r => Assert.Equal("Bearer " + Key, r.Authorization));
            AssertNoKeyIn(dir, run.Output, run.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task ARunTheServerRejects_DoesNotPutTheKeyInTheErrors()
    {
        const string variable = "MARKPORT_LEAK_TEST_KEY2";
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { RequiredKey = "a-different-key" };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);
        Environment.SetEnvironmentVariable(variable, Key);
        try
        {
            var run = await Cli.RunAsync("convert", source, "--settings", Settings(dir, server.BaseUrl, variable), "--out", dir.Combine("out"), "--step3", "on", "--result", dir.Combine("result.json"));

            Assert.NotEqual(ExitCodes.Success, run.ExitCode);
            AssertNoKeyIn(dir, run.Output, run.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task TheConnectionCheck_DoesNotPrintTheKey()
    {
        const string variable = "MARKPORT_LEAK_TEST_KEY3";
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("out"));
        using var server = new FakeOpenAiServer { RequiredKey = Key };
        Environment.SetEnvironmentVariable(variable, Key);
        try
        {
            var run = await Cli.RunAsync("check-connection", "--settings", Settings(dir, server.BaseUrl, variable));
            var listed = await Cli.RunAsync("list-connections", "--settings", Settings(dir, server.BaseUrl, variable));

            AssertNoKeyIn(dir, run.Output, run.Error, listed.Output, listed.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
