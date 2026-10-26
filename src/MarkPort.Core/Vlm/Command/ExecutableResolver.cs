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

namespace MarkPort.Core.Vlm.Command;

/// <summary>
/// Finds the file to start for the name of a command: a path as it is, or a name looked up in the folders of the
/// <c>PATH</c> with the extensions of <c>PATHEXT</c> (the commands installed with npm are <c>.cmd</c> files).
/// </summary>
public static class ExecutableResolver
{
    private static readonly string[] DefaultExtensions = [".com", ".exe", ".bat", ".cmd"];

    public static string? Resolve(string nameOrPath, Func<string, string?>? readEnvironment = null)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return null;
        }

        readEnvironment ??= Environment.GetEnvironmentVariable;
        var name = nameOrPath.Trim().Trim('"');
        var extensions = (readEnvironment("PATHEXT") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .Where(e => e.StartsWith('.'))
            .ToArray();
        if (extensions.Length == 0)
        {
            extensions = DefaultExtensions;
        }

        var hasDirectory = name.Contains('\\') || name.Contains('/') || Path.IsPathRooted(name);
        var folders = hasDirectory
            ? [string.Empty]
            : (readEnvironment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var folder in folders)
        {
            var candidate = hasDirectory ? name : Path.Combine(folder.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            foreach (var extension in extensions)
            {
                if (File.Exists(candidate + extension))
                {
                    return Path.GetFullPath(candidate + extension);
                }
            }
        }

        return null;
    }

    /// <summary>True for a file that the command interpreter runs (<c>.cmd</c>, <c>.bat</c>).</summary>
    public static bool IsBatch(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
