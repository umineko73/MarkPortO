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

namespace MarkPort.Core.IO;

/// <summary>
/// Writes files by writing to a temporary name first and then replacing the target,
/// so a failed or interrupted write never leaves a half-written file under the final name.
/// </summary>
public static class AtomicFile
{
    /// <summary>Suffix of temporary files. Files with this suffix are never a finished result.</summary>
    public const string TempSuffix = ".mptmp";

    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string content) =>
        Write(path, stream =>
        {
            var bytes = Utf8NoBom.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        });

    public static void WriteAllBytes(string path, byte[] content) =>
        Write(path, stream => stream.Write(content, 0, content.Length));

    public static void Write(string path, Action<Stream> writer)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        var tempPath = $"{fullPath}.{Guid.NewGuid():N}{TempSuffix}";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                writer(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Deletes leftover temporary files (from an interrupted run) under <paramref name="directory"/>.</summary>
    public static int DeleteStaleTemps(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + TempSuffix, SearchOption.AllDirectories))
        {
            if (TryDelete(file))
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
