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

namespace MarkPort.Core.Tests.Fixtures;

/// <summary>The stand-in for the commands claude, codex and opencode (<c>tests/MarkPort.FakeVlmCommand</c>, FR-056).</summary>
public static class FakeVlm
{
    /// <summary>The path of the stand-in; it is copied next to the test assembly.</summary>
    public static string Exe => Path.Combine(AppContext.BaseDirectory, "MarkPort.FakeVlmCommand.exe");

    /// <summary>The calls the stand-in recorded in <paramref name="logFile"/>, oldest first.</summary>
    public static IReadOnlyList<JsonNode> Calls(string logFile) =>
        File.Exists(logFile)
            ? [.. File.ReadAllLines(logFile).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!)]
            : [];

    /// <summary>True while a process with this id is running.</summary>
    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
