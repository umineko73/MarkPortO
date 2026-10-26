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

using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;

namespace MarkPort.Core.Vlm.Command;

/// <summary>One page (or band) to read: the prompt, the image file and the folder the command runs in.</summary>
public sealed record CommandJob(string Prompt, string ImagePath, string WorkDirectory, string Model, string Effort);

/// <summary>How to call the command for a job (FR-044).</summary>
/// <param name="OutputFile">A file the command writes its last message to, if the form has one.</param>
public sealed record CommandInvocation(
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    IReadOnlyDictionary<string, string?> Environment,
    string? OutputFile);

public sealed record CommandAnswer(string Text, UsageInfo Usage, double? CostUsd);

/// <summary>What was found in the output: the answer, or the message of the command about what went wrong. Both null: nothing usable.</summary>
public sealed record ParsedOutput(CommandAnswer? Answer, string? Error);

/// <summary>The form in which one command is called and its output is read (<c>contracts/vlm-command.md</c>).</summary>
public interface ICommandAdapter
{
    CommandKind Kind { get; }

    /// <summary>The name looked up in the PATH when the connection names no executable.</summary>
    string DefaultExecutable { get; }

    CommandInvocation Build(CommandJob job);

    ParsedOutput Parse(CommandResult result, CommandInvocation invocation);
}

public static class CommandAdapters
{
    public static ICommandAdapter For(CommandKind kind) => kind switch
    {
        CommandKind.Claude => new ClaudeAdapter(),
        CommandKind.Codex => new CodexAdapter(),
        CommandKind.OpenCode => new OpenCodeAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal static class AdapterSupport
{
    public static readonly IReadOnlyDictionary<string, string?> NoEnvironment = new Dictionary<string, string?>();

    public static IEnumerable<System.Text.Json.Nodes.JsonObject> Lines(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith('{'))
            {
                continue;
            }

            System.Text.Json.Nodes.JsonObject? node = null;
            try
            {
                node = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                // a line that is not JSON is skipped
            }

            if (node is not null)
            {
                yield return node;
            }
        }
    }

    /// <summary>The message of the error, or the first line of the error output when the exit code says it failed without one.</summary>
    public static string? FallbackError(CommandResult result) =>
        result.ExitCode == 0
            ? null
            : result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
              ?? $"exit code {result.ExitCode}";

    public static int? Int(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<long>(out var number) ? (int)Math.Min(number, int.MaxValue) : null;

    public static double? Double(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    public static string? Text(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static UsageInfo Usage(int? prompt, int? completion)
    {
        int? total = prompt is null && completion is null ? null : (prompt ?? 0) + (completion ?? 0);
        return new UsageInfo(prompt, completion, total);
    }
}
