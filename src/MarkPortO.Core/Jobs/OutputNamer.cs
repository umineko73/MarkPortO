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

using System.Text;

namespace MarkPortO.Core.Jobs;

/// <summary>
/// Turns a wanted name into a folder name that is valid on Windows and does not exist yet:
/// <c>name</c>, then <c>name (2)</c>, <c>name (3)</c> ... (FR-005, research R8).
/// </summary>
public static class OutputNamer
{
    /// <summary>Longest full path we create. Windows' limit is 260; the rest is kept as headroom.</summary>
    public const int MaxFullPathLength = 240;

    /// <summary>Room kept below the document folder for <c>\images\page-0000.png</c> and the Markdown file name.</summary>
    public const int ReservedBelowFolder = 40;

    private const int MinimumNameLength = 8;

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Replaces characters Windows does not allow, and avoids names Windows reserves.</summary>
    public static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(ch < 0x20 || Array.IndexOf(InvalidChars, ch) >= 0 ? '_' : ch);
        }

        // Windows silently drops trailing dots and spaces, which would make two names collide.
        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0)
        {
            return "_";
        }

        var stem = result.Split('.')[0].TrimEnd(' ');
        return ReservedDeviceNames.Contains(stem) ? "_" + result : result;
    }

    /// <summary>
    /// Hands out names one at a time and remembers them, so names given out in the same run
    /// never collide with each other or with what already exists on disk.
    /// </summary>
    public sealed class Allocator(Func<string, bool>? exists = null)
    {
        private readonly Func<string, bool> _exists = exists ?? (path => Directory.Exists(path) || File.Exists(path));
        private readonly HashSet<string> _handedOut = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns the full path of a folder that does not exist and has not been handed out yet.</summary>
        public string Allocate(string parentDirectory, string wantedName)
        {
            var name = Fit(Sanitize(wantedName), parentDirectory);
            for (var number = 1; ; number++)
            {
                var candidateName = number == 1 ? name : $"{name} ({number})";
                var candidate = Path.Combine(parentDirectory, candidateName);
                if (!_handedOut.Contains(candidate) && !_exists(candidate))
                {
                    _handedOut.Add(candidate);
                    return candidate;
                }
            }
        }

        private static string Fit(string name, string parentDirectory)
        {
            // " (999)" is the longest suffix we expect.
            const int suffixRoom = 6;
            var allowed = MaxFullPathLength - ReservedBelowFolder - suffixRoom - (parentDirectory.Length + 1);
            allowed = Math.Max(allowed, MinimumNameLength);
            if (name.Length <= allowed)
            {
                return name;
            }

            var cut = name[..allowed].TrimEnd('.', ' ');
            return cut.Length == 0 ? "_" : cut;
        }
    }
}
