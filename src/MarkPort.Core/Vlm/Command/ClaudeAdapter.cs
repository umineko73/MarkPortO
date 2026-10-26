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

namespace MarkPort.Core.Vlm.Command;

/// <summary>
/// <c>claude -p</c> with the prompt and the image as one line of JSON on the standard input and the result as JSON lines.
/// <c>--restricted</c> removes the tools that run commands and ignores the settings files of the user (hooks, memory,
/// instructions) but keeps the login; <c>--bare</c> would not use the login (<c>contracts/vlm-command.md</c>).
/// </summary>
public sealed class ClaudeAdapter : ICommandAdapter
{
    public Settings.CommandKind Kind => Settings.CommandKind.Claude;

    public string DefaultExecutable => "claude";

    public CommandInvocation Build(CommandJob job)
    {
        var arguments = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--restricted", "--strict-mcp-config", "--no-session-persistence", "--disable-slash-commands",
        };
        if (job.Model.Length > 0)
        {
            arguments.AddRange(["--model", job.Model]);
        }

        if (job.Effort.Length > 0)
        {
            arguments.AddRange(["--effort", job.Effort]);
        }

        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = job.Prompt },
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = System.Convert.ToBase64String(File.ReadAllBytes(job.ImagePath)),
                        },
                    }),
            },
        };

        var line = message.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return new CommandInvocation(arguments, line + "\n", AdapterSupport.NoEnvironment, null);
    }

    public ParsedOutput Parse(CommandResult result, CommandInvocation invocation)
    {
        JsonObject? final = null;
        foreach (var node in AdapterSupport.Lines(result.StandardOutput))
        {
            if (AdapterSupport.Text(node["type"]) == "result")
            {
                final = node;
            }
        }

        if (final is null)
        {
            return new ParsedOutput(null, AdapterSupport.FallbackError(result));
        }

        var text = AdapterSupport.Text(final["result"]) ?? string.Empty;
        if ((final["is_error"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError) || result.ExitCode != 0)
        {
            return new ParsedOutput(null, text.Length > 0 ? text : AdapterSupport.FallbackError(result));
        }

        var usage = final["usage"] as JsonObject;
        var usageInfo = Jobs.Models.UsageInfo.Unavailable;
        if (usage is not null)
        {
            // The input tokens that came from the cache are input tokens too.
            var parts = new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" }.Select(k => AdapterSupport.Int(usage[k])).ToList();
            int? prompt = parts.Any(p => p.HasValue) ? parts.Sum(p => p ?? 0) : null;
            usageInfo = AdapterSupport.Usage(prompt, AdapterSupport.Int(usage["output_tokens"]));
        }

        return new ParsedOutput(new CommandAnswer(text, usageInfo, AdapterSupport.Double(final["total_cost_usd"])), null);
    }

}
