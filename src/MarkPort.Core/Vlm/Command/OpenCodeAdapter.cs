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
/// <c>opencode run</c> with the prompt on the standard input and the image with <c>-f</c> (a full path). The agent
/// <c>markport</c> has no tools; it is passed in the environment, so the settings of the user do not decide which agent
/// runs (a user's default agent that does not exist made the run fail).
/// </summary>
public sealed class OpenCodeAdapter : ICommandAdapter
{
    internal const string AgentConfiguration =
        """{"agent":{"markport":{"mode":"primary","description":"reads text from an image","prompt":"Read the text of the image. Do not use tools.","tools":{"bash":false,"edit":false,"write":false,"read":false,"glob":false,"grep":false,"list":false,"patch":false,"webfetch":false,"task":false,"todowrite":false,"todoread":false},"permission":{"bash":"deny","edit":"deny","webfetch":"deny","external_directory":"deny"}}},"default_agent":"markport"}""";

    public Settings.CommandKind Kind => Settings.CommandKind.OpenCode;

    public string DefaultExecutable => "opencode";

    public CommandInvocation Build(CommandJob job)
    {
        var arguments = new List<string>
        {
            "run", "--pure", "--agent", "markport", "--format", "json", "--dir", job.WorkDirectory,
        };
        if (job.ImagePath is not null)
        {
            arguments.AddRange(["-f", job.ImagePath]);
        }

        if (job.Model.Length > 0)
        {
            arguments.AddRange(["-m", job.Model]);
        }

        if (job.Effort.Length > 0)
        {
            arguments.AddRange(["--variant", job.Effort]);
        }

        var environment = new Dictionary<string, string?> { ["OPENCODE_CONFIG_CONTENT"] = AgentConfiguration };
        return new CommandInvocation(arguments, job.Prompt, environment, null);
    }

    public ParsedOutput Parse(CommandResult result, CommandInvocation invocation)
    {
        var text = new StringBuilder();
        string? error = null;
        int? prompt = null, completion = null;
        double cost = 0;
        var anyTokens = false;

        foreach (var node in AdapterSupport.Lines(result.StandardOutput))
        {
            var part = node["part"] as JsonObject;
            switch (AdapterSupport.Text(node["type"]))
            {
                case "text" when part is not null:
                    text.Append(AdapterSupport.Text(part["text"]));
                    break;
                case "step_finish" when part is not null:
                    if (part["tokens"] is JsonObject tokens)
                    {
                        anyTokens = true;
                        prompt = (prompt ?? 0) + (AdapterSupport.Int(tokens["input"]) ?? 0);
                        completion = (completion ?? 0) + (AdapterSupport.Int(tokens["output"]) ?? 0);
                    }

                    cost += AdapterSupport.Double(part["cost"]) ?? 0;
                    break;
                case "error":
                    error ??= AdapterSupport.Text(node["error"]?["data"]?["message"]) ?? AdapterSupport.Text(node["error"]?["message"]) ?? AdapterSupport.Text(node["error"]?["name"]);
                    break;
            }
        }

        if (error is not null || result.ExitCode != 0)
        {
            return new ParsedOutput(null, error ?? AdapterSupport.FallbackError(result));
        }

        if (text.Length == 0)
        {
            return new ParsedOutput(null, null);
        }

        // opencode prints a cost of 0 when it does not price the model (a login instead of a key): that is not a cost.
        var usage = anyTokens ? AdapterSupport.Usage(prompt, completion) : Jobs.Models.UsageInfo.Unavailable;
        return new ParsedOutput(new CommandAnswer(text.ToString(), usage, cost > 0 ? cost : null), null);
    }
}
