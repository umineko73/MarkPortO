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
/// The empty folder a command runs in, made for each call and deleted afterwards (FR-044, FR-050). The image is copied
/// into it, so the original is never touched and the command has nothing else to look at.
/// </summary>
public sealed class CommandWorkFolder : IDisposable
{
    public const string Prefix = "markport-cmd-";

    private CommandWorkFolder(string path) => Path = path;

    public string Path { get; }

    /// <summary>The copy of the image; null when the folder was made without one (the prompt only).</summary>
    public string? ImagePath { get; private set; }

    public static CommandWorkFolder Create(byte[] png, string? parent = null)
    {
        var path = System.IO.Path.Combine(parent ?? System.IO.Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var folder = new CommandWorkFolder(path);
        if (png.Length > 0)
        {
            folder.ImagePath = System.IO.Path.Combine(path, "page.png");
            File.WriteAllBytes(folder.ImagePath, png);
        }

        return folder;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A process may still hold a file; the folder is removed by the next sweep.
        }
    }

    /// <summary>Deletes the folders that a run that ended abnormally left behind.</summary>
    public static int Sweep(TimeSpan olderThan, string? parent = null)
    {
        var removed = 0;
        var root = parent ?? System.IO.Path.GetTempPath();
        if (!Directory.Exists(root))
        {
            return 0;
        }

        foreach (var folder in Directory.EnumerateDirectories(root, Prefix + "*"))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > olderThan)
                {
                    Directory.Delete(folder, recursive: true);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use: left for later
            }
        }

        return removed;
    }
}
