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

using System.Text;
using System.Text.Json.Nodes;

namespace MarkPort.Core.Vlm.Command;

/// <summary>
/// <c>codex exec</c> with the prompt on the standard input, the image with <c>-i</c>, and the last message written to a
/// file. It runs without the settings and rules of the user, keeps no session, and may only read.
/// </summary>
public sealed class CodexAdapter : ICommandAdapter
{
    public Settings.CommandKind Kind => Settings.CommandKind.Codex;

    public string DefaultExecutable => "codex";

    public CommandInvocation Build(CommandJob job)
    {
        var outputFile = Path.Combine(job.WorkDirectory, "last-message.txt");
        var arguments = new List<string>
        {
            "exec", "-", "--json",
        };
        if (job.ImagePath is not null)
        {
            arguments.AddRange(["-i", job.ImagePath]);
        }

        arguments.AddRange(["--ephemeral", "--ignore-user-config", "--ignore-rules", "-s", "read-only", "--skip-git-repo-check", "-C", job.WorkDirectory, "-o", outputFile]);

        if (job.Model.Length > 0)
        {
            arguments.AddRange(["-m", job.Model]);
        }

        if (job.Effort.Length > 0)
        {
            arguments.AddRange(["-c", "model_reasoning_effort=" + job.Effort]);
        }

        return new CommandInvocation(arguments, job.Prompt, AdapterSupport.NoEnvironment, outputFile);
    }

    public ParsedOutput Parse(CommandResult result, CommandInvocation invocation)
    {
        string? lastMessage = null;
        string? error = null;
        Jobs.Models.UsageInfo usage = Jobs.Models.UsageInfo.Unavailable;

        foreach (var node in AdapterSupport.Lines(result.StandardOutput))
        {
            switch (AdapterSupport.Text(node["type"]))
            {
                case "item.completed" when node["item"] is JsonObject item && AdapterSupport.Text(item["type"]) == "agent_message":
                    lastMessage = AdapterSupport.Text(item["text"]);
                    break;
                case "turn.completed" when node["usage"] is JsonObject u:
                    usage = AdapterSupport.Usage(AdapterSupport.Int(u["input_tokens"]), AdapterSupport.Int(u["output_tokens"]));
                    break;
                case "error":
                    error ??= MessageOf(AdapterSupport.Text(node["message"]));
                    break;
                case "turn.failed" when node["error"] is JsonObject failure:
                    error ??= MessageOf(AdapterSupport.Text(failure["message"]));
                    break;
            }
        }

        if (invocation.OutputFile is { } file && File.Exists(file) && File.ReadAllText(file, Encoding.UTF8) is { Length: > 0 } fromFile)
        {
            lastMessage = fromFile;
        }

        if (error is not null || result.ExitCode != 0)
        {
            return new ParsedOutput(null, error ?? AdapterSupport.FallbackError(result));
        }

        return lastMessage is null ? new ParsedOutput(null, null) : new ParsedOutput(new CommandAnswer(lastMessage, usage, null), null);
    }

    /// <summary>The errors of codex are often a JSON text with the message inside.</summary>
    private static string? MessageOf(string? text)
    {
        if (text is null || !text.TrimStart().StartsWith('{'))
        {
            return text;
        }

        try
        {
            var node = JsonNode.Parse(text);
            return AdapterSupport.Text(node?["error"]?["message"]) ?? AdapterSupport.Text(node?["message"]) ?? text;
        }
        catch (System.Text.Json.JsonException)
        {
            return text;
        }
    }
}
