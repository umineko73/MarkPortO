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

using MarkPortO.Core.Vlm;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarkPortO.Cli;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Cli.Tests;

/// <summary>FR-044, FR-046, FR-049, FR-052, FR-057: consent, the connection list, the limits and the exit codes 3 and 4.</summary>
public class ConsentCommandTests
{
    private static string Settings(TempDir dir, object connection, string selected = "cloud", string? engine = null)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["engine"] = new { kind = engine ?? "libreoffice" },
            ["ui"] = new { language = "ja" },
            ["images"] = new { dpi = 72 },
            ["steps"] = new { step3 = true },
            ["vlm"] = new { selected, connections = new[] { connection } },
        });
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static object CommandConnection(bool consent = false, string name = "cloud") => new Dictionary<string, object?>
    {
        ["name"] = name,
        ["kind"] = "command",
        ["command"] = "codex",
        ["executable"] = FakeVlm.Exe,
        ["model"] = "gpt-6-luna",
        ["timeoutSeconds"] = 30,
        ["externalConsent"] = consent ? new { at = "2026-10-26T01:00:00+00:00", noticeVersion = ExternalConsent.CurrentNoticeVersion } : null,
    };

    private static object LocalConnection() => new { name = "local", baseUrl = "http://192.168.11.220:1234/v1", model = "m" };

    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    [Fact]
    public async Task Consent_ShowsTheNotice_AndWritesTheConsentIntoTheFile()
    {
        using var dir = new TempDir();
        var path = Settings(dir, CommandConnection());

        var run = await Cli.RunAsync("consent", "cloud", "--settings", path);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("outside the local network", run.Output);
        Assert.Contains("codex", run.Output);
        Assert.Contains("Usage charges", run.Output);
        var consent = Read(path)["vlm"]!["connections"]![0]!["externalConsent"]!;
        Assert.Equal(ExternalConsent.CurrentNoticeVersion, consent["noticeVersion"]!.GetValue<int>());
        Assert.NotNull(consent["at"]);
    }

    [Fact]
    public async Task Consent_KeepsTheOtherSettings()
    {
        using var dir = new TempDir();
        var path = Settings(dir, CommandConnection());

        await Cli.RunAsync("consent", "cloud", "--settings", path);

        var root = Read(path);
        Assert.Equal(72, root["images"]!["dpi"]!.GetValue<int>());
        Assert.Equal("gpt-6-luna", root["vlm"]!["connections"]![0]!["model"]!.GetValue<string>());
        Assert.Equal("codex", root["vlm"]!["connections"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConsentRevoke_RemovesTheConsent()
    {
        using var dir = new TempDir();
        var path = Settings(dir, CommandConnection(consent: true));

        var run = await Cli.RunAsync("consent", "cloud", "--revoke", "--settings", path);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Null(Read(path)["vlm"]!["connections"]![0]!["externalConsent"]);
    }

    [Fact]
    public async Task Consent_ForAConnectionInTheLocalNetwork_IsRefused()
    {
        using var dir = new TempDir();
        var path = Settings(dir, LocalConnection(), selected: "local");

        var run = await Cli.RunAsync("consent", "local", "--settings", path);

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("needs no consent", run.Error);
    }

    [Fact]
    public async Task Consent_ForAnUnknownConnection_IsRefused()
    {
        using var dir = new TempDir();
        var path = Settings(dir, CommandConnection());

        var run = await Cli.RunAsync("consent", "nope", "--settings", path);

        Assert.Equal(ExitCodes.InvalidArguments, run.ExitCode);
        Assert.Contains("'nope' was not found", run.Error);
    }

    [Fact]
    public async Task ListConnections_ShowsTheKindTheConsentAndTheLimits()
    {
        using var dir = new TempDir();
        var connection = (Dictionary<string, object?>)CommandConnection();
        connection["maxPages"] = 40;
        connection["maxCostUsd"] = 1.5;
        connection["parallel"] = 3;
        var path = Settings(dir, connection);

        var run = await Cli.RunAsync("list-connections", "--settings", path);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("command=codex", run.Output);
        Assert.Contains("model=gpt-6-luna", run.Output);
        Assert.Contains("parallel=3", run.Output);
        Assert.Contains("consent=no", run.Output);
        Assert.Contains("max-pages=40", run.Output);
        Assert.Contains("max-cost=1.5", run.Output);
    }

    [Fact]
    public async Task ListConnections_OfALocalServer_NeedsNoConsent_AndHasNoLimits()
    {
        using var dir = new TempDir();
        var path = Settings(dir, LocalConnection(), selected: "local");

        var run = await Cli.RunAsync("list-connections", "--settings", path);

        Assert.Contains("consent=not-needed", run.Output);
        Assert.Contains("max-pages=none", run.Output);
    }

    [Fact]
    public async Task CheckConnection_WithoutTheConsent_StopsWithExitCode3_AndStartsNothing()
    {
        using var dir = new TempDir();
        var path = Settings(dir, CommandConnection());

        var run = await Cli.RunAsync("check-connection", "--image", "--settings", path);

        Assert.Equal(ExitCodes.StartFailed, run.ExitCode);
        Assert.Contains("consent", run.Output);
        Assert.DoesNotContain("OK ", run.Output);
    }

    [Fact]
    public async Task CheckConnection_WithTheConsent_FindsTheCommand()
    {
        using var dir = new TempDir();
        var path = Settings(dir, CommandConnection(consent: true));

        var run = await Cli.RunAsync("check-connection", "--settings", path);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("OK  connection:", run.Output);
        Assert.Contains("fake-vlm 1.0.0", run.Output);
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task Convert_WithoutTheConsent_StopsWithExitCode3_AndNothingIsConverted()
    {
        using var dir = new TempDir();
        var path = Settings(dir, CommandConnection());
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);

        var run = await Cli.RunAsync("convert", source, "--out", dir.Combine("out"), "--settings", path);

        Assert.Equal(ExitCodes.StartFailed, run.ExitCode);
        Assert.Contains("consent", run.Error);
        Assert.False(Directory.Exists(dir.Combine("out")) && Directory.EnumerateFiles(dir.Combine("out"), "*.md", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void TheOptionsOfTheCommandLine_OverrideTheLimitsOfTheConnection()
    {
        var settings = AppSettings.CreateDefault();
        settings.Input.Paths = ["x"];
        settings.Output.Folder = "y";
        settings.Steps.Step3 = true;
        settings.Vlm.Connections.Add(new ConnectionProfile { Name = "cloud", Kind = ConnectionKind.Command, Command = CommandKind.Codex, MaxPages = 100, Parallel = 1 });
        settings.Vlm.Selected = "cloud";

        var (resolved, errors) = ConvertArgsResolver.Resolve(new ConvertArgs { MaxPages = 5, MaxCost = 0.25, Parallel = 4 }, settings);

        Assert.Empty(errors);
        var connection = resolved!.FindConnection("cloud")!;
        Assert.Equal(5, connection.MaxPages);
        Assert.Equal(0.25, connection.MaxCostUsd);
        Assert.Equal(4, connection.Parallel);
        Assert.Equal(100, settings.FindConnection("cloud")!.MaxPages); // the settings that were read stay as they were
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(null, 0.0, null)]
    [InlineData(null, -1.0, null)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, 99)]
    public void ALimitOutsideItsRange_IsRefused(int? pages, double? cost, int? parallel)
    {
        var settings = AppSettings.CreateDefault();
        settings.Input.Paths = ["x"];
        settings.Output.Folder = "y";
        settings.Steps.Step3 = true;
        settings.Vlm.Connections.Add(new ConnectionProfile { Name = "cloud", Kind = ConnectionKind.Command, Command = CommandKind.Codex });
        settings.Vlm.Selected = "cloud";

        var (resolved, errors) = ConvertArgsResolver.Resolve(new ConvertArgs { MaxPages = pages, MaxCost = cost, Parallel = parallel }, settings);

        Assert.Null(resolved);
        Assert.NotEmpty(errors);
    }

    [LibreOfficeFact]
    public async Task AUsageLimit_StopsTheRunWithExitCode4_AndAResumeFinishesIt()
    {
        using var dir = new TempDir();
        var path = Settings(dir, CommandConnection(consent: true));
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"]);
        var output = dir.Combine("out");

        // The environment of this process is inherited by the stand-in command.
        Environment.SetEnvironmentVariable("FAKE_VLM_MODE", "usagelimit");
        try
        {
            var first = await Cli.RunAsync("convert", source, "--out", output, "--settings", path);

            Assert.Equal(ExitCodes.StoppedByLimit, first.ExitCode);
            Assert.Contains("stopped:", first.Error);
            Assert.Contains("usage limit", first.Error);
            Assert.Contains("Page images will be sent outside the local network", first.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_VLM_MODE", null);
        }

        var second = await Cli.RunAsync("convert", "--resume", "--out", output, "--settings", path);

        Assert.Equal(ExitCodes.Success, second.ExitCode);
    }
}
