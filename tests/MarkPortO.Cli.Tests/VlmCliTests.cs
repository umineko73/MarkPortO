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

using System.Text.Json;
using MarkPortO.Cli;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Cli.Tests;

/// <summary>The VLM parts of the command line: check-connection, list-connections, and convert --step3.</summary>
public class VlmCliTests
{
    private static string Settings(TempDir dir, string baseUrl, string? model = "vision-model", string? apiKey = null, string? apiKeyEnv = null, bool selected = true)
    {
        var connection = new Dictionary<string, object?>
        {
            ["name"] = "fake",
            ["baseUrl"] = baseUrl,
            ["model"] = model,
            ["timeoutSeconds"] = 10,
        };
        if (apiKey is not null)
        {
            connection["apiKey"] = apiKey;
        }

        if (apiKeyEnv is not null)
        {
            connection["apiKeyEnv"] = apiKeyEnv;
        }

        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["engine"] = new { kind = "libreoffice" },
            ["ui"] = new { language = "ja" },
            ["images"] = new { dpi = 72 },
            ["vlm"] = new { selected = selected ? "fake" : "", connections = new[] { connection } },
        });
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task ListConnections_ShowsEverythingExceptTheKey()
    {
        using var dir = new TempDir();
        var settings = Settings(dir, "http://localhost:1234/v1", apiKey: "sk-list-secret-123456");

        var run = await Cli.RunAsync("list-connections", "--settings", settings);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("* fake", run.Output); // the selected one is marked
        Assert.Contains("http://localhost:1234/v1", run.Output);
        Assert.Contains("model=vision-model", run.Output);
        Assert.Contains("timeout=10s", run.Output);
        Assert.Contains("key=saved", run.Output);
        Assert.DoesNotContain("sk-list-secret-123456", run.Output + run.Error);
    }

    [Fact]
    public async Task ListConnections_ForAnEnvironmentKey_ShowsOnlyTheVariableName()
    {
        using var dir = new TempDir();
        var settings = Settings(dir, "http://localhost:1234/v1", apiKeyEnv: "MY_SECRET_VAR");

        var run = await Cli.RunAsync("list-connections", "--settings", settings);

        Assert.Contains("key=environment variable MY_SECRET_VAR", run.Output);
    }

    [Fact]
    public async Task CheckConnection_Succeeds_ForAWorkingServer()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();

        var run = await Cli.RunAsync("check-connection", "--settings", Settings(dir, server.BaseUrl));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("OK  connection:", run.Output);
        Assert.DoesNotContain("image", run.Output); // the image check is only run with --image
        Assert.DoesNotContain(server.Requests, r => r.HasImage);
    }

    [Fact]
    public async Task CheckConnection_WithImage_RunsBothChecks()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { ImageCode = "AB23" };
        // The real random code is not "AB23", so a fake that always answers AB23 fails the image check:
        // that is the point of a fresh code. A server that reads the picture is covered in the Core tests.
        var run = await Cli.RunAsync("check-connection", "--image", "--settings", Settings(dir, server.BaseUrl));

        Assert.Contains("OK  connection:", run.Output);
        Assert.Contains("image:", run.Output);
        Assert.Contains(server.Requests, r => r.HasImage);
    }

    [Fact]
    public async Task CheckConnection_ForAModelThatCannotSeeImages_ExitsWith3()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { SupportsImages = false };

        var run = await Cli.RunAsync("check-connection", "--image", "--settings", Settings(dir, server.BaseUrl));

        Assert.Equal(ExitCodes.StartFailed, run.ExitCode);
        Assert.Contains("OK  connection:", run.Output);
        Assert.Contains("NG  image:", run.Output);
    }

    [Fact]
    public async Task CheckConnection_ForAServerThatCannotBeReached_ExitsWith3()
    {
        using var dir = new TempDir();

        var run = await Cli.RunAsync("check-connection", "--settings", Settings(dir, ClosedPort.Url));

        Assert.Equal(ExitCodes.StartFailed, run.ExitCode);
        Assert.Contains("NG  connection:", run.Output);
    }

    [Fact]
    public async Task CheckConnection_UsesTheKeyFromTheEnvironmentVariable()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { RequiredKey = "env-key-for-cli-test-77" };
        const string variable = "MARKPORTO_CLI_TEST_KEY_A";
        Environment.SetEnvironmentVariable(variable, "env-key-for-cli-test-77");
        try
        {
            var run = await Cli.RunAsync("check-connection", "--settings", Settings(dir, server.BaseUrl, apiKeyEnv: variable));

            Assert.Equal(ExitCodes.Success, run.ExitCode);
            Assert.DoesNotContain("env-key-for-cli-test-77", run.Output + run.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task CheckConnection_WhenTheEnvironmentVariableIsNotSet_ExitsWith2_NamingIt()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();

        var run = await Cli.RunAsync("check-connection", "--settings", Settings(dir, server.BaseUrl, apiKeyEnv: "MARKPORTO_CLI_TEST_UNSET_VAR"));

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("MARKPORTO_CLI_TEST_UNSET_VAR", run.Error);
    }

    [Fact]
    public async Task CheckConnection_ForAnUnknownConnection_ExitsWith2()
    {
        using var dir = new TempDir();
        var settings = Settings(dir, "http://localhost:1234/v1");

        var run = await Cli.RunAsync("check-connection", "--connection", "nope", "--settings", settings);

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("nope", run.Error);
    }

    // ---- convert --step3 ----

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task ConvertWithStep3_ReadsThePages_AndTheResultHasTheUsage()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { PromptTokens = 50, CompletionTokens = 5 };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"]);
        var resultPath = dir.Combine("result.json");

        var run = await Cli.RunAsync("convert", source, "--settings", Settings(dir, server.BaseUrl), "--out", dir.Combine("out"), "--step3", "on", "--result", resultPath);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("step3  done", run.Output);
        var markdown = File.ReadAllText(dir.Combine("out", "a", "a.md"));
        Assert.Contains("<!-- vlm:begin page=1 -->", markdown);
        Assert.Contains("> **VLM使用量**: 合計 prompt=100 completion=10 total=110", markdown);
        using var json = JsonDocument.Parse(File.ReadAllText(resultPath));
        var usage = json.RootElement.GetProperty("documents")[0].GetProperty("usage");
        Assert.Equal(110, usage.GetProperty("totalTokens").GetInt32());
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task ConvertWithStep3_WhenAPageFails_ExitsWith1_AndTheFileIsStillDone()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { FailChatRequest = n => n == 1 };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"]);

        var run = await Cli.RunAsync("convert", source, "--settings", Settings(dir, server.BaseUrl), "--out", dir.Combine("out"), "--step3", "on");

        Assert.Equal(ExitCodes.SomeFailed, run.ExitCode);
        Assert.Contains("1 done, 0 failed", run.Output);
        Assert.Contains("1 page(s) could not be processed", run.Output);
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task ConvertWithStep3_ForAServerThatIsDown_ExitsWith3_AndWritesNothing()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);
        var outFolder = dir.Combine("out");

        var run = await Cli.RunAsync("convert", source, "--settings", Settings(dir, ClosedPort.Url), "--out", outFolder, "--step3", "on");

        Assert.Equal(ExitCodes.StartFailed, run.ExitCode);
        Assert.Contains("The VLM connection check failed", run.Error);
        Assert.False(Directory.Exists(outFolder));
    }

    [Fact]
    public async Task ConvertWithStep3_WithoutASelectedConnection_ExitsWith2()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);

        var run = await Cli.RunAsync("convert", source, "--settings", Settings(dir, "http://localhost:1234/v1", selected: false), "--out", dir.Combine("out"), "--step3", "on");

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("vlm.selected", run.Error);
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task ConvertWithStep3Off_NeverContactsTheServer()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);

        var run = await Cli.RunAsync("convert", source, "--settings", Settings(dir, server.BaseUrl), "--out", dir.Combine("out"));

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(server.Requests);
    }
}
