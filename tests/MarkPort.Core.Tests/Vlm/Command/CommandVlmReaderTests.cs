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

using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Vlm;
using MarkPort.Core.Vlm.Command;

namespace MarkPort.Core.Tests.Vlm.Command;

/// <summary>FR-044, FR-047, FR-049, FR-050, FR-056: reading an image with a command, against the stand-in.</summary>
public class CommandVlmReaderTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3];

    private static ConnectionProfile Connection(CommandKind kind, int timeoutSeconds = 30, string model = "m") => new()
    {
        Name = "c",
        Kind = ConnectionKind.Command,
        Command = kind,
        Executable = FakeVlm.Exe,
        Model = model,
        TimeoutSeconds = timeoutSeconds,
    };

    private static CommandVlmReader Reader(CommandKind kind, string mode, TempDir dir, int timeoutSeconds = 30, string? log = null, IReadOnlyList<TimeSpan>? waits = null, Dictionary<string, string?>? more = null)
    {
        var environment = new Dictionary<string, string?> { ["FAKE_VLM_MODE"] = mode };
        if (log is not null)
        {
            environment["FAKE_VLM_LOG"] = log;
        }

        foreach (var (key, value) in more ?? [])
        {
            environment[key] = value;
        }

        return new CommandVlmReader(Connection(kind, timeoutSeconds), waits ?? [TimeSpan.Zero, TimeSpan.Zero], dir.Combine("work"), environment);
    }

    public static TheoryData<CommandKind> Kinds => new() { CommandKind.Claude, CommandKind.Codex, CommandKind.OpenCode };

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TheAnswerAndTheUsage_AreReturned(CommandKind kind)
    {
        using var dir = new TempDir();

        var response = await Reader(kind, "ok", dir).ReadAsync("PROMPT 日本語", Png, CancellationToken.None);

        Assert.Equal("FAKE:PROMPT 日本語", response.Text);
        Assert.True(response.Usage.IsAvailable);
    }

    [Fact]
    public async Task Claude_ReportsTheCost()
    {
        using var dir = new TempDir();

        var response = await Reader(CommandKind.Claude, "ok", dir).ReadAsync("p", Png, CancellationToken.None);

        Assert.Equal(0.0123, response.CostUsd);
        Assert.Equal(159, response.Usage.TotalTokens);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task WhenTheCommandReportsNoUsage_ItIsUnavailable_AndTheReadingSucceeds(CommandKind kind)
    {
        using var dir = new TempDir();

        var response = await Reader(kind, "nousage", dir).ReadAsync("p", Png, CancellationToken.None);

        Assert.False(response.Usage.IsAvailable);
        Assert.Null(response.CostUsd);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TheCommandRunsInAnEmptyFolderOfItsOwn_WithACopyOfTheImage_ThatIsRemovedAfterwards(CommandKind kind)
    {
        using var dir = new TempDir();
        var log = dir.Combine("log.jsonl");

        await Reader(kind, "writefile", dir, log: log).ReadAsync("p", Png, CancellationToken.None);

        var call = Assert.Single(FakeVlm.Calls(log));
        var cwd = call["cwd"]!.GetValue<string>();
        Assert.StartsWith(Path.GetFullPath(dir.Combine("work")), Path.GetFullPath(cwd), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(dir.Combine("work"))); // the folder, with what the command wrote, is gone
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "hello.txt")));
    }

    [Fact]
    public async Task TheModelAndTheEffortOfTheConnection_ArePassedToTheCommand()
    {
        using var dir = new TempDir();
        var log = dir.Combine("log.jsonl");
        var reader = new CommandVlmReader(
            Connection(CommandKind.Claude, model: "haiku"),
            [],
            dir.Combine("work"),
            new Dictionary<string, string?> { ["FAKE_VLM_LOG"] = log });

        await reader.ReadAsync("p", Png, CancellationToken.None);

        var args = FakeVlm.Calls(log)[0]["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal("haiku", args[args.IndexOf("--model") + 1]);
        Assert.Equal("low", args[args.IndexOf("--effort") + 1]);
    }

    [Theory]
    [InlineData("notloggedin", VlmFailureKind.NotLoggedIn)]
    [InlineData("usagelimit", VlmFailureKind.UsageLimit)]
    [InlineData("imagetoolarge", VlmFailureKind.ImageTooLarge)]
    [InlineData("fail", VlmFailureKind.Unknown)]
    [InlineData("badoutput", VlmFailureKind.BadOutput)]
    [InlineData("empty", VlmFailureKind.BadOutput)]
    public async Task AFailure_IsReportedWithItsKind_ForEveryCommand(string mode, VlmFailureKind kind)
    {
        foreach (var command in new[] { CommandKind.Claude, CommandKind.Codex, CommandKind.OpenCode })
        {
            using var dir = new TempDir();

            var ex = await Assert.ThrowsAsync<VlmException>(() => Reader(command, mode, dir).ReadAsync("p", Png, CancellationToken.None));

            Assert.Equal(kind, ex.Kind);
        }
    }

    [Fact]
    public async Task ARateLimit_IsTriedAgainAfterAWait_AndThenReported()
    {
        using var dir = new TempDir();
        var log = dir.Combine("log.jsonl");

        var ex = await Assert.ThrowsAsync<VlmException>(() => Reader(CommandKind.Codex, "ratelimit", dir, log: log).ReadAsync("p", Png, CancellationToken.None));

        Assert.Equal(VlmFailureKind.RateLimit, ex.Kind);
        Assert.Equal(3, FakeVlm.Calls(log).Count); // the first try and one for each wait
    }

    [Fact]
    public async Task AFailureThatIsNotARateLimit_IsNotTriedAgain()
    {
        using var dir = new TempDir();
        var log = dir.Combine("log.jsonl");

        await Assert.ThrowsAsync<VlmException>(() => Reader(CommandKind.Codex, "usagelimit", dir, log: log).ReadAsync("p", Png, CancellationToken.None));

        Assert.Single(FakeVlm.Calls(log));
    }

    [Fact]
    public async Task ACommandThatDoesNotFinish_IsEnded_AndReportedAsTimedOut()
    {
        using var dir = new TempDir();

        var ex = await Assert.ThrowsAsync<VlmException>(() => Reader(CommandKind.Codex, "hang", dir, timeoutSeconds: 1).ReadAsync("p", Png, CancellationToken.None));

        Assert.True(ex.TimedOut);
        Assert.Equal(VlmFailureKind.Timeout, ex.Kind);
        Assert.Empty(Directory.GetDirectories(dir.Combine("work")));
    }

    [Fact]
    public async Task ACommandThatIsNotThere_IsReportedAsNotFound()
    {
        using var dir = new TempDir();
        var connection = Connection(CommandKind.Codex);
        connection.Executable = "no-such-command-xyz";

        var ex = await Assert.ThrowsAsync<VlmException>(() => new CommandVlmReader(connection, [], dir.Combine("work")).ReadAsync("p", Png, CancellationToken.None));

        Assert.Equal(VlmFailureKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Cancelling_StopsTheReading()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var read = Reader(CommandKind.Codex, "hang", dir).ReadAsync("p", Png, cts.Token);

        await Task.Delay(1000);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public void TheName_IsTheUsualOne_WhenTheConnectionNamesNoExecutable()
    {
        var connection = Connection(CommandKind.OpenCode);
        connection.Executable = null;

        Assert.Equal("opencode", new CommandVlmReader(connection).Executable);
    }

    [Fact]
    public void FoldersThatAnEarlierRunLeftBehind_AreSweptWhenOld()
    {
        using var dir = new TempDir();
        var old = Path.Combine(dir.Path, CommandWorkFolder.Prefix + "old");
        var fresh = Path.Combine(dir.Path, CommandWorkFolder.Prefix + "fresh");
        var other = Path.Combine(dir.Path, "something-else");
        foreach (var folder in new[] { old, fresh, other })
        {
            Directory.CreateDirectory(folder);
        }

        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-2));

        var removed = CommandWorkFolder.Sweep(TimeSpan.FromDays(1), dir.Path);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(other)); // only the folders of this program
    }
}
