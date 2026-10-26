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
using MarkPort.Core.Vlm.Command;

namespace MarkPort.Core.Tests.Vlm.Command;

/// <summary>FR-047, FR-049: the answer, the usage and the cost are taken from what each command prints.</summary>
public class OutputParserTests
{
    private static readonly CommandInvocation NoFile = new([], null, new Dictionary<string, string?>(), null);

    private static ParsedOutput Parse(CommandKind kind, string stdout, string stderr = "", int exitCode = 0, CommandInvocation? invocation = null) =>
        CommandAdapters.For(kind).Parse(new CommandResult(exitCode, stdout, stderr), invocation ?? NoFile);

    // ---------------- claude

    private const string ClaudeOk = """
        {"type":"system","subtype":"init"}
        {"type":"assistant","message":{"content":[{"type":"text","text":"K7QM"}]}}
        {"type":"rate_limit_event"}
        {"type":"result","subtype":"success","is_error":false,"result":"K7QM","total_cost_usd":0.0305983,"usage":{"input_tokens":2,"cache_creation_input_tokens":100,"cache_read_input_tokens":50,"output_tokens":7}}
        """;

    [Fact]
    public void Claude_TheAnswerComesFromTheResultEvent()
    {
        var parsed = Parse(CommandKind.Claude, ClaudeOk);

        Assert.Equal("K7QM", parsed.Answer!.Text);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void Claude_TheInputTokensIncludeTheCache_AndTheCostIsRead()
    {
        var answer = Parse(CommandKind.Claude, ClaudeOk).Answer!;

        Assert.Equal(152, answer.Usage.PromptTokens);
        Assert.Equal(7, answer.Usage.CompletionTokens);
        Assert.Equal(159, answer.Usage.TotalTokens);
        Assert.Equal(0.0305983, answer.CostUsd);
    }

    [Fact]
    public void Claude_WithoutUsage_TheUsageIsUnavailable()
    {
        var answer = Parse(CommandKind.Claude, """{"type":"result","is_error":false,"result":"text"}""").Answer!;

        Assert.False(answer.Usage.IsAvailable);
        Assert.Null(answer.CostUsd);
    }

    [Fact]
    public void Claude_AnErrorResult_IsTheError()
    {
        var parsed = Parse(CommandKind.Claude, """{"type":"result","is_error":true,"result":"Not logged in. Please run login."}""", exitCode: 1);

        Assert.Null(parsed.Answer);
        Assert.Equal("Not logged in. Please run login.", parsed.Error);
    }

    // ---------------- codex

    private const string CodexOk = """
        {"type":"thread.started","thread_id":"t"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"first thought"}}
        {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"K7QM"}}
        {"type":"turn.completed","usage":{"input_tokens":15725,"cached_input_tokens":12416,"output_tokens":7}}
        """;

    [Fact]
    public void Codex_TheAnswerIsTheLastMessage()
    {
        Assert.Equal("K7QM", Parse(CommandKind.Codex, CodexOk).Answer!.Text);
    }

    [Fact]
    public void Codex_TheFileOfTheLastMessage_IsPreferred()
    {
        using var dir = new Fixtures.TempDir();
        var file = dir.Combine("last.txt");
        File.WriteAllText(file, "from the file");

        var parsed = Parse(CommandKind.Codex, CodexOk, invocation: NoFile with { OutputFile = file });

        Assert.Equal("from the file", parsed.Answer!.Text);
    }

    [Fact]
    public void Codex_UsageIsInputAndOutput_AndThereIsNoCost()
    {
        var answer = Parse(CommandKind.Codex, CodexOk).Answer!;

        Assert.Equal(15725, answer.Usage.PromptTokens);
        Assert.Equal(7, answer.Usage.CompletionTokens);
        Assert.Equal(15732, answer.Usage.TotalTokens);
        Assert.Null(answer.CostUsd);
    }

    [Fact]
    public void Codex_WithoutUsage_TheUsageIsUnavailable()
    {
        var answer = Parse(CommandKind.Codex, """
            {"type":"item.completed","item":{"type":"agent_message","text":"x"}}
            {"type":"turn.completed"}
            """).Answer!;

        Assert.False(answer.Usage.IsAvailable);
    }

    [Fact]
    public void Codex_AnErrorEvent_IsTheError_AndItsJsonMessageIsOpened()
    {
        var stdout = """
            {"type":"error","message":"{\"type\":\"error\",\"status\":400,\"error\":{\"message\":\"The 'gpt-4o' model is not supported when using Codex with a ChatGPT account.\",\"code\":\"model_not_found\"}}"}
            {"type":"turn.failed","error":{"message":"..."}}
            """;

        var parsed = Parse(CommandKind.Codex, stdout, exitCode: 1);

        Assert.Null(parsed.Answer);
        Assert.Contains("gpt-4o", parsed.Error);
        Assert.DoesNotContain("{", parsed.Error);
    }

    // ---------------- opencode

    private const string OpenCodeOk = """
        {"type":"step_start","part":{"type":"step-start"}}
        {"type":"text","part":{"type":"text","text":"K7QM"}}
        {"type":"step_finish","part":{"type":"step-finish","tokens":{"total":14782,"input":14775,"output":7,"reasoning":0},"cost":0}}
        """;

    [Fact]
    public void OpenCode_TheAnswerIsTheTextParts()
    {
        Assert.Equal("K7QM", Parse(CommandKind.OpenCode, OpenCodeOk).Answer!.Text);
    }

    [Fact]
    public void OpenCode_UsageIsTheTokensOfTheSteps_AndACostOfZeroIsNotACost()
    {
        var answer = Parse(CommandKind.OpenCode, OpenCodeOk).Answer!;

        Assert.Equal(14775, answer.Usage.PromptTokens);
        Assert.Equal(7, answer.Usage.CompletionTokens);
        Assert.Equal(14782, answer.Usage.TotalTokens);
        Assert.Null(answer.CostUsd);
    }

    [Fact]
    public void OpenCode_ACostThatIsReported_IsSummedOverTheSteps()
    {
        var stdout = """
            {"type":"text","part":{"text":"a"}}
            {"type":"step_finish","part":{"tokens":{"total":10,"input":8,"output":2},"cost":0.01}}
            {"type":"text","part":{"text":"b"}}
            {"type":"step_finish","part":{"tokens":{"total":20,"input":15,"output":5},"cost":0.02}}
            """;

        var answer = Parse(CommandKind.OpenCode, stdout).Answer!;

        Assert.Equal("ab", answer.Text);
        Assert.Equal(30, answer.Usage.TotalTokens);
        Assert.Equal(0.03, answer.CostUsd!.Value, 6);
    }

    [Fact]
    public void OpenCode_AnErrorEvent_IsTheError_WithoutTheResponseHeaders()
    {
        var stdout = """{"type":"error","error":{"name":"APIError","data":{"message":"The 'gpt-5.5' model is not supported.","statusCode":400,"responseHeaders":{"set-cookie":"__cflb=SECRET"}}}}""";

        var parsed = Parse(CommandKind.OpenCode, stdout, exitCode: 1);

        Assert.Equal("The 'gpt-5.5' model is not supported.", parsed.Error);
        Assert.DoesNotContain("SECRET", parsed.Error);
    }

    // ---------------- all

    [Theory]
    [InlineData(CommandKind.Claude)]
    [InlineData(CommandKind.Codex)]
    [InlineData(CommandKind.OpenCode)]
    public void Output_ThatIsNotWhatTheCommandPrints_GivesNeitherAnAnswerNorAnError(CommandKind kind)
    {
        var parsed = Parse(kind, "this is not json at all");

        Assert.Null(parsed.Answer);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData(CommandKind.Claude)]
    [InlineData(CommandKind.Codex)]
    [InlineData(CommandKind.OpenCode)]
    public void AFailingExitCode_WithoutAnyMessage_UsesTheFirstLineOfTheErrorOutput(CommandKind kind)
    {
        var parsed = Parse(kind, "", stderr: "fatal: something broke\nmore details\n", exitCode: 2);

        Assert.Null(parsed.Answer);
        Assert.Equal("fatal: something broke", parsed.Error);
    }
}
