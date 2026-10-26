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

using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using MarkPortO.Core.Vlm;
using MarkPortO.Core.Vlm.Command;

namespace MarkPortO.Core.Tests.Vlm.Command;

/// <summary>FR-045, FR-046, FR-055, SC-023: the checks of a connection to a command, and the consent that comes before everything.</summary>
public class CommandConnectionCheckerTests
{
    private static ConnectionProfile Connection(CommandKind kind = CommandKind.Codex, string? executable = null, bool consent = true)
    {
        var connection = new ConnectionProfile { Name = "cloud", Kind = ConnectionKind.Command, Command = kind, Executable = executable ?? FakeVlm.Exe, Model = "m" };
        if (consent)
        {
            ExternalConsent.Grant(connection, DateTimeOffset.UtcNow);
        }

        return connection;
    }

    private static CommandConnectionChecker Checker(ConnectionProfile connection, TempDir dir, string mode = "ok", string reply = "AB23", string? log = null)
    {
        var environment = new Dictionary<string, string?> { ["FAKE_VLM_MODE"] = mode, ["FAKE_VLM_REPLY"] = reply };
        if (log is not null)
        {
            environment["FAKE_VLM_LOG"] = log;
        }

        var reader = new CommandVlmReader(connection, [], dir.Combine("work"), environment);
        return new CommandConnectionChecker(connection, reader, () => "AB23");
    }

    [Fact]
    public async Task TheConnectionCheck_FindsTheCommand_AndKeepsItsVersion()
    {
        using var dir = new TempDir();
        var checker = Checker(Connection(), dir);

        var result = await checker.CheckConnectionAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("fake-vlm 1.0.0", checker.Version);
        Assert.Contains("fake-vlm 1.0.0", result.Message);
    }

    [Fact]
    public async Task TheCommandCheck_DoesNotCallTheModel()
    {
        using var dir = new TempDir();
        var log = dir.Combine("log.jsonl");
        var checker = Checker(Connection(), dir, log: log);

        var result = await checker.CheckCommandAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Empty(FakeVlm.Calls(log)); // --version only; no image, no cost
    }

    [Fact]
    public async Task TheConnectionCheck_AsksOneShortQuestionWithoutAnImage()
    {
        using var dir = new TempDir();
        var log = dir.Combine("log.jsonl");
        var checker = Checker(Connection(), dir, log: log);

        var result = await checker.CheckConnectionAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var call = Assert.Single(FakeVlm.Calls(log));
        Assert.DoesNotContain("-i", call["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Theory]
    [InlineData("notloggedin", "logged in")]
    [InlineData("usagelimit", "usage limit")]
    [InlineData("badoutput", "")]
    public async Task TheConnectionCheck_FailsWhenTheCommandCannotAnswer(string mode, string expected)
    {
        using var dir = new TempDir();

        var result = await Checker(Connection(), dir, mode).CheckConnectionAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACommandThatIsNotThere_FailsTheConnectionCheck_WithItsName()
    {
        using var dir = new TempDir();
        var checker = Checker(Connection(executable: "no-such-command-xyz"), dir);

        var result = await checker.CheckConnectionAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no-such-command-xyz", result.Message);
    }

    [Fact]
    public async Task TheImageCheck_PassesWhenTheCommandReadsTheCode()
    {
        using var dir = new TempDir();

        var result = await Checker(Connection(), dir, reply: "The text is AB23.").CheckImageAsync(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public async Task TheImageCheck_FailsWhenTheAnswerIsNotTheCode()
    {
        using var dir = new TempDir();

        var result = await Checker(Connection(), dir, reply: "I cannot see any image.").CheckImageAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("I cannot see any image.", result.Message);
    }

    [Theory]
    [InlineData("notloggedin", "logged in")]
    [InlineData("usagelimit", "usage limit")]
    public async Task TheImageCheck_SaysWhyItFailed(string mode, string expected)
    {
        using var dir = new TempDir();

        var result = await Checker(Connection(), dir, mode).CheckImageAsync(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- the consent, before anything

    private static AppSettings Settings(ConnectionProfile connection)
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(connection);
        settings.Vlm.Selected = connection.Name;
        return settings;
    }

    [Fact]
    public async Task WithoutTheConsent_TheCheckBeforeStep1_FailsAndNothingIsStarted_SC023()
    {
        using var dir = new TempDir();
        var (setup, error) = VlmSetupFactory.Create(Settings(Connection(consent: false)));

        Assert.NotNull(setup);
        Assert.Null(error);
        var result = await setup!.Preflight(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("consent", result.Message);
        Assert.Contains("markporto consent cloud", result.Message);
    }

    [Fact]
    public async Task WithTheConsent_TheCheckBeforeStep1_RunsTheConnectionCheck_AndRemembersTheVersion()
    {
        var (setup, _) = VlmSetupFactory.Create(Settings(Connection()));

        var result = await setup!.Preflight(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("fake-vlm 1.0.0", setup.CommandVersion);
    }

    [Fact]
    public void ACommandSetup_CarriesTheParallelAndTheLimitsOfTheConnection()
    {
        var connection = Connection();
        connection.Parallel = 3;
        connection.MaxPages = 40;
        connection.MaxCostUsd = 1.5;
        connection.MaxLinesPerRead = 15;

        var (setup, _) = VlmSetupFactory.Create(Settings(connection));

        Assert.Equal(3, setup!.Parallel);
        Assert.Equal(40, setup.MaxPages);
        Assert.Equal(1.5, setup.MaxCostUsd);
        Assert.Equal(15, setup.MaxLinesPerRead);
    }

    [Fact]
    public async Task ARemoteOpenAiConnection_NeedsTheConsentToo_AndNothingIsSentWithoutIt()
    {
        var connection = new ConnectionProfile { Name = "remote", BaseUrl = "https://api.example.invalid/v1", Model = "m", ApiKey = "k" };
        var (setup, _) = VlmSetupFactory.Create(Settings(connection));

        var result = await setup!.Preflight(CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("consent", result.Message); // the address is not even tried
    }

    [Fact]
    public async Task ALocalOpenAiConnection_NeedsNoConsent()
    {
        using var server = new FakeOpenAiServer();
        var connection = new ConnectionProfile { Name = "local", BaseUrl = server.BaseUrl, Model = "vision-model" };
        var (setup, _) = VlmSetupFactory.Create(Settings(connection));

        var result = await setup!.Preflight(CancellationToken.None);

        Assert.True(result.Ok, result.Message);
    }
}
