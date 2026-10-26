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

using System.Text.Json.Nodes;
using MarkPort.Core.Settings;
using MarkPort.Core.Vlm.Command;

namespace MarkPort.Core.Tests.Vlm.Command;

/// <summary>FR-044, FR-048, FR-053: the fixed form in which each command is called, and the isolation from the user's own settings.</summary>
public class AdapterTests
{
    private static readonly CommandJob Job = new("PROMPT 日本語", @"C:\work\run1\page.png", @"C:\work\run1", "MODEL", "low");

    private static CommandInvocation Build(CommandKind kind, CommandJob? job = null)
    {
        job ??= Job;
        if (kind == CommandKind.Claude && !File.Exists(job.ImagePath))
        {
            // claude gets the image inside its input, so the file has to be there.
            var image = Path.Combine(Path.GetTempPath(), "markport-adapter-test.png");
            File.WriteAllBytes(image, [1, 2, 3]);
            job = job with { ImagePath = image };
        }

        return CommandAdapters.For(kind).Build(job);
    }

    [Theory]
    [InlineData(CommandKind.Claude, "claude")]
    [InlineData(CommandKind.Codex, "codex")]
    [InlineData(CommandKind.OpenCode, "opencode")]
    public void EachCommand_HasItsUsualName(CommandKind kind, string name)
    {
        Assert.Equal(name, CommandAdapters.For(kind).DefaultExecutable);
    }

    [Fact]
    public void Claude_ReadsTheImageFromTheStandardInput_AndIsIsolated()
    {
        var call = Build(CommandKind.Claude);

        Assert.Equal(
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--restricted", "--strict-mcp-config", "--no-session-persistence", "--disable-slash-commands", "--model", "MODEL", "--effort", "low"],
            call.Arguments);
        Assert.DoesNotContain("--bare", call.Arguments); // it would not use the login (FR-043)
        Assert.DoesNotContain("--tools", call.Arguments); // it hangs
    }

    [Fact]
    public void Claude_GetsThePromptAndTheImageAsOneJsonLine()
    {
        var png = new byte[] { 1, 2, 3, 4 };
        using var dir = new Fixtures.TempDir();
        var image = dir.Combine("page.png");
        File.WriteAllBytes(image, png);

        var call = Build(CommandKind.Claude, Job with { ImagePath = image });

        var line = call.StandardInput!.TrimEnd('\n');
        Assert.DoesNotContain('\n', line);
        var content = JsonNode.Parse(line)!["message"]!["content"]!.AsArray();
        Assert.Equal("PROMPT 日本語", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("image", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", content[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(System.Convert.ToBase64String(png), content[1]!["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void Codex_IsIsolated_ReadOnly_AndTakesThePromptFromTheStandardInput()
    {
        var call = Build(CommandKind.Codex);

        Assert.Equal(
            ["exec", "-", "--json", "-i", @"C:\work\run1\page.png", "--ephemeral", "--ignore-user-config", "--ignore-rules", "-s", "read-only", "--skip-git-repo-check", "-C", @"C:\work\run1", "-o", @"C:\work\run1\last-message.txt", "-m", "MODEL", "-c", "model_reasoning_effort=low"],
            call.Arguments);
        Assert.Equal("PROMPT 日本語", call.StandardInput);
        Assert.Equal(@"C:\work\run1\last-message.txt", call.OutputFile);
    }

    [Fact]
    public void OpenCode_UsesAnAgentWithoutTools_ThroughTheEnvironment_AndAnAbsoluteImagePath()
    {
        var call = Build(CommandKind.OpenCode);

        Assert.Equal(
            ["run", "--pure", "--agent", "markport", "--format", "json", "--dir", @"C:\work\run1", "-f", @"C:\work\run1\page.png", "-m", "MODEL", "--variant", "low"],
            call.Arguments);
        Assert.Equal("PROMPT 日本語", call.StandardInput);

        var config = JsonNode.Parse(call.Environment["OPENCODE_CONFIG_CONTENT"]!)!;
        Assert.Equal("markport", config["default_agent"]!.GetValue<string>());
        var tools = config["agent"]!["markport"]!["tools"]!.AsObject();
        Assert.All(tools, t => Assert.False(t.Value!.GetValue<bool>(), t.Key));
        foreach (var name in new[] { "bash", "edit", "write", "read", "webfetch" })
        {
            Assert.True(tools.ContainsKey(name), name);
        }
    }

    [Theory]
    [InlineData(CommandKind.Claude)]
    [InlineData(CommandKind.Codex)]
    [InlineData(CommandKind.OpenCode)]
    public void AnEmptyModelAndEffort_AreLeftOut_SoTheDefaultsOfTheCommandApply(CommandKind kind)
    {
        var call = Build(kind, Job with { Model = "", Effort = "" });

        Assert.DoesNotContain("-m", call.Arguments);
        Assert.DoesNotContain("--model", call.Arguments);
        Assert.DoesNotContain("--effort", call.Arguments);
        Assert.DoesNotContain("--variant", call.Arguments);
        Assert.DoesNotContain(call.Arguments, a => a.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CommandKind.Claude)]
    [InlineData(CommandKind.Codex)]
    [InlineData(CommandKind.OpenCode)]
    public void TheArgumentsAreFixed_NothingFromTheUserIsAddedExceptTheModelAndEffort(CommandKind kind)
    {
        var plain = Build(kind).Arguments;
        var other = Build(kind, Job with { Model = "X", Effort = "high" }).Arguments;

        Assert.Equal(plain.Count, other.Count);
        Assert.Equal(2, plain.Zip(other).Count(pair => pair.First != pair.Second)); // the model and the effort only
    }

    [Theory]
    [InlineData("haiku", true)]
    [InlineData("gpt-6-luna", true)]
    [InlineData("openai/gpt-6-luna", true)]
    [InlineData("qwen3.6-35b-a3b@q4_k_s", true)]
    [InlineData("", true)]
    [InlineData("a b", false)]
    [InlineData("a&b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a|b", false)]
    [InlineData("%PATH%", false)]
    [InlineData("--yolo", false)]
    public void TheModelIsAWordWithoutSpecialCharacters_NotAnArgument(string model, bool valid)
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(new ConnectionProfile { Name = "c", Kind = ConnectionKind.Command, Command = CommandKind.Claude, Model = model });

        Assert.Equal(valid, settings.Validate().Count == 0);
    }

    [Theory]
    [InlineData(@"C:\Program Files\tool\claude.exe", true)]
    [InlineData("claude", true)]
    [InlineData("", true)]
    [InlineData("claude --dangerously-skip-permissions", false)]
    [InlineData("claude\"", false)]
    [InlineData("cl%aude", false)]
    public void TheExecutableIsAPathOrAName_NotACommandLine(string executable, bool valid)
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(new ConnectionProfile { Name = "c", Kind = ConnectionKind.Command, Command = CommandKind.Claude, Executable = executable });

        Assert.Equal(valid, settings.Validate().Count == 0);
    }
}
