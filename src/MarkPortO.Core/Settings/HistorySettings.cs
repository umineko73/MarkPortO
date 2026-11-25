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

namespace MarkPortO.Core.Settings;

/// <summary>What the screen remembers of earlier conversions (FR-156). "Clear history" empties it.</summary>
public sealed class HistorySettings
{
    public const int MaxOutputs = 200;

    /// <summary>The output folder each input (a given file or folder) was last converted into, newest first.</summary>
    public List<OutputMemory> Outputs { get; set; } = [];

    /// <summary>The output folder remembered for the input; <c>null</c> when there is none.</summary>
    public string? OutputOf(string input)
    {
        var key = Normalize(input);
        return Outputs.FirstOrDefault(o => string.Equals(Normalize(o.Input), key, StringComparison.OrdinalIgnoreCase))?.Output;
    }

    /// <summary>Remembers the output folder of the input; the older memory of the input is replaced, and the oldest go past 200.</summary>
    public void Remember(string input, string output, DateTimeOffset at)
    {
        var key = Normalize(input);
        Outputs.RemoveAll(o => string.Equals(Normalize(o.Input), key, StringComparison.OrdinalIgnoreCase));
        Outputs.Insert(0, new OutputMemory { Input = key, Output = output, At = at });
        if (Outputs.Count > MaxOutputs)
        {
            Outputs.RemoveRange(MaxOutputs, Outputs.Count - MaxOutputs);
        }
    }

    /// <summary>The path as inputs are compared (FR-130): full, without a separator at the end; compared without case.</summary>
    public static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

/// <summary>An input and the output folder it was last converted into (FR-156).</summary>
public sealed class OutputMemory
{
    public string Input { get; set; } = string.Empty;

    public string Output { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
}

/// <summary>A work set (FR-159): the inputs, the output folder and the conditions, under a name.</summary>
public sealed class WorkSet
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The inputs, in their order.</summary>
    public List<string> Inputs { get; set; } = [];

    public string Output { get; set; } = string.Empty;

    public ConditionSet Conditions { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When it was opened last (the list shows the ones opened lately first); <c>null</c> when never.</summary>
    public DateTimeOffset? OpenedAt { get; set; }
}
