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

namespace MarkPort.Core.Tests.Fixtures;

/// <summary>
/// Which converters are installed on this computer. Tests that need one are skipped (not failed) when it is missing,
/// so the same test run works on a computer without Microsoft Office (FR-027, constitution V).
/// </summary>
public static class InstalledEngines
{
    private static readonly Lazy<string?> LibreOffice = new(FindLibreOffice);
    private static readonly Lazy<bool> Office = new(IsOfficeInstalled);

    public static string? LibreOfficePath => LibreOffice.Value;

    public static bool HasLibreOffice => LibreOfficePath is not null;

    public static bool HasOffice => Office.Value;

    private static string? FindLibreOffice()
    {
        string[] candidates =
        [
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        ];
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "soffice.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is ignored.
            }
        }

        return null;
    }

    private static bool IsOfficeInstalled() =>
        Type.GetTypeFromProgID("Word.Application") is not null
        && Type.GetTypeFromProgID("Excel.Application") is not null
        && Type.GetTypeFromProgID("PowerPoint.Application") is not null;
}

/// <summary>A test that needs LibreOffice. Skipped when it is not installed.</summary>
public sealed class LibreOfficeFactAttribute : FactAttribute
{
    public LibreOfficeFactAttribute()
    {
        if (!InstalledEngines.HasLibreOffice)
        {
            Skip = "LibreOffice is not installed.";
        }
    }
}

/// <summary>A test that needs Microsoft Office (Word, Excel and PowerPoint). Skipped when it is not installed.</summary>
public sealed class OfficeFactAttribute : FactAttribute
{
    public OfficeFactAttribute()
    {
        if (!InstalledEngines.HasOffice)
        {
            Skip = "Microsoft Office is not installed.";
        }
    }
}
