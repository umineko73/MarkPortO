// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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

namespace MarkPort.Core.Engines;

/// <summary>Finds <c>soffice.exe</c> of LibreOffice (research R12).</summary>
public static class LibreOfficeLocator
{
    private static readonly string[] UsualPlaces =
    [
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
    ];

    /// <summary>
    /// The configured path if it exists, otherwise the usual installation folders, otherwise the folders on PATH.
    /// Returns <c>null</c> when LibreOffice is not found.
    /// </summary>
    public static string? Find(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        var usual = UsualPlaces.FirstOrDefault(File.Exists);
        if (usual is not null)
        {
            return usual;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), "soffice.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return null;
    }
}
