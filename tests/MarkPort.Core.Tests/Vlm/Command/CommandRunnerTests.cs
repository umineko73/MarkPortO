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

using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Vlm.Command;

namespace MarkPort.Core.Tests.Vlm.Command;

/// <summary>FR-044, SC-024: starting a command without a shell and ending it, with its child processes, when it takes too long.</summary>
public class CommandRunnerTests
{
    private static CommandRequest Codex(string folder, string? stdin = "prompt", Dictionary<string, string?>? environment = null, TimeSpan? timeout = null, string? file = null) =>
        new(file ?? FakeVlm.Exe, ["exec", "-"], folder, stdin, environment ?? [], timeout ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task ACommand_IsRunWithTheInputOnTheStandardInput_AndItsOutputIsRead()
    {
        using var dir = new TempDir();

        var result = await CommandRunner.RunAsync(Codex(dir.Path), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("FAKE:prompt", result.StandardOutput);
    }

    [Fact]
    public async Task JapaneseText_GoesThroughTheStandardInputAndOutput_AsUtf8()
    {
        using var dir = new TempDir();

        var result = await CommandRunner.RunAsync(Codex(dir.Path, stdin: "画像の文字を答えてください。"), CancellationToken.None);

        Assert.Contains("FAKE:画像の文字を答えてください。", result.StandardOutput);
    }

    [Fact]
    public async Task TheCommand_RunsInTheGivenFolder_AndSeesTheGivenEnvironment()
    {
        using var dir = new TempDir();
        var log = dir.Combine("log.jsonl");

        await CommandRunner.RunAsync(Codex(dir.Path, environment: new() { ["FAKE_VLM_LOG"] = log }), CancellationToken.None);

        var call = Assert.Single(FakeVlm.Calls(log));
        Assert.Equal(Path.GetFullPath(dir.Path).TrimEnd('\\'), Path.GetFullPath(call["cwd"]!.GetValue<string>()).TrimEnd('\\'));
    }

    [Fact]
    public async Task TheErrorOutputAndTheExitCode_AreReturned()
    {
        using var dir = new TempDir();

        var result = await CommandRunner.RunAsync(Codex(dir.Path, environment: new() { ["FAKE_VLM_MODE"] = "fail" }), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Something unexpected", result.StandardError);
    }

    [Fact]
    public async Task ACommandThatDoesNotFinish_IsEnded_AndReportedAsTimedOut()
    {
        using var dir = new TempDir();
        var log = dir.Combine("log.jsonl");
        var started = DateTime.UtcNow;

        await Assert.ThrowsAsync<CommandTimeoutException>(() => CommandRunner.RunAsync(
            Codex(dir.Path, environment: new() { ["FAKE_VLM_MODE"] = "hang", ["FAKE_VLM_LOG"] = log }, timeout: TimeSpan.FromSeconds(2)),
            CancellationToken.None));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "the command is not waited for");
    }

    [Fact]
    public async Task TimingOut_EndsTheChildProcessesToo_SC024()
    {
        using var dir = new TempDir();
        var pidFile = dir.Combine("child.pid");

        await Assert.ThrowsAsync<CommandTimeoutException>(() => CommandRunner.RunAsync(
            Codex(dir.Path, environment: new() { ["FAKE_VLM_MODE"] = "hangtree", ["FAKE_VLM_PIDFILE"] = pidFile }, timeout: TimeSpan.FromSeconds(3)),
            CancellationToken.None));

        Assert.True(File.Exists(pidFile), "the command started a child");
        var child = int.Parse(File.ReadAllText(pidFile));
        for (var i = 0; i < 50 && FakeVlm.IsRunning(child); i++)
        {
            await Task.Delay(100);
        }

        Assert.False(FakeVlm.IsRunning(child), "the child process was left running");
    }

    [Fact]
    public async Task Cancelling_EndsTheCommand_AndPassesTheCancellationOn()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var run = CommandRunner.RunAsync(Codex(dir.Path, environment: new() { ["FAKE_VLM_MODE"] = "hang" }, timeout: TimeSpan.FromSeconds(60)), cts.Token);

        await Task.Delay(1000);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task ACommandThatIsNotThere_IsReportedAsNotFound()
    {
        using var dir = new TempDir();

        var ex = await Assert.ThrowsAsync<CommandStartException>(() => CommandRunner.RunAsync(Codex(dir.Path, file: "no-such-command-xyz"), CancellationToken.None));

        Assert.Contains("no-such-command-xyz", ex.Message);
    }

    [Fact]
    public async Task ACmdFile_IsRunThroughTheInterpreter_WithJapaneseInput()
    {
        // The commands that npm installs are .cmd files.
        using var dir = new TempDir();
        var shim = dir.Combine("fake-codex.cmd");
        File.WriteAllText(shim, $"@\"{FakeVlm.Exe}\" %*\r\n");

        var result = await CommandRunner.RunAsync(Codex(dir.Path, stdin: "日本語の指示", file: shim), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("FAKE:日本語の指示", result.StandardOutput);
    }

    [Fact]
    public async Task ACmdFile_IsFoundByItsNameInThePath()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("fake-codex-by-name.cmd"), $"@\"{FakeVlm.Exe}\" %*\r\n");
        var path = dir.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        Assert.Equal(dir.Combine("fake-codex-by-name.cmd"), ExecutableResolver.Resolve("fake-codex-by-name", n => n == "PATH" ? path : Environment.GetEnvironmentVariable(n)), ignoreCase: true);
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("100%")]
    [InlineData("a^b")]
    [InlineData("a\"b")]
    public async Task ForACmdFile_AnArgumentThatTheInterpreterWouldReadIsRefused(string argument)
    {
        using var dir = new TempDir();
        var shim = dir.Combine("fake.cmd");
        File.WriteAllText(shim, $"@\"{FakeVlm.Exe}\" %*\r\n");

        await Assert.ThrowsAsync<CommandStartException>(() => CommandRunner.RunAsync(
            new CommandRequest(shim, ["exec", argument], dir.Path, "x", new Dictionary<string, string?>(), TimeSpan.FromSeconds(10)),
            CancellationToken.None));
    }
}
