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

using MarkPortO.Core.Convert;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using MarkPortO.Core.Vlm;
using MarkPortO.Core.Vlm.Command;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Pipeline;

/// <summary>
/// FR-043 to FR-049, FR-055, SC-023, SC-026: a whole conversion with a connection to a command, against the stand-in
/// command: the record of the connection, the stop on a usage limit and the resume after it.
/// </summary>
[Trait("Engine", "LibreOffice")]
public class Step3CommandTests
{
    private static ConnectionProfile Connection(bool consent = true)
    {
        var connection = new ConnectionProfile
        {
            Name = "cloud",
            Kind = ConnectionKind.Command,
            Command = CommandKind.Codex,
            Executable = FakeVlm.Exe,
            Model = "gpt-6-luna",
        };
        if (consent)
        {
            ExternalConsent.Grant(connection, DateTimeOffset.UtcNow);
        }

        return connection;
    }

    private static VlmSetup Setup(ConnectionProfile connection, TempDir dir, string mode, string? log = null)
    {
        var environment = new Dictionary<string, string?> { ["FAKE_VLM_MODE"] = mode };
        if (log is not null)
        {
            environment["FAKE_VLM_LOG"] = log;
        }

        var reader = new CommandVlmReader(connection, [], dir.Combine("work"), environment);
        return new VlmSetup(reader, new PromptSettings(), connection.Name, _ => Task.FromResult(new CheckResult(true, "ok")))
        {
            Connection = connection,
            CommandVersion = "fake-vlm 1.0.0",
        };
    }

    private static async Task<ConvertResult> RunAsync(TempDir dir, VlmSetup setup, string source)
    {
        using var engine = Engine(dir);
        return await new ConvertService(engine, vlm: setup).RunAsync(Request(dir.Combine("out"), Options(step3: true, connection: "cloud"), source));
    }

    [LibreOfficeFact]
    public async Task EveryPageIsRead_AndTheJobRecordsWhatItUsed_FR055()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"]);

        var result = await RunAsync(dir, Setup(Connection(), dir, "ok"), source);

        Assert.True(result.Documents[0].IsDone());
        Assert.Equal(JobStatus.Completed, result.Job.Status);
        Assert.All(result.Documents[0].Pages, p => Assert.Equal(PageStep3Status.Done, p.Step3));
        var used = result.Job.Step3Connection!;
        Assert.Equal("command", used.Kind);
        Assert.Equal("codex", used.Command);
        Assert.Equal("fake-vlm 1.0.0", used.CommandVersion);
        Assert.Equal("gpt-6-luna", used.Model);
        Assert.Equal(2, result.Job.Step3Totals.Pages);
        Assert.Null(result.Job.StopReason);
    }

    [LibreOfficeFact]
    public async Task AUsageLimit_InterruptsTheJob_WithTheReason_AndAResumeReadsTheRest_SC026()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"], ["three"]);
        var log = dir.Combine("log.jsonl");

        var first = await RunAsync(dir, Setup(Connection(), dir, "usagelimit", log), source);

        Assert.Equal(JobStatus.Interrupted, first.Job.Status);
        Assert.Contains("usage limit", first.Job.StopReason);
        Assert.Single(FakeVlm.Calls(log)); // no second command was started
        Assert.All(first.Documents[0].Pages, p => Assert.Equal(PageStep3Status.Pending, p.Step3));
        Assert.False(first.Documents[0].IsDone());

        // The limit is lifted: the resume reads the three pages, once each.
        var secondLog = dir.Combine("log2.jsonl");
        using var engine = Engine(dir);
        var second = await new ConvertService(engine, vlm: Setup(Connection(), dir, "ok", secondLog))
            .ResumeAsync(new ResumeRequest { OutputFolder = dir.Combine("out") });

        Assert.Equal(JobStatus.Completed, second.Job.Status);
        Assert.Equal(3, FakeVlm.Calls(secondLog).Count);
        Assert.Null(second.Job.StopReason);
        Assert.True(second.Documents[0].IsDone());
    }

    [LibreOfficeFact]
    public async Task WithoutTheConsent_TheConversionDoesNotStart_AndNothingIsWritten_SC023()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);
        var log = dir.Combine("log.jsonl");
        var settings = AppSettings.CreateDefault();
        var connection = Connection(consent: false);
        connection.Executable = FakeVlm.Exe;
        settings.Vlm.Connections.Add(connection);
        settings.Vlm.Selected = "cloud";
        var (setup, _) = VlmSetupFactory.Create(settings);

        using var engine = Engine(dir);
        var error = await Assert.ThrowsAsync<ConvertStartException>(() =>
            new ConvertService(engine, vlm: setup).RunAsync(Request(dir.Combine("out"), Options(step3: true, connection: "cloud"), source)));

        Assert.Contains("consent", error.Message);
        Assert.Empty(FakeVlm.Calls(log));
        Assert.False(Directory.Exists(dir.Combine("out")) && Directory.EnumerateFiles(dir.Combine("out"), "*.md", SearchOption.AllDirectories).Any());
    }

    [LibreOfficeFact]
    public async Task TheCostOfTheJob_IsAddedUp()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"]);
        var connection = Connection();
        connection.Command = CommandKind.Claude; // the stand-in answers like claude, with a cost of 0.0123

        var result = await RunAsync(dir, Setup(connection, dir, "ok"), source);

        Assert.Equal(0.0246, result.Job.Step3Totals.CostUsd, 6);
        Assert.Equal(0.0246, result.Documents[0].CostTotalUsd!.Value, 6);
    }
}
